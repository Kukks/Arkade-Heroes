using System.Security.Cryptography;
using ArkadeHeroes.Chain;
using ArkadeHeroes.Core;
using ArkadeHeroes.Core.Combat;
using ArkadeHeroes.Core.Fairness;
using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Heroes;
using ArkadeHeroes.Core.Progression;
using Microsoft.Extensions.Options;

namespace ArkadeHeroes.Server;

/// <summary>A rule violation surfaced to the client as HTTP 400.</summary>
public class GameRuleException(string message) : Exception(message);

/// <summary>
/// Orchestrates game flows under the non-custodial mandate: players register
/// their own wallet's Arkade address; every fee/stake is an invoice the
/// player's wallet pays and the server verifies on-chain; the treasury signs
/// only its own outputs (mints, item deliveries, payouts); asset ownership is
/// checked against the chain, never against server records alone.
/// </summary>
public class GameService(
    GameStore store, IChainService chain, ReceiptSigner receipts, IOptions<GameOptions> options,
    Persistence.IGameStatePersistence persistence, ILogger<GameService>? logger = null)
{
    private readonly GameOptions _options = options.Value;

    // The game-balance config the server runs under (economy from GameOptions; the rest from
    // GameConfig.Default unless retuned here).
    private readonly GameConfig _config = options.Value.ToGameConfig();

    // The version id of _config — STAMPED onto every outcome this server resolves, so a client verifies a
    // replay under the rules it actually ran on instead of guessing at its own compiled-in GameConfig.Default.
    // Derived from the SAME _config instance the resolvers fight under (never recomputed from options), so
    // the stamp and the rules can never name different things. Cached on first use; a benign race recomputes
    // the same value.
    private string? _configVersion;

    /// <summary>The version id of the rules this server resolves under — the stamp for an outcome whose
    /// response is built in the SAME request that resolved it (gauntlet, trials, death-match, fight). A
    /// replay served later must read the stamp RECORDED on its own session at resolve time instead, so it
    /// stays true to the fight even if the running config ever becomes reloadable.</summary>
    public string ConfigVersion => _configVersion ??= GameConfigVersion.Compute(_config);

    /// <summary>The rules this server resolves under, for <c>GET /api/config/{version}</c>.</summary>
    public GameConfig Config => _config;

    /// <summary>The CONTENT this server resolves under — the gear and dungeons compiled into this build.
    /// Served by <c>GET /api/content/{version}</c>.</summary>
    public ArkadeHeroes.Core.Content.ContentPack Content => ArkadeHeroes.Core.Content.ContentPack.Default;

    /// <summary>The version id of that content — STAMPED onto every outcome this server resolves, beside
    /// the config stamp. Item stats feed combat, so a verifier that replayed a match against DIFFERENT gear
    /// than it was resolved under would disagree with an honest server and print "SERVER CHEATED". Cached
    /// on the immutable default pack; a benign race recomputes the same value.</summary>
    public string ContentVersion => ArkadeHeroes.Core.Content.ContentPackVersion.Default;

    private Shared.ProgressionReceiptDto IssueReceipt(Shared.ProgressionReceiptDto unsigned, params string[] heroIds)
    {
        var receipt = receipts.Issue(unsigned);
        foreach (var heroId in heroIds)
            store.ReceiptsByHero.AddOrUpdate(heroId,
                _ => [receipt],
                (_, list) => { lock (list) { list.Add(receipt); } return list; });
        return receipt;
    }

    private static string NewId(string prefix)
        => $"{prefix}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant()}";

    // ── Players ────────────────────────────────────────────────────────

    /// <summary>How long a login nonce is valid after issuance.</summary>
    private static readonly TimeSpan LoginNonceTtl = TimeSpan.FromMinutes(5);

    public async Task<(Player Player, string Address, long Balance)> RegisterPlayerAsync(
        string name, string arkadeAddress, string? loginPubKeyHex,
        string? nonceHex, string? signatureHex, CancellationToken ct,
        int? acceptedTermsVersion = null)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new GameRuleException("Player name is required.");
        if (string.IsNullOrWhiteSpace(arkadeAddress))
            throw new GameRuleException("Your wallet's Arkade address is required — keys stay on your side.");

        string? loginKey = null;
        if (!string.IsNullOrWhiteSpace(loginPubKeyHex))
        {
            loginKey = loginPubKeyHex.Trim().ToLowerInvariant();
            // Proof-of-possession: you may only register a login key you actually
            // control — sign a fresh server challenge with it. Without this, an
            // attacker could bind a VICTIM's login pubkey (paired with their own
            // address) to their own player and hijack the victim's later sign-in.
            if (string.IsNullOrWhiteSpace(nonceHex) || string.IsNullOrWhiteSpace(signatureHex))
                throw new GameRuleException("Registering a login key requires proof of possession (a signed challenge).");
            ConsumeAndVerifyChallenge(loginKey, nonceHex, signatureHex);
            // Uniqueness: one player per login key, so sign-in is unambiguous.
            if (store.Players.Values.Any(p =>
                    string.Equals(p.LoginPubKeyHex, loginKey, StringComparison.OrdinalIgnoreCase)))
                throw new GameRuleException("This wallet is already registered — use 'login' to resume it.");
        }

        // Validate the claimed acceptance BEFORE the player exists: a registration carrying a garbage
        // version must fail outright rather than create a player whose acceptance record is nonsense.
        if (acceptedTermsVersion is int claimed && !Shared.Terms.IsAcceptableVersion(claimed))
            throw new GameRuleException(
                $"Unknown Terms of Use version {claimed} — the current version is {Shared.Terms.CurrentVersion}.");

        var player = new Player
        {
            Id = NewId("player"),
            Name = name.Trim(),
            Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
            LoginPubKeyHex = loginKey,
            // Recorded in the SAME call that creates the player, so there is no window in which a player
            // row exists with no acceptance on file even though the player did accept.
            TermsAcceptedVersion = acceptedTermsVersion,
            TermsAcceptedAtUtc = acceptedTermsVersion is null ? null : DateTimeOffset.UtcNow,
        };

        try
        {
            await chain.RegisterPlayerAddressAsync(player.Id, arkadeAddress.Trim(), ct);
        }
        catch (InvalidOperationException ex)
        {
            throw new GameRuleException(ex.Message);
        }

        store.Players[player.Id] = player;
        store.PlayersByToken[player.Token] = player;
        await persistence.SavePlayerAsync(player, ct);   // identity is the anchor everything else references
        var balance = await chain.GetAddressBalanceSatsAsync(player.Id, ct);
        return (player, arkadeAddress.Trim(), balance);
    }

    public Player Authenticate(string? token)
    {
        if (token is not null && store.PlayersByToken.TryGetValue(token, out var player))
            return player;
        throw new GameRuleException("Invalid or missing bearer token.");
    }

    /// <summary>Issues a fresh single-use login nonce (and prunes expired ones).</summary>
    public string IssueLoginChallenge()
    {
        var cutoff = DateTimeOffset.UtcNow - LoginNonceTtl;
        foreach (var (nonce, issued) in store.LoginNonces)
            if (issued < cutoff) store.LoginNonces.TryRemove(nonce, out _);

        var fresh = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        store.LoginNonces[fresh] = DateTimeOffset.UtcNow;
        return fresh;
    }

    /// <summary>
    /// "Sign in with your wallet": consumes the single-use nonce, verifies the
    /// BIP340 signature over its domain-separated digest, and returns the player
    /// registered with that login key — so a restored wallet resumes its existing
    /// heroes without the server ever holding a key.
    /// </summary>
    public Player Login(string loginPubKeyHex, string nonceHex, string signatureHex)
    {
        var key = string.IsNullOrWhiteSpace(loginPubKeyHex) ? "" : loginPubKeyHex.Trim().ToLowerInvariant();
        ConsumeAndVerifyChallenge(key, nonceHex, signatureHex);

        // SingleOrDefault, not FirstOrDefault: registration enforces one player per
        // login key, so a duplicate would be a broken invariant — fail closed
        // (throw) rather than silently pick one and confuse accounts.
        try
        {
            return store.Players.Values.SingleOrDefault(p =>
                    string.Equals(p.LoginPubKeyHex, key, StringComparison.OrdinalIgnoreCase))
                ?? throw new GameRuleException("No player is registered with this login key.");
        }
        catch (InvalidOperationException)
        {
            throw new GameRuleException("This login key is ambiguous — sign-in refused.");
        }
    }

    /// <summary>
    /// Consumes a single-use challenge nonce (whatever happens next) and verifies
    /// the BIP340 signature over its digest proves control of <paramref name="pubKeyHex"/>.
    /// Shared by login and by registration's proof-of-possession.
    /// </summary>
    private void ConsumeAndVerifyChallenge(string pubKeyHex, string nonceHex, string signatureHex)
    {
        if (string.IsNullOrWhiteSpace(nonceHex) || !store.LoginNonces.TryRemove(nonceHex, out var issued))
            throw new GameRuleException("Unknown or already-used challenge — request a fresh one.");
        if (DateTimeOffset.UtcNow - issued > LoginNonceTtl)
            throw new GameRuleException("The challenge expired — request a fresh one.");
        if (!VerifyLoginSignature(pubKeyHex, nonceHex, signatureHex))
            throw new GameRuleException("Signature does not prove control of this login key.");
    }

    private static bool VerifyLoginSignature(string pubKeyHex, string nonceHex, string sigHex)
    {
        try
        {
            var digest = Shared.LoginChallenge.Digest(nonceHex); // 32-byte message
            return NBitcoin.Secp256k1.ECXOnlyPubKey.TryCreate(Convert.FromHexString(pubKeyHex), out var pk) && pk is not null
                && NBitcoin.Secp256k1.SecpSchnorrSignature.TryCreate(Convert.FromHexString(sigHex), out var sig) && sig is not null
                && pk.SigVerifyBIP340(sig, digest);
        }
        catch { return false; }
    }

    // ── Terms of Use: the explicit, versioned, server-recorded acceptance ──

    /// <summary>
    /// Records that this player explicitly accepted the Terms of Use at <paramref name="version"/>.
    ///
    /// The version is validated against <see cref="Shared.Terms"/> first: zero (what a missing JSON field
    /// deserialises into), negatives, and versions that do not exist yet are all REFUSED. Recording an
    /// unchecked number would be worse than recording nothing — a stored "accepted v9999" would silently
    /// satisfy every future bump, so the player would never be re-asked about terms they never read.
    ///
    /// Monotonic: an older acceptance arriving late (a stale tab that still thinks v1 is current, posting
    /// after the player already accepted v2 elsewhere) must not walk the record backwards and re-prompt.
    /// </summary>
    public async Task<Player> AcceptTermsAsync(Player player, int version, CancellationToken ct)
    {
        if (!Shared.Terms.IsAcceptableVersion(version))
            throw new GameRuleException(
                $"Unknown Terms of Use version {version} — the current version is {Shared.Terms.CurrentVersion}.");

        if (player.TermsAcceptedVersion is int already && already >= version)
            return player;   // already covered; nothing to record and nothing to walk back

        player.TermsAcceptedVersion = version;
        player.TermsAcceptedAtUtc = DateTimeOffset.UtcNow;
        // Durably, and awaited inline: browser-local storage is one cache clear from gone and is the
        // player's own machine, so the row IS the evidence that the disclosure was made and agreed to.
        await persistence.SavePlayerAsync(player, ct);
        return player;
    }

    /// <summary>What is on file for this player, and what the server currently requires.</summary>
    public Shared.TermsAcceptanceDto TermsFor(Player player) => new(
        player.TermsAcceptedVersion, player.TermsAcceptedAtUtc,
        Shared.Terms.CurrentVersion, _options.RequireTermsAcceptance);

    /// <summary>
    /// Refuses an irreversible entry point until the player's recorded acceptance covers the current terms.
    /// OPT-IN (<c>Game:RequireTermsAcceptance</c>, default off) because the browser gate already stops a
    /// player reaching this, and turning it on unconditionally would break every API client that never
    /// showed a terms screen. A deployment that stakes real bitcoin turns it on.
    ///
    /// Called from every operation that STAKES SATS or DESTROYS/ESCROWS AN ASSET — death-match open+accept
    /// (permadeath), duel and squad open+accept (wagers), merge (burns both inputs), breed, tournament
    /// open+join (buy-ins), hero listing, gauntlet entry, item purchase — and from the starter claim, the
    /// first mint. Guarding the starter claim ALONE would gate almost nothing: every player registered
    /// before this feature already has StarterClaimed set, so they would sail past the only check and go
    /// straight to burning a hero. Read-only and reversible paths (spar, trials, equip, profile) are
    /// deliberately not gated — nothing there can cost a player anything.
    /// </summary>
    private void RequireAcceptedTerms(Player player)
    {
        if (!_options.RequireTermsAcceptance) return;
        if (!Shared.Terms.Satisfies(player.TermsAcceptedVersion))
            throw new GameRuleException(
                "You must accept the Terms of Use before playing — this game stakes real bitcoin and can destroy assets permanently.");
    }

    // ── Heroes ─────────────────────────────────────────────────────────

    public Hero GetHero(string heroId)
        => store.Heroes.TryGetValue(heroId, out var hero)
            ? hero
            : throw new GameRuleException($"Unknown hero '{heroId}'.");

    private Hero GetOwnedHero(Player player, string heroId)
    {
        var hero = GetHero(heroId);
        if (hero.OwnerId != player.Id)
            throw new GameRuleException($"Hero '{hero.Name}' does not belong to you.");
        return hero;
    }

    // ── Unique-name registry: claim a custom, globally-unique hero name (a treasury sats sink) ──

    /// <summary>
    /// Requests a custom, globally-unique name for one of the player's heroes. Validates the format +
    /// uniqueness, then (if a fee is set) bills a treasury fee-invoice the player pays from their wallet;
    /// the claim is applied by <see cref="ConfirmRenameAsync"/> once it clears. Returns null when free.
    /// </summary>
    public async Task<FeeInvoice?> RequestRenameAsync(Player player, string heroId, string name, CancellationToken ct)
    {
        GetOwnedHero(player, heroId); // ownership check
        if (Core.Progression.NameRegistry.Validate(name, out var normalized) is { } error)
            throw new GameRuleException(error);
        if (NameTaken(normalized, heroId))
            throw new GameRuleException($"The name '{normalized}' is already taken by another hero.");

        // A previous attempt can lose the apply-time uniqueness race AFTER its fee has cleared
        // (ConfirmRenameAsync re-checks, throws, and leaves the session standing). That fee bought a
        // rename that never happened, so reuse the paid invoice rather than billing again: one paid
        // fee buys one APPLIED rename, however many names the player has to try. It cannot be milked
        // — the session is removed on success, so the invoice stops being reusable the moment a
        // rename lands.
        if (store.Renames.TryGetValue(heroId, out var prior)
            && prior.FeeInvoiceId is { } priorInvoice
            && await chain.IsInvoicePaidAsync(priorInvoice, ct))
        {
            store.Renames[heroId] = new RenameSession { HeroId = heroId, NewName = normalized, FeeInvoiceId = priorInvoice };
            return null;   // already paid for — nothing further to settle before confirming
        }

        var fee = _options.HeroRenameFeeSats > 0
            ? await chain.CreateFeeInvoiceAsync($"rename:{heroId}", _options.HeroRenameFeeSats, ct)
            : null;
        store.Renames[heroId] = new RenameSession { HeroId = heroId, NewName = normalized, FeeInvoiceId = fee?.InvoiceId };
        return fee;
    }

    /// <summary>Applies a pending rename once its treasury fee has cleared (or immediately when free).</summary>
    public async Task<Hero> ConfirmRenameAsync(Player player, string heroId, CancellationToken ct)
    {
        var hero = GetOwnedHero(player, heroId);
        if (!store.Renames.TryGetValue(heroId, out var pending))
            throw new GameRuleException("No pending rename — request one first.");
        if (pending.FeeInvoiceId is not null && !await chain.IsInvoicePaidAsync(pending.FeeInvoiceId, ct))
            throw new GameRuleException("The rename fee invoice has not been paid yet — pay it from your wallet, then confirm.");
        if (pending.FeeInvoiceId is not null)
            await store.RecordInflowAsync(pending.FeeInvoiceId, "rename", _options.HeroRenameFeeSats, ct);

        // Keyed on the NAME, not the player: the name is the contended resource, and it is the thing the
        // registry promises is globally unique. Without this the re-check below is a check-then-act — two
        // confirms for the same name both read "free" before either assigns, and both then take it. That
        // is not theoretical: released on one barrier, 64 concurrent confirms handed ONE name to TWO heroes,
        // durably (each writes its own SaveHeroAsync), and at a non-zero rename fee both owners had paid
        // for it. Different names still confirm in parallel — only same-name confirms serialize.
        using var gate = await store.LockAsync($"rename:{pending.NewName.ToLowerInvariant()}", ct);

        // Re-check uniqueness at apply time — another hero may have claimed the name since the request.
        if (NameTaken(pending.NewName, heroId))
            throw new GameRuleException($"The name '{pending.NewName}' was claimed before you confirmed — pick another.");

        hero.Name = pending.NewName;
        store.Renames.TryRemove(heroId, out _);
        // RENAME is an identity event: the name was bought (a real-sats fee) and is globally unique —
        // losing it to a crash would both refund nothing and free the name for someone else to claim.
        await persistence.SaveHeroAsync(hero, ct);
        return hero;
    }

    /// <summary>True if any OTHER hero already holds this name (case-insensitive) — the global registry.</summary>
    private bool NameTaken(string name, string exceptHeroId) =>
        store.Heroes.Values.Any(h => h.Id != exceptHeroId && string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The player's season-pass standing — scored from their own signed receipts inside the current
    /// season window, so it's derived rather than tracked, and a client holding those receipts recomputes it.</summary>
    public Shared.SeasonPassProgress SeasonPassFor(Player player)
    {
        var window = Season.Current(DateTimeOffset.UtcNow, _config.SeasonLengthDays);
        var from = window.Start.ToUnixTimeSeconds();
        var to = window.End.ToUnixTimeSeconds();
        var myHeroIds = store.Heroes.Values.Where(h => h.OwnerId == player.Id).Select(h => h.Id).ToHashSet();

        // A match receipt is filed under BOTH heroes, so dedupe by receipt id or a duel would score twice.
        var inWindow = store.ReceiptsByHero.Values.SelectMany(list => list)
            .DistinctBy(r => r.Id)
            .Where(r => r.UnixSeconds >= from && r.UnixSeconds < to)
            .ToList();

        return Shared.SeasonPass.Progress(inWindow, myHeroIds);
    }

    /// <summary>A player's derived accomplishments — computed from their current roster + resolved tournaments,
    /// with a badge unlocked at each milestone. A pure read over in-memory state; no per-event tracking needed.</summary>
    public Shared.PlayerAchievementsDto PlayerAchievements(Player player)
    {
        var mine = store.Heroes.Values.Where(h => h.OwnerId == player.Id).ToList();
        var owned = mine.Count;
        var bred = mine.Count(h => h.Generation > 0);
        var legendaries = mine.Count(h => Core.Progression.Rarity.Of(h.Genome).Tier.ToString() == "Legendary");
        var fancies = mine.Count(h => Core.Progression.FancySets.TitleFor(h.Genome) is not null);
        var fancySetsOwned = mine.Select(h => Core.Progression.FancySets.TitleFor(h.Genome))
            .Where(t => t is not null).Select(t => t!).Distinct().ToList();
        var traitAlbum = Core.Progression.TraitAlbum.CoverageByCategory(mine.Select(h => h.Genome))
            .ToDictionary(kv => kv.Key.ToString(), kv => kv.Value);
        var myHeroIds = mine.Select(h => h.Id).ToHashSet();
        var tournamentsWon = store.Tournaments.Values.Count(t => t.Result?.ChampionId is { } champ && myHeroIds.Contains(champ));
        // Discovering a Fancy set is a permanent DEED, recorded once to its first finder — NOT a function
        // of who holds the #1-edition hero now. Reading it from current holdings made the badge buyable
        // (acquire a #1 → inherit it) and revocable (sell your discovery → lose it).
        var discoveredAFancySet = store.FancyDiscoveries.Values.Any(d => d.OwnerId == player.Id);

        // The player's Fancy heroes with their edition numbers, rarest (lowest edition) first.
        var fancyEditions = mine
            .Select(h => store.FancyEditionByHero.TryGetValue(h.Id, out var e)
                ? new Shared.FancyEditionDto(h.Id, h.Name, e.Title, e.Edition)
                : null)
            .Where(e => e is not null).Select(e => e!)
            .OrderBy(e => e.Edition).ThenBy(e => e.Title, StringComparer.Ordinal)
            .ToList();

        var badges = new List<string>();
        if (owned >= 5) badges.Add("Collector");
        if (bred >= 3) badges.Add("Breeder");
        if (legendaries >= 1) badges.Add("Legend-keeper");
        if (fancies >= 1) badges.Add("Fancier");
        if (tournamentsWon >= 1) badges.Add("Champion");
        // First to breed a named set, ever — credited to the discoverer for good, even if the hero is sold on.
        if (discoveredAFancySet) badges.Add("Trailblazer");

        return new Shared.PlayerAchievementsDto(
            owned, bred, legendaries, fancies, tournamentsWon, badges, fancySetsOwned, traitAlbum, fancyEditions);
    }

    /// <summary>How many of a player's best heroes a public profile puts on display.</summary>
    private const int NotableHeroes = 3;

    /// <summary>A player's public trophy case: their season standing, achievements, and a few best heroes.
    /// Pure composition of the two views they can already see of themselves — no new derivation, and
    /// nothing here that isn't safe for the whole arena to read.</summary>
    public Shared.PlayerProfileDto ProfileFor(Player player)
    {
        // Rarity first (the thing the game brags about), then level. Ties break on id so the
        // display order is stable across calls rather than however the dictionary enumerated.
        var notable = store.Heroes.Values
            .Where(h => h.OwnerId == player.Id)
            .OrderByDescending(h => Core.Progression.Rarity.Of(h.Genome).Score)
            .ThenByDescending(h => h.Level)
            .ThenBy(h => h.Id, StringComparer.Ordinal)
            .Take(NotableHeroes)
            .Select(h => h.ToDto())
            .ToList();

        return new Shared.PlayerProfileDto(
            player.Id, player.Name, SeasonPassFor(player), PlayerAchievements(player), notable);
    }

    /// <summary>Treasury-health telemetry (economy control plane): current spendable balance, treasury outflow tallied
    /// by category, fees accrued to season pots, and the hero supply. A pure read over live state — never mutates.
    /// Outflow is the sats-insolvency side; hero supply is the OTHER inflation side (heroes have no hard cap, sats
    /// do). Per-source net-issuance and a mint/burn rate are a deliberate follow-up.</summary>
    public async Task<Shared.EconomyHealthDto> EconomyHealthAsync(CancellationToken ct = default)
    {
        var balance = await chain.TreasuryBalanceAsync(ct);
        var inflow = store.TreasuryInflowByTag.ToDictionary(kv => kv.Key, kv => kv.Value);
        var outflow = store.TreasuryOutflowByTag.ToDictionary(kv => kv.Key, kv => kv.Value);
        var seasonAccrual = store.SeasonFeeAccrual.Values.Sum();
        var minted = store.HeroesMinted;
        var heroSupply = store.Heroes.Count;
        // Gen-0 heroes come ONLY from the free starter grant — this is the tradeable-asset float that grant emits.
        var gen0Supply = store.Heroes.Values.Count(h => h.Generation == 0);
        // Counted at each burn site (merge ×2, absorb ×2, death-match ×1), NOT inferred as minted − supply.
        // That subtraction held only while heroes were volatile; since they persist, a restart leaves minted
        // at 0 against a surviving supply and the old clamp swallowed every real burn until mints overtook the
        // whole population — so the card showed mints with no burns, which is the alarm state, from a healthy
        // game. Both counters are now per-uptime deltas, which is how the gauge is documented to be read.
        var heroesBurned = store.HeroesBurned;
        // Market liquidity: resting (buyable) inventory vs cleared. Last-observed status — a pure read never
        // reconciles against the chain, so a just-sold offer may still read active until the next list call.
        var activeOffers = store.Offers.Values.Count(o => o.Status == "active");
        var closedOffers = store.Offers.Values.Count(o => o.Status == "closed");
        // The sale-attribution tripwire. DERIVED at read time from the same booking key the two booking
        // paths write, never accumulated: a counter incremented at the close would keep a false positive
        // forever when a hero offer reconciles closed before its buyer claims (the claim books it a moment
        // later), and could not be made idempotent against a reconcile that runs on every list call. Read
        // as a TREND against booked `listing` income — most of these are genuine reclaims, which book
        // nothing correctly, so the level means little and the slope means everything.
        var unbookedClosedFeeOffers = store.Offers.Values.Count(
            o => o.Status == "closed" && o.ListingFeeSats > 0 && !store.WasInflowTallied(OfferSaleInflowId(o.Id)));
        return new Shared.EconomyHealthDto(balance, inflow.Values.Sum(), outflow.Values.Sum(), inflow, outflow,
            seasonAccrual, heroSupply, gen0Supply, minted, heroesBurned, activeOffers, closedOffers,
            unbookedClosedFeeOffers, store.LedgerWriteFailures);
    }

    /// <summary>
    /// The operator console's one read: the economy-health picture plus the population, supply, market,
    /// flow-backlog and season figures around it. PURE OBSERVATION — every number is composed from live
    /// state, nothing is tracked that wasn't already tracked, and the read never reconciles, settles or pays.
    ///
    /// Two deliberate omissions, both because the alternative would have this read WRITE:
    /// the offer counts are last-observed rather than reconciled (reconciling books listing income into the
    /// treasury ledger), and the season is the un-settled snapshot (the player-facing board settles due
    /// seasons as a side effect of being read). An analytics view must not be able to move a sat.
    /// </summary>
    public async Task<Shared.AdminOverviewDto> AdminOverviewAsync(CancellationToken ct = default)
    {
        var economy = await EconomyHealthAsync(ct);
        var heroes = store.Heroes.Values.ToList();

        // Population + activity. There is no registration timestamp on a Player and no last-seen field, so
        // "signups today" and "DAU" are NOT answerable from existing state and are deliberately absent
        // rather than approximated. What the daily loop already records IS real activity: the day a player
        // last claimed, and whether their streak is alive.
        var today = Daily.DayIndex(DateTimeOffset.UtcNow);
        var players = new Shared.AdminPlayersDto(
            store.Players.Count,
            heroes.Select(h => h.OwnerId).Distinct().Count(),
            store.Players.Values.Count(p => p.LastClaimDay == today),
            store.Players.Values.Count(p => p.StreakCount > 0));

        // Supply cut two ways. Generation is stored on the hero; the rarity tier is recomputed from the
        // genome under THIS server's config — the same pure derivation /rarest and the hero cards use.
        var byGeneration = heroes.GroupBy(h => h.Generation)
            .OrderBy(g => g.Key)
            .Select(g => new Shared.SupplyBucketDto(g.Key.ToString(), g.Count()))
            .ToList();
        var byRarity = heroes.GroupBy(h => Rarity.Of(h.Genome, _config).Tier)
            .OrderByDescending(g => g.Key)
            .Select(g => new Shared.SupplyBucketDto(g.Key.ToString(), g.Count()))
            .ToList();

        var offers = store.Offers.Values.ToList();
        var market = new Shared.AdminMarketDto(
            offers.Count(o => o.Status == "pending"),
            offers.Count(o => o.Status == "active"),
            offers.Count(o => o.Status == "closed"),
            economy.InflowByTag.GetValueOrDefault("listing"),
            offers.Where(o => o.Status == "active").Sum(o => o.AskSats));

        // Session backlog per flow: how many are still in play against how many this server has seen.
        // Each flow spells "still in play" its own way — a status string for the match-like flows, a
        // Completed flag for the commit/reveal ones — so each is counted in its own vocabulary.
        var flows = new List<Shared.FlowCountsDto>
        {
            new("duel", store.Matches.Values.Count(m => m.Status is "open" or "accepted"), store.Matches.Count),
            new("death-match", store.DeathMatches.Values.Count(d => !d.Completed), store.DeathMatches.Count),
            new("squad", store.SquadMatches.Values.Count(s => s.Status is "open" or "accepted"), store.SquadMatches.Count),
            new("tournament", store.Tournaments.Values.Count(t => t.Status is "open" or "full"), store.Tournaments.Count),
            new("trials", store.Trials.Values.Count(t => !t.Completed), store.Trials.Count),
            new("gauntlet", store.Gauntlets.Values.Count(g => !g.Completed), store.Gauntlets.Count),
            new("breeding", store.Breedings.Values.Count(b => !b.Completed), store.Breedings.Count),
            new("merge", store.Merges.Values.Count(m => !m.Completed), store.Merges.Count),
        };

        // Brackets, unfinished ones first — those are the only ones a refund can ever apply to, and the
        // list is capped, so a stranded bracket must not be able to fall off the end behind resolved ones.
        // Ids are random rather than time-ordered, so the id tiebreak is for STABILITY, not chronology.
        // HasEntrantSnapshots is exactly what the strand-refund gate reads for a FULL bracket, so an
        // operator sees which brackets are stranded without this read re-deriving (and possibly
        // disagreeing with) that rule.
        var tournaments = store.Tournaments.Values
            .OrderBy(t => t.Status is "open" or "full" ? 0 : 1)
            .ThenBy(t => t.Id, StringComparer.Ordinal)
            .Take(50)
            .Select(t => new Shared.AdminTournamentDto(t.Id, t.Status, t.BuyInSats, t.Size, t.Entrants.Count,
                t.EntrantSnapshots is { Count: > 0 }))
            .ToList();

        return new Shared.AdminOverviewDto(
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(), economy, players, byGeneration, byRarity, market,
            flows, SeasonSnapshotAt(DateTimeOffset.UtcNow), tournaments);
    }

    // ── Tournaments: a buy-in bracket, treasury-mediated (buy-ins → treasury, prizes → podium minus the house rake) ──

    private const int MaxTournamentSize = 16;

    /// <summary>Opens a tournament and joins the opener as entrant #1: creates the committed bracket seed and
    /// bills the opener's buy-in fee-invoice. Others join with <see cref="JoinTournamentAsync"/>.</summary>
    public async Task<(TournamentSession Session, FeeInvoice BuyIn)> OpenTournamentAsync(
        Player player, string heroId, long buyInSats, int size, CancellationToken ct)
    {
        RequireAcceptedTerms(player);
        GetOwnedHero(player, heroId);
        if (buyInSats <= 0) throw new GameRuleException("The buy-in must be a positive number of sats.");
        if (size < Tournament.MinEntrants || size > MaxTournamentSize)
            throw new GameRuleException($"A tournament needs {Tournament.MinEntrants}–{MaxTournamentSize} entrants.");
        if (size % 2 != 0) throw new GameRuleException("The bracket size must be even.");

        var seed = CommitReveal.NewSeed();
        var session = new TournamentSession
        {
            Id = NewId("tourney"), OpenerPlayerId = player.Id, BuyInSats = buyInSats, Size = size,
            ServerSeed = seed, CommitmentHex = CommitReveal.Commit(seed),
        };
        var buyIn = await AddEntrantAsync(session, player, heroId, ct);   // the opener is entrant #1
        store.Tournaments[session.Id] = session;
        return (session, buyIn);
    }

    /// <summary>Joins a hero to an open tournament, billing the entrant's buy-in fee-invoice; once the bracket
    /// fills to <see cref="TournamentSession.Size"/> it is <c>full</c> and any entrant may resolve it.</summary>
    public async Task<(TournamentSession Session, FeeInvoice BuyIn)> JoinTournamentAsync(
        Player player, string tournamentId, string heroId, CancellationToken ct)
    {
        RequireAcceptedTerms(player);
        if (!store.Tournaments.TryGetValue(tournamentId, out var session))
            throw new GameRuleException($"Unknown tournament '{tournamentId}'.");
        GetOwnedHero(player, heroId);

        await store.TournamentLock.WaitAsync(ct);
        try
        {
            if (session.Status != "open") throw new GameRuleException("This tournament is no longer open to join.");
            if (session.Entrants.Any(e => e.PlayerId == player.Id))
                throw new GameRuleException("You have already joined this tournament.");
            var buyIn = await AddEntrantAsync(session, player, heroId, ct);
            return (session, buyIn);
        }
        finally { store.TournamentLock.Release(); }
    }

    /// <summary>Bills a buy-in fee-invoice and adds the entrant; the last entrant flips the bracket to <c>full</c>.</summary>
    private async Task<FeeInvoice> AddEntrantAsync(TournamentSession session, Player player, string heroId, CancellationToken ct)
    {
        var buyIn = await chain.CreateFeeInvoiceAsync($"tournament-buyin:{player.Id}:{session.Id}", session.BuyInSats, ct);
        session.Entrants.Add(new TournamentEntrant { PlayerId = player.Id, HeroId = heroId, BuyInInvoiceId = buyIn.InvoiceId });
        if (session.Entrants.Count >= session.Size)
        {
            session.Status = "full";
            // The bracket is full — LOCK the field. Snapshot every entrant's fighting state as of this
            // instant and commit to the canonical set: the commitment rides on the tournament DTO (so a
            // client pins it independently of the replay) and VerifyTournament recomputes it over the
            // replay's snapshots — a server can't substitute a genome/level/gear after the fact to steer
            // the real-sats pot. Resolve fights from THESE snapshots, so the committed set is exactly
            // what the bracket runs over (a hero re-geared/levelled after joining fights at fill state).
            session.EntrantSnapshots = session.Entrants.Select(e => GetHero(e.HeroId).ToDto()).ToList();
            session.EntrantsCommitmentHex = Shared.FairnessAudit.ComputeEntrantsCommitment(session.EntrantSnapshots);
        }
        // Durable BEFORE the buy-in invoice reaches the player: once they can pay it, the bracket holding
        // their sats has to survive a restart. (No-op unless persistence is configured.)
        await persistence.SaveTournamentAsync(session, ct);
        return buyIn;
    }

    /// <summary>Resolves a full bracket (once every buy-in has cleared): runs the pure resolver over the revealed
    /// entropy and pays the podium out of the pot minus the house rake. Single-shot + double-pay-safe.</summary>
    public async Task<(TournamentSession Session, TournamentResult Result, string ServerSeedHex, string EntropyHex, IReadOnlyList<long> Prizes)>
        ResolveTournamentAsync(Player player, string tournamentId, string nonce, CancellationToken ct)
    {
        if (!store.Tournaments.TryGetValue(tournamentId, out var session))
            throw new GameRuleException($"Unknown tournament '{tournamentId}'.");
        if (string.IsNullOrWhiteSpace(nonce)) throw new GameRuleException("A nonce is required.");
        if (session.Entrants.All(e => e.PlayerId != player.Id))
            throw new GameRuleException("Only an entrant can resolve the tournament.");

        await store.TournamentLock.WaitAsync(ct);
        try
        {
            if (session.Status == "resolved") throw new GameRuleException("This tournament is already resolved.");
            if (session.Status != "full") throw new GameRuleException("The bracket is not full yet.");

            // Every buy-in must have cleared before the bracket runs — an unpaid entry would leak the treasury.
            foreach (var e in session.Entrants)
                if (!await chain.IsInvoicePaidAsync(e.BuyInInvoiceId, ct))
                    throw new GameRuleException("All buy-ins must be paid before the tournament can run.");

            // Fight from the FILL-time locked snapshots — the set the published entrants-commitment binds
            // — via the SAME rebuild the client verifies with, so server resolution and client replay are
            // one computation. A full bracket without snapshots (rehydrated after a restart — they are
            // never persisted) can no longer honor its commitment: refuse, and let the strand refund
            // return the paid buy-ins.
            if (session.EntrantSnapshots is not { Count: > 0 })
                throw new GameRuleException("This bracket lost its locked entrant snapshots — refund it instead.");
            var entrants = session.EntrantSnapshots.Select(Shared.FairnessAudit.RebuildHero).ToList();
            var entropy = CommitReveal.DeriveEntropy(session.ServerSeed, "tournament", session.Id, nonce);
            var result = Tournament.Resolve(entrants, entropy, _config);

            session.Status = "resolved";   // commit BEFORE paying → no double-pay (mirrors the season settle marker)
            // Make that commit DURABLE before a single sat moves: a crash mid-payout must not let a restart
            // rehydrate this bracket as unresolved and pay the podium twice.
            await persistence.SaveTournamentAsync(session, ct);
            session.Result = result;
            session.Nonce = nonce;
            session.EntropyHex = Convert.ToHexString(entropy).ToLowerInvariant();
            session.ConfigVersion = ConfigVersion;   // stamp the rules this resolved under
        session.ContentVersion = ContentVersion; // …and the gear/dungeons it resolved with
            session.ContentVersion = ContentVersion; // …and the gear/dungeons it resolved with

            // The pot is already treasury-held (paid buy-ins); the rake is simply what we DON'T pay out.
            // PrizePool clamps the rake to 0..100% so a misconfigured rake can never pay the podium above the pot.
            var pot = session.BuyInSats * session.Entrants.Count;
            var prizePool = Tournament.PrizePool(pot, _config.TournamentRakePct);
            var podium = Tournament.Podium(result);
            var prizes = SeasonPrize.Split(prizePool, podium.Count, Tournament.PrizeWeights);
            session.Prizes = prizes;
            for (var i = 0; i < podium.Count && i < prizes.Count; i++)
            {
                var winnerPlayerId = session.Entrants.First(e => e.HeroId == podium[i]).PlayerId;
                var tag = $"tournament:{session.Id}:rank{i + 1}";
                // A failed prize is never retried (documented v1 limit), so the LOG is the only record that
                // this player is owed real sats — swallowing it silently made the debt unrecoverable.
                // Payout and booking are caught separately ON PURPOSE: a booking failure means the sats DID
                // move, and reading that as "unpaid" is exactly how a manual reconciliation double-pays.
                try { await chain.PayoutAsync(winnerPlayerId, prizes[i], tag, ct); }
                catch (Exception ex)
                {
                    logger?.LogError(ex,
                        "Tournament prize payout FAILED and will never be retried: player {PlayerId} is owed "
                        + "{Sats} sats for {Tag}. Settle it by hand — nothing else records this debt.",
                        winnerPlayerId, prizes[i], tag);
                    continue;
                }
                try { await store.RecordOutflowAsync("tournament", prizes[i], ct); }
                catch (Exception ex)
                {
                    logger?.LogError(ex,
                        "Tournament prize of {Sats} sats for {Tag} WAS PAID but could not be booked as "
                        + "outflow. The player has their sats; do NOT re-pay. Treasury outflow now "
                        + "under-reports by this amount.", prizes[i], tag);
                }
            }
            return (session, result, Convert.ToHexString(session.ServerSeed).ToLowerInvariant(), session.EntropyHex, prizes);
        }
        finally { store.TournamentLock.Release(); }
    }

    /// <summary>Refunds an UNRESOLVABLE bracket — one that can never run again: a FULL bracket whose
    /// fill-time entrant snapshots are gone (never persisted, so a restart drops them and resolve refuses
    /// without them), or an OPEN bracket that can never fill because an entrant hero was burned away. Every
    /// buy-in that actually CLEARED goes back to its entrant and the bracket lands terminally <c>refunded</c>;
    /// a bracket that can still be played is refused, so this is safe for anyone to trigger — it can't
    /// unwind a live pot. Single-shot + double-refund-safe.</summary>
    public async Task<(TournamentSession Session, int EntrantsRefunded, long RefundedSats)>
        RefundTournamentAsync(string tournamentId, CancellationToken ct)
    {
        if (!store.Tournaments.TryGetValue(tournamentId, out var session))
            throw new GameRuleException($"Unknown tournament '{tournamentId}'.");

        await store.TournamentLock.WaitAsync(ct);
        try
        {
            if (session.Status == "refunded") throw new GameRuleException("This tournament is already refunded.");
            if (session.Status == "resolved") throw new GameRuleException("This tournament is already resolved.");
            // The unresolvable gate, split by phase because resolvability is phase-dependent post-#104:
            // a FULL bracket fights from its fill-time locked EntrantSnapshots, NOT the live store — a
            // hero burned or transferred after fill still fights as its snapshot, so hero presence has no
            // bearing; the ONLY dead state is the snapshots being gone (never persisted; a restart drops
            // them). An OPEN bracket has no snapshots yet and lives on its heroes: it can still fill and
            // resolve unless an entrant hero was burned away — and heroes are persisted, so a restart no
            // longer strands it (nor may a stranger refund a forming pot out from under its entrants).
            var unresolvable = session.Status == "full"
                ? session.EntrantSnapshots is not { Count: > 0 }
                : session.Entrants.Any(e => !store.Heroes.ContainsKey(e.HeroId));
            if (!unresolvable)
                throw new GameRuleException("This tournament can still be resolved — refunds are only for a stranded bracket.");

            session.Status = "refunded";   // commit BEFORE paying → no double-refund (mirrors the resolve marker)
            // Make that commit DURABLE before a single sat moves: a crash mid-refund must not let a restart
            // rehydrate this bracket as stranded-but-live and pay every buy-in back a second time.
            await persistence.SaveTournamentAsync(session, ct);

            var refunded = 0;
            long refundedSats = 0;
            foreach (var e in session.Entrants)
            {
                var tag = $"tournament-refund:{session.Id}:{e.PlayerId}";
                // Only a CLEARED buy-in ever reached the treasury — "refunding" an unpaid seat would pay sats
                // the treasury never received. Each step is caught on its own because the three failures mean
                // different things to whoever has to clean up: an unreadable paid-check leaves the debt
                // UNKNOWN, a failed payout leaves it OWED, and a failed booking means it was already PAID.
                // A fault on any of them loses only THIS entrant's refund — it never aborts the rest with the
                // bracket already durably marked refunded (mirrors the podium's per-prize catch). None are
                // retried, so these logs are the only record that survives.
                bool paid;
                try { paid = await chain.IsInvoicePaidAsync(e.BuyInInvoiceId, ct); }
                catch (Exception ex)
                {
                    logger?.LogError(ex,
                        "Tournament refund SKIPPED for player {PlayerId}: could not read whether buy-in "
                        + "{InvoiceId} cleared, so {Sats} sats may or may not be owed for {Tag}. Check the "
                        + "invoice by hand before paying anything.",
                        e.PlayerId, e.BuyInInvoiceId, session.BuyInSats, tag);
                    continue;
                }
                if (!paid) continue;

                try { await chain.PayoutAsync(e.PlayerId, session.BuyInSats, tag, ct); }
                catch (Exception ex)
                {
                    logger?.LogError(ex,
                        "Tournament refund payout FAILED and will never be retried: player {PlayerId} paid a "
                        + "buy-in and is owed {Sats} sats back for {Tag}. Settle it by hand — nothing else "
                        + "records this debt.", e.PlayerId, session.BuyInSats, tag);
                    continue;
                }
                refunded++;
                refundedSats += session.BuyInSats;
                try { await store.RecordOutflowAsync("tournament-refund", session.BuyInSats, ct); }
                catch (Exception ex)
                {
                    logger?.LogError(ex,
                        "Tournament refund of {Sats} sats for {Tag} WAS PAID but could not be booked as "
                        + "outflow. The player has their sats; do NOT re-pay. Treasury outflow now "
                        + "under-reports by this amount.", session.BuyInSats, tag);
                }
            }
            return (session, refunded, refundedSats);
        }
        finally { store.TournamentLock.Release(); }
    }

    /// <summary>How many generation-0 heroes a starter claim mints. See <see cref="StarterPolicy"/>.</summary>
    public const int StarterHeroCount = StarterPolicy.HeroCount;

    /// <summary>What a starter claim costs — the floor price of a hero, once per hero minted.</summary>
    public long StarterClaimFeeSats => StarterPolicy.ClaimFeeSats(_config);

    /// <summary>
    /// Bills the starter claim. Returns the invoice to pay from the player's own wallet, or null when the
    /// server charges nothing — the heroes are only minted by <see cref="ClaimStartersAsync"/> once this
    /// clears.
    /// </summary>
    public async Task<FeeInvoice?> RequestStartersAsync(Player player, CancellationToken ct)
    {
        RequireAcceptedTerms(player);
        // Deliberately NOT gated on having claimed before: recruits are buyable as often as the player
        // wants to pay for them. The supply control is that they are the worst heroes in the game, not
        // that there is a limit on them.
        if (StarterClaimFeeSats <= 0) return null;

        // Re-requesting must not re-bill. The player may well have paid already and then lost the
        // response (a closed tab, a failed mint, a restart) — handing them a second invoice would charge
        // twice for one set of heroes. The outstanding invoice is theirs until it is spent on a mint.
        if (player.StarterFeeInvoiceId is { } outstanding)
            return await chain.GetFeeInvoiceAsync(outstanding, ct);

        var invoice = await chain.CreateFeeInvoiceAsync($"starter:{player.Id}", StarterClaimFeeSats, ct);
        player.StarterFeeInvoiceId = invoice.InvoiceId;
        await persistence.SavePlayerAsync(player, ct);
        return invoice;
    }

    /// <summary>Mints the one-time pair of generation-0 starter heroes to the player's own address,
    /// once the claim fee billed by <see cref="RequestStartersAsync"/> has cleared.</summary>
    public async Task<IReadOnlyList<Hero>> ClaimStartersAsync(Player player, CancellationToken ct)
    {
        // The first irreversible step into the game — assets get minted here. Gate it (opt-in) on the
        // terms the player is supposed to have read before any of that happened.
        RequireAcceptedTerms(player);

        // Serialized per player, and everything that consumes the invoice happens inside. One paid invoice
        // buys ONE claim: without the lock spanning check → mint → clear, two concurrent requests could
        // both see the same payment and mint two batches for the price of one.
        using var gate = await store.LockAsync($"starters:{player.Id}", ct);

        var invoiceId = player.StarterFeeInvoiceId;
        if (StarterClaimFeeSats > 0)
        {
            if (invoiceId is null)
                throw new GameRuleException("Request your starter heroes first — they carry a fee.");
            if (!await chain.IsInvoicePaidAsync(invoiceId, ct))
                throw new GameRuleException(
                    $"The {StarterClaimFeeSats} sat claim fee has not arrived yet — pay it from your wallet, then claim.");
        }

        var minted = new List<Hero>();
        for (var i = 0; i < StarterHeroCount; i++)
        {
            var entropy = RandomNumberGenerator.GetBytes(32);
            // Bought heroes are the worst in the game on purpose — see StarterPolicy.RecruitStatCap.
            var genome = Genome.NewRecruit(entropy, StarterPolicy.RecruitStatCap);
            minted.Add(await MintHeroAsync(player, genome, generation: 0,
                parentA: null, parentB: null,
                serverSeedHex: Convert.ToHexString(entropy).ToLowerInvariant(),
                playerNonce: null, entropyHex: null, ct));
        }

        // Spend the invoice only now the heroes exist. Clearing it is what makes the claim repeatable
        // WITHOUT being free: the next claim finds nothing outstanding and has to buy its own. A throw
        // above leaves it in place, so a player who paid and then hit a failed mint keeps what they bought.
        if (invoiceId is not null)
            await store.RecordInflowAsync(invoiceId, "starter", StarterClaimFeeSats, ct);
        player.StarterFeeInvoiceId = null;
        player.StarterClaimed = true;   // now only means "has claimed before" — the UI's cue, not a gate
        await persistence.SavePlayerAsync(player, ct);
        return minted;
    }

    /// <summary>Dev/test lever: mint one extra gen-0 hero to a player (InMemory only) — for tests that need a
    /// full 3-hero squad lineup beyond the two starters. NOT part of the game economy.</summary>
    public async Task<Hero> DevMintHeroAsync(Player player, CancellationToken ct)
    {
        var entropy = RandomNumberGenerator.GetBytes(32);
        return await MintHeroAsync(player, Genome.NewGen0(entropy), generation: 0,
            parentA: null, parentB: null,
            serverSeedHex: Convert.ToHexString(entropy).ToLowerInvariant(),
            playerNonce: null, entropyHex: null, ct);
    }

    private async Task<Hero> MintHeroAsync(
        Player player, Genome genome, int generation,
        string? parentA, string? parentB,
        string? serverSeedHex, string? playerNonce, string? entropyHex,
        CancellationToken ct)
    {
        var mint = await chain.MintHeroAssetAsync(player.Id, new HeroMintData(
            genome.ToHex(), generation, parentA, parentB, serverSeedHex, playerNonce), ct);
        return await BuildAndStoreHero(player, mint, genome, generation, parentA, parentB, serverSeedHex, playerNonce, entropyHex, ct);
    }

    private async Task<Hero> BuildAndStoreHero(
        Player player, HeroMintResult mint, Genome genome, int generation,
        string? parentA, string? parentB, string? serverSeedHex, string? playerNonce, string? entropyHex,
        CancellationToken ct = default)
    {
        var hero = new Hero
        {
            Id = mint.AssetId,
            OwnerId = player.Id,
            Name = HeroNamer.DeriveName(genome),
            Genome = genome,
            Generation = generation,
            ParentAId = parentA,
            ParentBId = parentB,
            ServerSeedHex = serverSeedHex,
            PlayerNonce = playerNonce,
            EntropyHex = entropyHex,
            AssetId = mint.AssetId,
            MintArkTxId = mint.ArkTxId,
        };
        store.Heroes[hero.Id] = hero;
        store.RecordMint();   // adjacent to the add so supply++ and minted++ stay in lockstep for telemetry
        // Every hero in the game passes through here — starters, bred, fused, absorbed — so this is the one
        // place the discovery race needs to watch. A newly-stamped find is persisted so the discovery, the
        // hero's edition, and the per-set count all survive a restart (else the next find becomes a 2nd "#1").
        if (FancySets.TitleFor(hero.Genome, _config) is { } fancy
            && store.RecordFancyFind(fancy, hero.Id, hero.Name, player.Id, DateTimeOffset.UtcNow.ToUnixTimeSeconds()) is { } find)
            await persistence.SaveFancyFindAsync(find, ct);
        // MINT is an identity event: durable NOW, not at the next flush — the chain can't enumerate a
        // player's heroes back, so an unsaved hero is unrecoverable if the process dies. Saved AFTER the
        // on-chain mint (this isn't a payout latch; the asset exists whether or not the row lands, and a
        // faulted save re-throws into a retryable flow). (No-op unless persistence is configured.)
        await persistence.SaveHeroAsync(hero, ct);
        return hero;
    }

    // ── Breeding: commit (invoice) → client pays → reveal ──────────────

    public async Task<(BreedingSession Session, FeeInvoice? Invoice)> CommitBreedingAsync(
        Player player, string parentAId, string parentBId, string mode, CancellationToken ct)
    {
        RequireAcceptedTerms(player);
        var parentA = GetOwnedHero(player, parentAId);
        var parentB = GetOwnedHero(player, parentBId);
        // The breed fee escalates with how much the parents have already been bred
        // (their combined breed count) — a supply-side sats sink.
        var breedFee = BreedingPolicy.FeeSats(_config.BreedingFeeSats, parentA.BreedCount + parentB.BreedCount, _config);

        // Rarity-derived sterility: the rarest heroes can be born unable to breed,
        // capping the supply of legendary lines. Deterministic from the genome.
        if (Sterility.IsSterile(parentA.Genome, _config))
            throw new GameRuleException($"{parentA.Name} is sterile — it cannot breed.");
        if (Sterility.IsSterile(parentB.Genome, _config))
            throw new GameRuleException($"{parentB.Name} is sterile — it cannot breed.");

        if (BreedingService.Validate(parentA, parentB, DateTimeOffset.UtcNow) is { } error)
            throw new GameRuleException(error);

        var seed = CommitReveal.NewSeed();
        var sessionId = NewId("breed");

        if (mode == "covenant")
        {
            // The player deposits BOTH parents + the fee into the breed escrow;
            // the covenant (not the treasury) then enforces the mint's shape.
            var escrow = await chain.CreateBreedEscrowAsync(
                sessionId, player.Id, parentA.AssetId!, parentB.AssetId!,
                breedFee, receipts.PublicKeyHex,
                DateTimeOffset.UtcNow.Add(_options.WagerEscrowRefundAfter).ToUnixTimeSeconds(), ct);
            var covenantSession = new BreedingSession
            {
                Id = sessionId, PlayerId = player.Id, ParentAId = parentAId, ParentBId = parentBId,
                ServerSeed = seed, CommitmentHex = CommitReveal.Commit(seed),
                Mode = "covenant", EscrowAddress = escrow.EscrowAddress, FeeSats = breedFee,
            };
            store.Breedings[covenantSession.Id] = covenantSession;
            return (covenantSession, null);
        }

        var invoice = await chain.CreateFeeInvoiceAsync(
            $"breed:{parentAId}+{parentBId}", breedFee, ct);
        var session = new BreedingSession
        {
            Id = sessionId,
            PlayerId = player.Id,
            ParentAId = parentAId,
            ParentBId = parentBId,
            ServerSeed = seed,
            CommitmentHex = CommitReveal.Commit(seed),
            FeeInvoiceId = invoice.InvoiceId,
            FeeSats = breedFee,
        };
        store.Breedings[session.Id] = session;
        return (session, invoice);
    }

    public async Task<(Hero Child, string ServerSeedHex, string EntropyHex, Shared.ProgressionReceiptDto Receipt)> RevealBreedingAsync(
        Player player, string breedingId, string nonce, CancellationToken ct)
    {
        if (!store.Breedings.TryGetValue(breedingId, out var session) || session.PlayerId != player.Id)
            throw new GameRuleException($"Unknown breeding session '{breedingId}'.");
        // Per-session gate: completed-check → mint → complete-set must be one atomic step, or two
        // concurrent reveals of one paid fee both pass the guard and mint two children.
        using var gate = await store.LockAsync($"breed:{session.Id}", ct);
        if (session.Completed) throw new GameRuleException("Breeding already completed.");
        if (string.IsNullOrWhiteSpace(nonce)) throw new GameRuleException("A nonce is required.");

        // The deposit must be present: a paid fee invoice (invoice mode) or the
        // parents + fee sitting in the breed escrow (covenant mode).
        if (session.Mode == "covenant")
        {
            if (!await chain.IsBreedEscrowFundedAsync(session.Id, ct))
                throw new GameRuleException("Deposit both parents and the fee into the breed escrow, then reveal.");
        }
        else if (!await chain.IsInvoicePaidAsync(session.FeeInvoiceId!, ct))
        {
            throw new GameRuleException("The breeding fee invoice has not been paid yet — pay it from your wallet, then reveal.");
        }
        else
        {
            // Invoice mode: the fee is now in the treasury (a paid Receive invoice). Tally it as "breed"
            // inflow, deduped by invoice id (covenant mode captures structurally → recorded at execution).
            await store.RecordInflowAsync(session.FeeInvoiceId!, "breed", session.FeeSats, ct);
        }

        var parentA = GetOwnedHero(player, session.ParentAId);
        var parentB = GetOwnedHero(player, session.ParentBId);
        var now = DateTimeOffset.UtcNow;
        if (BreedingService.Validate(parentA, parentB, now) is { } error)
            throw new GameRuleException(error);

        var entropy = CommitReveal.DeriveEntropy(session.ServerSeed, session.ParentAId, session.ParentBId, nonce);
        var policy = new BreedingPolicy(_options.BreedingCooldownBaseUnit);
        var outcome = BreedingService.Breed(parentA, parentB, entropy, policy, _config);

        var serverSeedHex = Convert.ToHexString(session.ServerSeed).ToLowerInvariant();
        var entropyHex = Convert.ToHexString(entropy).ToLowerInvariant();

        Hero child;
        if (session.Mode == "covenant")
        {
            // The oracle (game key) attests the child's metadata Merkle root;
            // the covenant binds the on-chain mint to exactly this attestation.
            var childData = new HeroMintData(
                outcome.ChildGenome.ToHex(), outcome.ChildGeneration,
                session.ParentAId, session.ParentBId, serverSeedHex, nonce);
            var root = Chain.Covenants.ArkadeCovenants.MetadataMerkleRoot(
                Chain.Covenants.BreedEscrowContracts.ChildMetadata(
                    childData.GenomeHex, childData.Generation, childData.ParentAId ?? "", childData.ParentBId ?? "",
                    childData.ServerSeedHex ?? "", childData.PlayerNonce ?? ""));
            var oracleSig = receipts.SignDigest(root);
            var mint = await chain.ExecuteBreedCovenantAsync(session.Id, childData, oracleSig, ct);
            child = await BuildAndStoreHero(player, mint, outcome.ChildGenome, outcome.ChildGeneration,
                session.ParentAId, session.ParentBId, serverSeedHex, nonce, entropyHex, ct);
            // Covenant mode: the spend delivered FeeSats to the treasury fee output — tally it (dedup by session id).
            await store.RecordInflowAsync(session.Id, "breed", session.FeeSats, ct);
        }
        else
        {
            child = await MintHeroAsync(player, outcome.ChildGenome, outcome.ChildGeneration,
                session.ParentAId, session.ParentBId, serverSeedHex, nonce, entropyHex, ct);
        }
        // Chain FIRST, latch + in-memory effects after (the death-match settle pattern): if the mint
        // faults, the session stays open and the parents untouched, so the already-paid fee can be
        // retried instead of stranded behind a Completed flag and a burned cooldown.
        session.Completed = true;
        parentA.BreedCount++;
        parentA.BreedCooldownUntil = now + outcome.ParentACooldown;
        parentB.BreedCount++;
        parentB.BreedCooldownUntil = now + outcome.ParentBCooldown;
        // Parent breed-counts + cooldowns are progression — flushed, not saved inline (the CHILD's mint
        // above already saved durably). Worst case a crash re-opens a cooldown and re-prices one breed fee.
        store.MarkHeroDirty(parentA.Id);
        store.MarkHeroDirty(parentB.Id);
        session.ChildHeroId = child.Id;

        var receipt = IssueReceipt(new Shared.ProgressionReceiptDto(
                "breeding", session.Id, session.ParentAId, session.ParentBId, child.Id,
                serverSeedHex, nonce, session.CommitmentHex,
                0, 0, parentA.Level, parentB.Level,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "", ""),
            session.ParentAId, session.ParentBId, child.Id);

        return (child, serverSeedHex, entropyHex, receipt);
    }

    // ── PvE gauntlet (F1): open (commit + fee invoice) → client pays → run ──

    public async Task<(GauntletSession Session, FeeInvoice Invoice)> OpenGauntletAsync(
        Player player, string heroId, CancellationToken ct)
    {
        RequireAcceptedTerms(player);
        var hero = GetOwnedHero(player, heroId);
        var now = DateTimeOffset.UtcNow;
        if (hero.GauntletCooldownUntil is { } until && until > now)
            throw new GameRuleException($"{hero.Name} is resting after its last gauntlet — try again shortly.");

        var seed = CommitReveal.NewSeed();
        var id = NewId("gauntlet");
        var fee = Gauntlet.Fee(hero.Level, _config);
        var invoice = await chain.CreateFeeInvoiceAsync($"gauntlet:{heroId}", fee, ct);
        var session = new GauntletSession
        {
            Id = id, PlayerId = player.Id, HeroId = heroId,
            ServerSeed = seed, CommitmentHex = CommitReveal.Commit(seed),
            FeeInvoiceId = invoice.InvoiceId, FeeSats = fee,
        };
        store.Gauntlets[id] = session;
        return (session, invoice);
    }

    public async Task<(GauntletRun Run, long XpAwarded, Shared.HeroDto HeroSnapshot, string? ItemAwarded, string? ItemAssetId, string ServerSeedHex, string EntropyHex, Shared.ProgressionReceiptDto Receipt)> RunGauntletAsync(
        Player player, string gauntletId, string nonce, CancellationToken ct)
    {
        if (!store.Gauntlets.TryGetValue(gauntletId, out var session) || session.PlayerId != player.Id)
            throw new GameRuleException($"Unknown gauntlet '{gauntletId}'.");
        // Per-session gate: completed-check → run → item delivery → complete-set must be one atomic
        // step, or two concurrent runs of one paid fee both resolve — double XP and a doubled full-clear item.
        using var gate = await store.LockAsync($"gauntlet:{session.Id}", ct);
        if (session.Completed) throw new GameRuleException("This gauntlet has already been run.");
        if (string.IsNullOrWhiteSpace(nonce)) throw new GameRuleException("A nonce is required.");
        if (!await chain.IsInvoicePaidAsync(session.FeeInvoiceId, ct))
            throw new GameRuleException("The gauntlet fee invoice has not been paid yet — pay it from your wallet, then run.");
        await store.RecordInflowAsync(session.FeeInvoiceId, "gauntlet", session.FeeSats, ct);

        var hero = GetOwnedHero(player, session.HeroId);
        var heroSnapshot = hero.ToDto();          // pre-run, so the client can replay the ghosts + fights
        var preRunLevel = hero.Level;

        var entropy = CommitReveal.DeriveEntropy(session.ServerSeed, session.Id, session.HeroId, nonce);
        var run = Gauntlet.Resolve(hero, entropy, _config);

        // Capped, priced XP faucet (anti-farming): the award is computed from the PRE-run level, so a run
        // that crosses the cap keeps its award, but future runs (already past the cap) award nothing.
        var xpAward = Gauntlet.XpForRun(preRunLevel, run.WavesCleared);

        // A full clear delivers one entropy-picked 500-sat-tier item to the player's wallet — chain
        // FIRST, latch + in-memory effects after (the breed-reveal pattern): if the delivery faults,
        // the session stays open and the hero untouched, so the already-paid fee re-runs the SAME
        // deterministic gauntlet (same seed + nonce) instead of stranding behind a Completed flag
        // with the item undelivered.
        var itemAwarded = Gauntlet.RewardItem(entropy, run.WavesCleared);
        string? itemAssetId = null;
        if (itemAwarded is not null)
        {
            var item = Core.Equipment.ItemCatalog.Find(itemAwarded)!;
            var delivery = await chain.DeliverItemAssetAsync(player.Id, item.Id, item.Name, ct);
            itemAssetId = delivery.ItemAssetId;
        }

        session.Completed = true;
        ApplyXp(hero, xpAward);
        hero.GauntletCooldownUntil = DateTimeOffset.UtcNow + _options.GauntletCooldown;
        store.MarkHeroDirty(hero.Id);   // the cooldown too — its own mutation, not coupled to ApplyXp's mark

        var serverSeedHex = Convert.ToHexString(session.ServerSeed).ToLowerInvariant();
        var entropyHex = Convert.ToHexString(entropy).ToLowerInvariant();
        // Gauntlet receipt (NOT a "match" receipt → carries no leaderboard weight). HeroBId is empty;
        // ResultHeroId = the hero on a full clear; XpAwardA = the award; LevelA = post-run level;
        // LevelB = PRE-run level (so a verifier can recompute the level-10 cap independently).
        var receipt = IssueReceipt(new Shared.ProgressionReceiptDto(
                "gauntlet", session.Id, session.HeroId, "", run.WavesCleared >= Gauntlet.WaveCount ? session.HeroId : null,
                serverSeedHex, nonce, session.CommitmentHex,
                xpAward, 0, hero.Level, preRunLevel,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "", ""),
            session.HeroId);

        return (run, xpAward, heroSnapshot, itemAwarded, itemAssetId, serverSeedHex, entropyHex, receipt);
    }

    // ── Endless PvE Trials: open (commit, FREE) → run (endless ghost ladder, score-only) ──

    /// <summary>Per-player cap on open Trials sessions. Trials is the one FREE, chainless open-flow, so
    /// without a bound a single player's open-loop would grow the in-memory store without limit (a
    /// one-player memory DoS). Generous for honest open→run play; a completed run is evicted in
    /// <see cref="RunTrials"/> (its score lives in the signed receipt + best-by-hero, not the session).</summary>
    public const int MaxOpenTrialsPerPlayer = 8;

    public TrialsSession OpenTrials(Player player, string heroId)
    {
        GetOwnedHero(player, heroId);   // ownership check (throws if not the player's hero)
        // Bound the free flow: refuse a player already sitting on a full quota of open sessions (a
        // completed run is evicted in RunTrials, so this counts only live, open-and-unrun sessions).
        if (store.Trials.Values.Count(s => s.PlayerId == player.Id) >= MaxOpenTrialsPerPlayer)
            throw new GameRuleException("Too many open trials runs — finish one before starting another.");
        var seed = CommitReveal.NewSeed();
        var id = NewId("trials");
        var session = new TrialsSession
        {
            Id = id, PlayerId = player.Id, HeroId = heroId,
            ServerSeed = seed, CommitmentHex = CommitReveal.Commit(seed),
            Affix = Trials.AffixFor(DateTimeOffset.UtcNow),   // pinned at open, not recomputed at run/verify
        };
        store.Trials[id] = session;
        return session;
    }

    public (TrialsRun Run, Shared.HeroDto HeroSnapshot, string? Title, int BestScore, TrialsAffix Affix, string ServerSeedHex, string EntropyHex, Shared.ProgressionReceiptDto Receipt) RunTrials(
        Player player, string trialsId, string nonce)
    {
        if (!store.Trials.TryGetValue(trialsId, out var session) || session.PlayerId != player.Id)
            throw new GameRuleException($"Unknown trials run '{trialsId}'.");
        if (session.Completed) throw new GameRuleException("This trials run has already been run.");
        if (string.IsNullOrWhiteSpace(nonce)) throw new GameRuleException("A nonce is required.");

        var hero = GetOwnedHero(player, session.HeroId);
        var heroSnapshot = hero.ToDto();   // pre-run, so the client replays the ghosts + fights
        session.Completed = true;

        var entropy = CommitReveal.DeriveEntropy(session.ServerSeed, session.Id, session.HeroId, nonce);
        var run = Trials.Resolve(hero, entropy, _config, session.Affix);
        var title = Trials.TitleFor(run.WavesCleared);

        // Track the hero's personal best (the leaderboard basis) — only ever climbs.
        var best = store.TrialsBestByHero.AddOrUpdate(session.HeroId, run.WavesCleared,
            (_, prev) => Math.Max(prev, run.WavesCleared));

        var serverSeedHex = Convert.ToHexString(session.ServerSeed).ToLowerInvariant();
        var entropyHex = Convert.ToHexString(entropy).ToLowerInvariant();
        // Trials receipt (NOT a "match" receipt → no ranked-ladder weight). No XP/level change; the SCORE
        // rides in XpAwardB so the signed receipt itself attests the run's waves-cleared (tamper-evident).
        var receipt = IssueReceipt(new Shared.ProgressionReceiptDto(
                "trials", session.Id, session.HeroId, "", session.HeroId,
                serverSeedHex, nonce, session.CommitmentHex,
                0, run.WavesCleared, hero.Level, hero.Level,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "", ""),
            session.HeroId);

        // Evict the completed session — its score now lives in the signed receipt + best-by-hero, so the
        // live session is never read again; dropping it bounds the free-flow store to only open runs.
        store.Trials.TryRemove(trialsId, out _);

        return (run, heroSnapshot, title, best, session.Affix, serverSeedHex, entropyHex, receipt);
    }

    // ── Merge / fusion: commit (escrow deposit) → reveal ───────────────

    public async Task<(MergeSession Session, string EscrowAddress)> CommitMergeAsync(
        Player player, string baseId, string sacrificeId, string mode, CancellationToken ct)
    {
        RequireAcceptedTerms(player);
        if (baseId == sacrificeId)
            throw new GameRuleException("The base and the sacrifice must be two different heroes.");
        var baseHero = GetOwnedHero(player, baseId);
        var sacrificeHero = GetOwnedHero(player, sacrificeId);
        // Sterility does NOT gate being an input — a sterile Legendary is a great
        // sacrifice (feed its rare trait in), which gives sterile rares a use.

        var seed = CommitReveal.NewSeed();
        var sessionId = NewId("merge");
        // Both inputs plus the fee go into the merge escrow; execution retires the two
        // inputs to the treasury (the sink) and mints the fused hero to the player.
        var escrow = await chain.CreateMergeEscrowAsync(
            sessionId, player.Id, baseHero.AssetId!, sacrificeHero.AssetId!,
            _options.MergeFeeSats, receipts.PublicKeyHex,
            DateTimeOffset.UtcNow.Add(_options.WagerEscrowRefundAfter).ToUnixTimeSeconds(), ct);

        var session = new MergeSession
        {
            Id = sessionId, PlayerId = player.Id, BaseId = baseId, SacrificeId = sacrificeId,
            ServerSeed = seed, CommitmentHex = CommitReveal.Commit(seed),
            Mode = mode, EscrowAddress = escrow, FeeSats = _options.MergeFeeSats,
        };
        store.Merges[session.Id] = session;
        return (session, escrow);
    }

    public async Task<(Hero Fused, string ServerSeedHex, string EntropyHex, Shared.ProgressionReceiptDto Receipt)> RevealMergeAsync(
        Player player, string mergeId, string nonce, CancellationToken ct)
    {
        if (!store.Merges.TryGetValue(mergeId, out var session) || session.PlayerId != player.Id)
            throw new GameRuleException($"Unknown merge session '{mergeId}'.");
        // Per-session gate: completed-check → execute → complete-set must be one atomic step, or two
        // concurrent reveals of one funded escrow both pass the guard and race the once-only execute.
        using var gate = await store.LockAsync($"merge:{session.Id}", ct);
        if (session.Completed) throw new GameRuleException("Merge already completed.");
        if (string.IsNullOrWhiteSpace(nonce)) throw new GameRuleException("A nonce is required.");

        // The deposit must be present: base + sacrifice + fee sitting in the merge escrow.
        if (!await chain.IsMergeEscrowFundedAsync(session.Id, ct))
            throw new GameRuleException("Deposit the base, the sacrifice, and the fee into the merge escrow, then reveal.");

        var baseHero = GetOwnedHero(player, session.BaseId);
        var sacrificeHero = GetOwnedHero(player, session.SacrificeId);

        // Entropy-seeded fusion: concentration almost always succeeds, but the fused
        // genome (hence its sterility) can't be precomputed — the gamble that keeps
        // sterility meaningful. Deterministic given (seed, ids, nonce).
        var entropy = CommitReveal.DeriveEntropy(session.ServerSeed, session.Id, session.BaseId, session.SacrificeId, nonce);
        var fusedGenome = Fusion.Fuse(baseHero.Genome, sacrificeHero.Genome, entropy, _config);
        var fusedGeneration = Math.Max(baseHero.Generation, sacrificeHero.Generation) + 1;

        var serverSeedHex = Convert.ToHexString(session.ServerSeed).ToLowerInvariant();
        var entropyHex = Convert.ToHexString(entropy).ToLowerInvariant();

        // The oracle (game key) attests the fused hero's metadata Merkle root; rung 2's
        // covenant binds the on-chain mint (inputs retired, fused issued) to this attestation.
        var fusedData = new HeroMintData(
            fusedGenome.ToHex(), fusedGeneration, session.BaseId, session.SacrificeId, serverSeedHex, nonce);
        var root = Chain.Covenants.ArkadeCovenants.MetadataMerkleRoot(
            Chain.Covenants.BreedEscrowContracts.ChildMetadata(
                fusedData.GenomeHex, fusedData.Generation, fusedData.ParentAId ?? "", fusedData.ParentBId ?? "",
                fusedData.ServerSeedHex ?? "", fusedData.PlayerNonce ?? ""));
        var oracleSig = receipts.SignDigest(root);
        var mint = await chain.ExecuteMergeAsync(session.Id, fusedData, oracleSig, ct);

        var fused = await BuildAndStoreHero(player, mint, fusedGenome, fusedGeneration,
            session.BaseId, session.SacrificeId, serverSeedHex, nonce, entropyHex, ct);
        // The merge spend retired both inputs and delivered FeeSats to the treasury — tally it (dedup by session id).
        await store.RecordInflowAsync(session.Id, "merge", session.FeeSats, ct);
        // Chain FIRST, latch + in-memory effects after (the breed/death-match settle pattern): if the
        // execute faults, the session stays open and the inputs untouched, so the deposited base +
        // sacrifice + fee can be retried instead of stranded in escrow behind a Completed flag.
        session.Completed = true;
        // The fused hero inherits the base's level (you keep your progression); its genesis
        // level is attested by the merge receipt below so ReplayLevel stays consistent.
        fused.Level = baseHero.Level;
        session.FusedHeroId = fused.Id;
        // Re-save: the inherited level is BIRTH state, not grind — losing it to a crash inside the flush
        // window would rehydrate the fused hero at level 1. Still inside the identity event, so save now.
        await persistence.SaveHeroAsync(fused, ct);

        // Both inputs are consumed: drop their server-side records (their assets are
        // on-chain-retired to the treasury by ExecuteMergeAsync) — and their durable rows with them,
        // or a restart resurrects two heroes whose assets no longer exist.
        store.Heroes.TryRemove(session.BaseId, out _);
        store.Heroes.TryRemove(session.SacrificeId, out _);
        store.RecordBurn(); store.RecordBurn();
        await persistence.DeleteHeroAsync(session.BaseId, ct);
        await persistence.DeleteHeroAsync(session.SacrificeId, ct);

        var receipt = IssueReceipt(new Shared.ProgressionReceiptDto(
                "merge", session.Id, session.BaseId, session.SacrificeId, fused.Id,
                serverSeedHex, nonce, session.CommitmentHex,
                0, 0, baseHero.Level, sacrificeHero.Level,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "", ""),
            session.BaseId, session.SacrificeId, fused.Id);

        return (fused, serverSeedHex, entropyHex, receipt);
    }

    // ── Death-match: open → both stake a hero → settle (loser's hero burns) ──

    public async Task<(DeathMatchSession Session, string EscrowAddress, Shared.FavorabilityDto Favorability, IReadOnlyList<Shared.GearStakeDto> ChallengerGear, IReadOnlyList<Shared.GearStakeDto> DefenderGear, FeeInvoice ChallengerFeeInvoice)> OpenDeathMatchAsync(
        Player player, string challengerHeroId, string defenderHeroId, bool absorb, CancellationToken ct)
    {
        RequireAcceptedTerms(player);
        var challenger = GetOwnedHero(player, challengerHeroId);
        var defender = GetHero(defenderHeroId);
        if (challenger.Id == defender.Id)
            throw new GameRuleException("A hero cannot death-match itself.");
        if (defender.OwnerId == player.Id)
            throw new GameRuleException("A death-match needs an opponent — you own both heroes.");

        var seed = CommitReveal.NewSeed();
        var id = NewId("dm");
        var commitment = CommitReveal.Commit(seed);
        var refundAfter = DateTimeOffset.UtcNow.Add(_options.WagerEscrowRefundAfter).ToUnixTimeSeconds();
        // Covenant-v2: ONE joint escrow baked at open — both parties are known (the
        // defender is the challenged hero's owner). Both players stake into this one
        // address; consent = staking. The settle branch STRUCTURALLY enforces the
        // outcome: burn the loser, return the winner's hero AND ALL staked gear to
        // the winner. Each side's stake = their hero + the item units matching the
        // hero's equipped loadout AT OPEN (unequip before opening to shield gear).
        var challengerGearIds = challenger.Equipment.Slots.Values.ToList();
        var defenderGearIds = defender.Equipment.Slots.Values.ToList();
        // Absorb mode → the 6-leaf escrow bakes the species the absorbed hero mints under.
        var speciesId = absorb ? (await chain.GetInfoAsync(ct)).SpeciesAssetId ?? "" : "";
        var escrow = await chain.CreateDeathMatchJointEscrowAsync(
            id, player.Id, challenger.AssetId!, defender.OwnerId, defender.AssetId!,
            Convert.FromHexString(commitment), receipts.PublicKeyHex, refundAfter,
            challengerGearIds, defenderGearIds, absorb: absorb, speciesId: speciesId, ct: ct);

        // Per-character death-match fee (level-scaled treasury sink) — both sides' fees gate settle.
        var feeInvoice = await chain.CreateFeeInvoiceAsync(
            $"dm-fee:challenger:{id}", Leveling.DeathMatchFee(challenger.Level, absorb, _config), ct);

        var session = new DeathMatchSession
        {
            Id = id,
            ChallengerFeeInvoiceId = feeInvoice.InvoiceId,
            ChallengerPlayerId = player.Id,
            DefenderPlayerId = defender.OwnerId,
            ChallengerHeroId = challengerHeroId,
            DefenderHeroId = defenderHeroId,
            ServerSeed = seed,
            CommitmentHex = commitment,
            JointEscrowAddress = escrow,
            ChallengerGearItemIds = challengerGearIds,
            DefenderGearItemIds = defenderGearIds,
            Absorb = absorb,
            SpeciesId = speciesId,
        };
        store.DeathMatches[session.Id] = session;
        // Favorability from realized POWER + the element matchup (F18) — gear is staked here, so a level read
        // would lie, and a power-only read would ignore the ring, the biggest lever between these two heroes.
        var favor = new Shared.FavorabilityDto(defender.Level - challenger.Level,
            Matchmaking.PowerFavor(PowerScore.Compute(challenger, _config), PowerScore.Compute(defender, _config),
                challenger.Genome.Element, defender.Genome.Element, _config));
        var escrowParams = await chain.GetDeathMatchEscrowParamsAsync(id, ct);
        return (session, escrow, favor, MapGearDtos(escrowParams?.ChallengerGear), MapGearDtos(escrowParams?.DefenderGear), feeInvoice);
    }

    /// <summary>The chain-resolved gear stakes as client-facing deposit instructions (ItemId is display provenance; AssetId is what gets sent).</summary>
    private static IReadOnlyList<Shared.GearStakeDto> MapGearDtos(IReadOnlyList<Chain.Covenants.GearStake>? stakes)
        => stakes?.Select(s => new Shared.GearStakeDto(s.ItemId ?? s.AssetId, s.AssetId, s.Amount)).ToList() ?? [];

    public async Task<(DeathMatchSession Session, string EscrowAddress, Hero Defender, IReadOnlyList<Shared.GearStakeDto> DefenderGear, FeeInvoice DefenderFeeInvoice)> AcceptDeathMatchAsync(
        Player player, string deathMatchId, CancellationToken ct)
    {
        RequireAcceptedTerms(player);
        if (!store.DeathMatches.TryGetValue(deathMatchId, out var session))
            throw new GameRuleException($"Unknown death-match '{deathMatchId}'.");
        if (session.DefenderPlayerId != player.Id)
            throw new GameRuleException("Only the challenged hero's owner can accept this death-match.");
        if (session.Accepted) throw new GameRuleException("Death-match already accepted.");
        if (session.Completed) throw new GameRuleException("Death-match already resolved.");
        var defender = GetOwnedHero(player, session.DefenderHeroId);

        // Covenant-v2: no new escrow — the joint escrow was baked at open. Accepting =
        // staking the defender's hero (+ their baked gear) into the SAME joint address
        // (consent = staking).
        session.Accepted = true;
        // Defender's death-match fee — mirrors the wager defender fee; gated at settle.
        var feeInvoice = await chain.CreateFeeInvoiceAsync(
            $"dm-fee:defender:{deathMatchId}", Leveling.DeathMatchFee(defender.Level, session.Absorb, _config), ct);
        session.DefenderFeeInvoiceId = feeInvoice.InvoiceId;
        var escrowParams = await chain.GetDeathMatchEscrowParamsAsync(deathMatchId, ct);
        return (session, session.JointEscrowAddress!, defender, MapGearDtos(escrowParams?.DefenderGear), feeInvoice);
    }

    public async Task<(Shared.BattleResultDto Result, string WinnerHeroId, string LoserHeroId, Shared.HeroDto ChallengerSnapshot, Shared.HeroDto DefenderSnapshot, string ServerSeedHex, string EntropyHex, Shared.ProgressionReceiptDto Receipt, bool Minted, int TraitsAbsorbed, string? NewGenomeHex, Shared.HeroDto? NewHero)> SettleDeathMatchAsync(
        Player player, string deathMatchId, string nonce, CancellationToken ct)
    {
        if (!store.DeathMatches.TryGetValue(deathMatchId, out var session))
            throw new GameRuleException($"Unknown death-match '{deathMatchId}'.");
        if (session.ChallengerPlayerId != player.Id && session.DefenderPlayerId != player.Id)
            throw new GameRuleException("Only a participant can settle this death-match.");
        // Per-match gate: completed-check → chain settle → complete-set must be one atomic step, or two
        // concurrent settles (a poll-retrying client, or both participants at once) both pass the guard
        // and race the once-only burn/mint. Keyed to the death-match itself (not settle-specific) so the
        // timelocked reclaim path can later serialize against settle under the same key.
        using var gate = await store.LockAsync($"deathmatch:{deathMatchId}", ct);
        if (session.Completed) throw new GameRuleException("Death-match already resolved.");
        if (string.IsNullOrWhiteSpace(nonce)) throw new GameRuleException("A nonce is required.");

        // Both players must have staked their hero into the one joint escrow.
        if (!await chain.IsDeathMatchEscrowFundedAsync(deathMatchId, ct))
            throw new GameRuleException("Both players must stake their hero before the death-match settles.");

        // Both per-character death-match fees must be paid (mirrors the wager fight gate; never blocks the refund path).
        if (session.ChallengerFeeInvoiceId is null || !await chain.IsInvoicePaidAsync(session.ChallengerFeeInvoiceId, ct))
            throw new GameRuleException("The challenger's death-match fee hasn't been paid yet.");
        if (session.DefenderFeeInvoiceId is null || !await chain.IsInvoicePaidAsync(session.DefenderFeeInvoiceId, ct))
            throw new GameRuleException("The defender's death-match fee hasn't been paid yet.");

        var challenger = GetHero(session.ChallengerHeroId);
        var defender = GetHero(session.DefenderHeroId);
        // Death-match fees (both confirmed paid above) — treasury captures, tallied once per invoice.
        await store.RecordInflowAsync(session.ChallengerFeeInvoiceId!, "deathmatch", Leveling.DeathMatchFee(challenger.Level, session.Absorb, _config), ct);
        await store.RecordInflowAsync(session.DefenderFeeInvoiceId!, "deathmatch", Leveling.DeathMatchFee(defender.Level, session.Absorb, _config), ct);
        // Pre-fight snapshots — what the engine fights with — so the client can replay + verify the winner.
        var challengerSnapshot = challenger.ToDto();
        var defenderSnapshot = defender.ToDto();

        var entropy = CommitReveal.DeriveEntropy(session.ServerSeed, session.Id, challenger.Id, defender.Id, nonce);
        var result = BattleEngine.Fight(challenger, defender, entropy, _config);
        var challengerWon = result.WinnerId == challenger.Id;
        var (winner, loser) = challengerWon ? (challenger, defender) : (defender, challenger);
        var serverSeedHex = Convert.ToHexString(session.ServerSeed).ToLowerInvariant();
        var entropyHex = Convert.ToHexString(entropy).ToLowerInvariant();

        // Persist the fight-time replay data before the branches (both burn a hero) so ANY spectator can
        // watch + verify this death-match later — the same trustless replay as a wager match.
        session.Result = result;
        session.ChallengerSnapshot = challengerSnapshot;
        session.DefenderSnapshot = defenderSnapshot;
        session.EntropyHex = entropyHex;
        session.Nonce = nonce;
        session.ConfigVersion = ConfigVersion;   // stamp the rules this resolved under
        session.ContentVersion = ContentVersion; // …and the gear/dungeons it resolved with

        // ── ABSORB MODE: a seed-driven roll may RE-MINT the winner absorbing the loser's better
        // traits — BOTH heroes burn and a new hero mints under species to the winner. A failed roll
        // (or a classic match) falls through to the keep path (the winner keeps its exact hero).
        if (session.Absorb)
        {
            var outcome = Absorb.Resolve(winner.Genome, loser.Genome, entropy,
                _config.Absorb);
            if (outcome.Minted)
            {
                var absorbGen = Math.Max(winner.Generation, loser.Generation) + 1;
                var absorbedData = new HeroMintData(outcome.Result.ToHex(), absorbGen, winner.Id, loser.Id, serverSeedHex, nonce);
                // The oracle (game key) attests BOTH the winner (absorb-mint message) AND the absorbed
                // genome root; the covenant binds the burn+mint to exactly these. Chain FIRST (retryable).
                var outcomeSig = receipts.SignDigest(Chain.Covenants.ArkadeCovenants.DeathMatchAbsorbMintMessage(session.Id, challengerWon));
                var root = Chain.Covenants.ArkadeCovenants.MetadataMerkleRoot(
                    Chain.Covenants.BreedEscrowContracts.ChildMetadata(
                        absorbedData.GenomeHex, absorbedData.Generation, absorbedData.ParentAId ?? "", absorbedData.ParentBId ?? "",
                        absorbedData.ServerSeedHex ?? "", absorbedData.PlayerNonce ?? ""));
                var rootSig = receipts.SignDigest(root);
                var mint = await chain.SettleDeathMatchAbsorbMintAsync(session.Id, challengerWon, absorbedData, session.ServerSeed, outcomeSig, rootSig, ct);

                session.Completed = true;
                session.WinnerHeroId = result.WinnerId;
                // The absorbed hero is a NEW asset owned by the WINNER (the settler may be the loser).
                var winnerPlayer = store.Players[winner.OwnerId];
                var absorbed = await BuildAndStoreHero(winnerPlayer, mint, outcome.Result, absorbGen,
                    winner.Id, loser.Id, serverSeedHex, nonce, entropyHex, ct);
                absorbed.Level = winner.Level;   // the winner keeps its progression (absorb receipt attests it)
                absorbed.Name = winner.Name;     // the same hero, evolved — keep its name
                // Re-save: inherited level + kept name are BIRTH state (the merge-reveal pattern) — save
                // inside the identity event rather than let a crash rehydrate the evolution nameless at level 1.
                await persistence.SaveHeroAsync(absorbed, ct);
                // BOTH input heroes are burned on-chain — drop their server records and their durable rows.
                store.Heroes.TryRemove(winner.Id, out _);
                store.Heroes.TryRemove(loser.Id, out _);
                store.RecordBurn(); store.RecordBurn();
                await persistence.DeleteHeroAsync(winner.Id, ct);
                await persistence.DeleteHeroAsync(loser.Id, ct);

                var absorbReceipt = IssueReceipt(new Shared.ProgressionReceiptDto(
                        "absorb", session.Id, session.ChallengerHeroId, session.DefenderHeroId, absorbed.Id,
                        serverSeedHex, nonce, session.CommitmentHex,
                        0, 0, winner.Level, loser.Level,
                        DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "", ""),
                    session.ChallengerHeroId, session.DefenderHeroId, absorbed.Id);
                return (result.ToDto(), result.WinnerId, loser.Id, challengerSnapshot, defenderSnapshot,
                    serverSeedHex, entropyHex, absorbReceipt, true, outcome.TraitsAbsorbed, outcome.Result.ToHex(), absorbed.ToDto());
            }
        }

        // ── KEEP PATH (classic death-match, or an absorb roll that didn't fire): the loser's hero
        // is BURNED and the winner keeps its exact hero. The oracle signs the keep branch. Chain
        // FIRST (deterministic fight re-runs identically, so a retry surfaces the real error).
        var settleMessage = Chain.Covenants.ArkadeCovenants.DeathMatchSettleMessage(session.Id, challengerWon);
        var oracleSig = receipts.SignDigest(settleMessage);
        await chain.SettleDeathMatchAsync(session.Id, challengerWon, session.ServerSeed, oracleSig, ct);

        session.Completed = true;
        session.WinnerHeroId = result.WinnerId;
        store.Heroes.TryRemove(loser.Id, out _);
        store.RecordBurn();
        // The loser is burned on-chain — erase its durable row too, or a restart resurrects it.
        await persistence.DeleteHeroAsync(loser.Id, ct);

        var receipt = IssueReceipt(new Shared.ProgressionReceiptDto(
                "deathmatch", session.Id, session.ChallengerHeroId, session.DefenderHeroId, result.WinnerId,
                serverSeedHex, nonce, session.CommitmentHex,
                0, 0, challenger.Level, defender.Level,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "", ""),
            session.ChallengerHeroId, session.DefenderHeroId);

        return (result.ToDto(), result.WinnerId, loser.Id, challengerSnapshot, defenderSnapshot,
            serverSeedHex, entropyHex, receipt, false, 0, null, null);
    }

    // ── Matches: open (invoice) → accept (invoice) → fight ─────────────

    public async Task<(MatchSession Session, FeeInvoice? StakeInvoice, FeeInvoice? MatchFeeInvoice)> OpenMatchAsync(
        Player player, string challengerHeroId, string defenderHeroId, long wagerSats,
        string mode, CancellationToken ct)
    {
        RequireAcceptedTerms(player);
        var challenger = GetOwnedHero(player, challengerHeroId);
        var defender = GetHero(defenderHeroId);
        if (challenger.Id == defender.Id)
            throw new GameRuleException("A hero cannot fight itself.");
        if (wagerSats < 0)
            throw new GameRuleException("Wager cannot be negative.");
        if (wagerSats > 0 && defender.OwnerId == player.Id)
            throw new GameRuleException("Wagered matches need an opponent — you own both heroes.");
        if (mode is not ("invoice" or "covenant"))
            throw new GameRuleException("Match mode must be 'invoice' or 'covenant'.");
        if (mode == "covenant" && wagerSats <= 0)
            throw new GameRuleException("Covenant matches are for wagers — set WagerSats.");

        var seed = CommitReveal.NewSeed();
        var commitmentHex = CommitReveal.Commit(seed);
        var matchId = NewId("match");

        FeeInvoice? invoice = null;
        FeeInvoice? feeInvoice = null;
        string? escrowChallenger = null;
        string? escrowDefender = null;
        long? refundAfterUnix = null;
        if (wagerSats > 0)
        {
            if (mode == "covenant")
            {
                // The per-party escrow covenants bake in THIS match's seed
                // commitment, both players' addresses, the game oracle key
                // (the receipt key), and a timelocked refund leaf per party.
                var escrow = await chain.CreateWagerEscrowAsync(
                    matchId, player.Id, defender.OwnerId, wagerSats,
                    Convert.FromHexString(commitmentHex), receipts.PublicKeyHex,
                    DateTimeOffset.UtcNow.Add(_options.WagerEscrowRefundAfter).ToUnixTimeSeconds(), ct);
                escrowChallenger = escrow.ChallengerEscrowAddress;
                escrowDefender = escrow.DefenderEscrowAddress;
                refundAfterUnix = escrow.RefundAfterUnixSeconds;
            }
            else
            {
                invoice = await chain.CreateFeeInvoiceAsync($"wager-stake:challenger", wagerSats, ct);
            }

            // The per-character match fee: a level-proportional sats sink the
            // challenger pays to the treasury to stage the match (gated at fight,
            // both modes). Separate from the pot — fielding a high-level hero costs
            // sats every staked fight, whoever wins, so idle-training isn't free.
            feeInvoice = await chain.CreateFeeInvoiceAsync(
                $"match-fee:challenger:{matchId}", Leveling.MatchFee(challenger.Level, _config), ct);
        }

        var session = new MatchSession
        {
            Id = matchId,
            ChallengerPlayerId = player.Id,
            ChallengerHeroId = challenger.Id,
            DefenderHeroId = defender.Id,
            ServerSeed = seed,
            CommitmentHex = commitmentHex,
            WagerSats = wagerSats,
            Mode = mode,
            EscrowChallengerAddress = escrowChallenger,
            EscrowDefenderAddress = escrowDefender,
            ChallengerInvoiceId = invoice?.InvoiceId,
            ChallengerFeeInvoiceId = feeInvoice?.InvoiceId,
            RefundAfterUnixSeconds = refundAfterUnix,
            DefenderPlayerId = defender.OwnerId,
        };
        store.Matches[session.Id] = session;
        return (session, invoice, feeInvoice);
    }

    /// <summary>
    /// Defender's owner accepts a wagered match. Invoice mode: they receive
    /// their stake invoice. Covenant mode: acceptance is consent — they stake
    /// by paying the escrow address from their own wallet.
    /// </summary>
    public async Task<(MatchSession Session, FeeInvoice? StakeInvoice, FeeInvoice? MatchFeeInvoice)> AcceptMatchAsync(
        Player player, string matchId, CancellationToken ct)
    {
        RequireAcceptedTerms(player);
        if (!store.Matches.TryGetValue(matchId, out var session))
            throw new GameRuleException($"Unknown match '{matchId}'.");
        if (session.WagerSats == 0)
            throw new GameRuleException("Friendly matches don't need acceptance — the challenger can fight directly.");
        if (session.Status != "open")
            throw new GameRuleException($"Match is {session.Status}, not open.");

        var defender = GetHero(session.DefenderHeroId);
        if (defender.OwnerId != player.Id)
            throw new GameRuleException("Only the defender hero's owner can accept this match.");

        FeeInvoice? invoice = null;
        if (session.Mode == "invoice")
        {
            invoice = await chain.CreateFeeInvoiceAsync($"wager-stake:defender:{matchId}", session.WagerSats, ct);
            session.DefenderInvoiceId = invoice.InvoiceId;
        }
        // The defender's per-character match fee, proportional to their OWN level
        // (both modes) — the same sats sink the challenger paid at open.
        var feeInvoice = await chain.CreateFeeInvoiceAsync(
            $"match-fee:defender:{matchId}", Leveling.MatchFee(defender.Level, _config), ct);
        session.DefenderFeeInvoiceId = feeInvoice.InvoiceId;
        session.DefenderPlayerId = player.Id;
        session.Status = "accepted";
        return (session, invoice, feeInvoice);
    }

    /// <summary>
    /// Marks stale covenant matches 'expired' so the match list drops them: past
    /// its refund window, an OPEN match whose challenger stake is gone (never
    /// staked, or refunded) or an ACCEPTED match missing either stake is
    /// abandoned. A still-fully-funded match stays visible — it can yet settle or
    /// be refunded. Within the window nothing is touched, so a live pending match
    /// is never mis-marked (this is why it needs PER-PARTY funding — a single
    /// both-parties probe can't tell "defender hasn't staked yet" from "challenger
    /// refunded"). Runs lazily on match listing; a no-op in invoice mode.
    /// </summary>
    public async Task ReconcileAbandonedMatchesAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var m in store.Matches.Values)
        {
            if (m.Mode != "covenant" || m.Status is not ("open" or "accepted")) continue;
            if (m.RefundAfterUnixSeconds is not { } refundAfter || now < refundAfter) continue;
            var funding = await chain.GetWagerEscrowFundingAsync(m.Id, ct);
            if (funding is null) continue;
            var abandoned = m.Status == "open"
                ? !funding.ChallengerFunded
                : !(funding.ChallengerFunded && funding.DefenderFunded);
            if (abandoned) m.Status = "expired";
        }
    }

    /// <summary>
    /// XP-weighted matchmaking: OTHER players' heroes ranked by how evenly matched
    /// they are with the given hero (closest level first), each annotated with the
    /// conserved XP swing — what a staked win would gain and a loss would cost — so
    /// a player finds fights where XP is actually at stake, not lopsided ones.
    /// </summary>
    public IReadOnlyList<Shared.OpponentSuggestionDto> SuggestOpponents(Player player, string heroId, int? take = null)
    {
        var hero = GetOwnedHero(player, heroId);
        var heroPower = PowerScore.Compute(hero, _config);
        return store.Heroes.Values
            .Where(h => h.OwnerId != player.Id)
            .Select(h =>
            {
                var oppPower = PowerScore.Compute(h, _config);
                return new Shared.OpponentSuggestionDto(
                    h.ToDto(), h.OwnerId,
                    Matchmaking.LevelGap(hero.Level, h.Level),
                    Matchmaking.XpIfWin(hero.Level, h.Level),
                    Matchmaking.XpIfLose(hero.Level, h.Level),
                    oppPower,
                    Matchmaking.PowerGapPercent(heroPower, oppPower),
                    // F2: the free-underdog-shot label rides along the (level-based) conserved swings.
                    Matchmaking.Favor(hero.Level, h.Level));
            })
            // Closest realized-power fights first (F18); level gap + a stable id keep a total order.
            .OrderBy(s => s.PowerGapPercent)
            .ThenBy(s => s.LevelGap)
            .ThenBy(s => s.Hero.Id, StringComparer.Ordinal)
            .Take(take ?? _config.MatchmakingTake)
            .ToList();
    }

    /// <summary>The current season's ranked ladder: staked-match wins tallied over the receipts that fall
    /// within the season window (reusing <see cref="Shared.LeaderboardBuilder"/>), plus when the season ends.
    /// Trustless + auto-resetting — computed from the signed receipts, and the window rolls with the clock.</summary>
    public Task<Shared.SeasonLeaderboardDto> SeasonLeaderboard(CancellationToken ct = default) =>
        SeasonLeaderboardAt(DateTimeOffset.UtcNow, ct);

    /// <summary>The season board at an explicit <paramref name="now"/> (the test seam — no injectable clock):
    /// settle anything that ended, then project the current window's standings + live pot + last settlement.</summary>
    public async Task<Shared.SeasonLeaderboardDto> SeasonLeaderboardAt(DateTimeOffset now, CancellationToken ct)
    {
        await SettleDueSeasonsAsync(now, ct);
        return SeasonSnapshotAt(now);
    }

    /// <summary>The season board WITHOUT the settle — the same projection <see cref="SeasonLeaderboardAt"/>
    /// returns, minus the lazy payout that runs first. A pure read over live state.
    ///
    /// It exists for the operator console, which must be able to look at the season without moving a sat:
    /// reading the player-facing board triggers <see cref="SettleDueSeasonsAsync"/>, and an analytics view
    /// that pays out prizes as a side effect of being opened is not an analytics view. The settle stays
    /// exactly where it was for every existing caller.</summary>
    public Shared.SeasonLeaderboardDto SeasonSnapshotAt(DateTimeOffset now)
    {
        var season = Season.Current(now, _config.SeasonLengthDays);
        var standings = SeasonStandings(season);
        var pot = _config.SeasonPotBaseSats + store.SeasonFeeAccrual.GetValueOrDefault(season.Number);
        return new Shared.SeasonLeaderboardDto(
            season.Number, season.End.ToUnixTimeSeconds(), pot, standings, store.LastSettlement);
    }

    /// <summary>The ranked standings within a season window — staked-match wins tallied from receipts,
    /// idle heroes dropped, re-ranked 1..N. Reused by the current board and by settlement.</summary>
    private List<Shared.LeaderboardEntryDto> SeasonStandings(SeasonInfo season)
    {
        var startUnix = season.Start.ToUnixTimeSeconds();
        var endUnix = season.End.ToUnixTimeSeconds();
        var heroes = store.Heroes.Values.ToDictionary(h => h.Id, h => (h.Name, h.Level, h.OwnerId));
        var receipts = store.ReceiptsByHero.Values.SelectMany(list => list).DistinctBy(r => r.Id)
            .Where(r => r.UnixSeconds >= startUnix && r.UnixSeconds < endUnix);
        return Shared.LeaderboardBuilder.Build(heroes, receipts)
            .Where(e => e.Matches > 0)
            .Select((e, i) => e with { Rank = i + 1 })
            .ToList();
    }

    /// <summary>Pay out any ended-but-unsettled seasons (lazy, on board read). Under a lock; the settled
    /// marker is advanced BEFORE paying, so a crash/concurrent read can't re-settle → no double-pay (the
    /// marker + the winner-defining receipts drop together on restart).</summary>
    private async Task SettleDueSeasonsAsync(DateTimeOffset now, CancellationToken ct)
    {
        var current = Season.Current(now, _config.SeasonLengthDays).Number;
        if (!SeasonPrize.DueSeasons(store.LastSettledSeason, current).Any()) return;

        await store.SettleLock.WaitAsync(ct);
        try
        {
            foreach (var s in SeasonPrize.DueSeasons(store.LastSettledSeason, current))
            {
                // A prize needs a WIN behind it. The board ranks on wins but falls through to level and
                // match count when they tie, so a season in which nobody won anything still had a "top
                // three" — and paid them, out of a pot that includes a treasury-funded base. That is
                // reachable without trying: a fight between two heroes with no XP banked moves nothing, so
                // every entrant sits on zero wins, and the payout then went to whoever had the highest
                // level and the most matches, both of which cost only fees to pump. Requiring a win reuses
                // the no-competitors path below, which already retains the pot rather than forcing it out.
                var standings = SeasonStandings(Season.ForNumber(s, _config.SeasonLengthDays))
                    .Where(e => e.Wins > 0)
                    .Take(3)
                    .ToList();
                var pot = _config.SeasonPotBaseSats + store.SeasonFeeAccrual.GetValueOrDefault(s);
                if (standings.Count == 0) { store.LastSettledSeason = s; continue; }   // no competitors / receipts gone
                if (await chain.TreasuryBalanceAsync(ct) < pot) break;                 // underfunded → retry on a later read

                var shares = SeasonPrize.Split(pot, standings.Count, SeasonPrize.Weights);
                store.LastSettledSeason = s;   // commit BEFORE paying → no double-pay
                store.LastSettlement = new Shared.SeasonSettlementDto(s, pot,
                    standings.Select((e, i) => new Shared.SeasonWinnerDto(e.Rank, e.Name, shares[i])).ToList());
                for (var i = 0; i < standings.Count; i++)
                {
                    var tag = $"season:{s}:rank{standings[i].Rank}";
                    // Never retried (documented v1 limit), so the LOG is the only record of the debt. Payout
                    // and booking are caught separately because a booking failure means the sats DID move —
                    // reading that as "unpaid" is how a manual reconciliation double-pays a champion.
                    try { await chain.PayoutAsync(standings[i].OwnerId, shares[i], tag, ct); }
                    catch (Exception ex)
                    {
                        logger?.LogError(ex,
                            "Season prize payout FAILED and will never be retried: player {PlayerId} is owed "
                            + "{Sats} sats for {Tag}. Settle it by hand — nothing else records this debt.",
                            standings[i].OwnerId, shares[i], tag);
                        continue;
                    }
                    try { await store.RecordOutflowAsync("season", shares[i], ct); }
                    catch (Exception ex)
                    {
                        logger?.LogError(ex,
                            "Season prize of {Sats} sats for {Tag} WAS PAID but could not be booked as "
                            + "outflow. The player has their sats; do NOT re-pay. Treasury outflow now "
                            + "under-reports by this amount.", shares[i], tag);
                    }
                }
            }
        }
        finally { store.SettleLock.Release(); }
    }

    // ── Daily engagement loop ──────────────────────────────────────────────────────────────────
    // A once-per-UTC-day claim: a small base + a bonus per completed daily quest (server-verified,
    // derived from the receipt log), scaled by a login streak, paid from the treasury. Day/streak/
    // reward math is pure Core (Daily/DailyStreak/DailyReward); quests are Shared (DailyQuests).

    /// <summary>Today's daily-loop state for a player: the day's quests + which are done (from the
    /// player's in-window receipts), the projected streak, and what a claim right now would pay.</summary>
    public Shared.DailyStatusDto DailyStatus(Player player)
    {
        var window = Daily.ForDay(DateTimeOffset.UtcNow);
        var (heroIds, receipts) = DailyReceiptsInWindow(player, window);
        var quests = Shared.DailyQuests.ForDay(window.DayIndex, _config.DailyQuestsPerDay);

        var questDtos = quests.Select(q => new Shared.DailyQuestDto(
            q.Id, q.Title, _config.DailyQuestBonusSats,
            Shared.DailyQuests.IsComplete(q, receipts, heroIds))).ToList();

        var claimedToday = player.LastClaimDay == window.DayIndex;
        // The reward previews at the streak the claim will RESULT in (post-increment), so ClaimableNow
        // matches the payout; the displayed Streak is the player's CURRENT standing (0 when fresh, and
        // already-incremented once claimed today) — standard streak-counter semantics.
        var rewardStreak = claimedToday
            ? player.StreakCount
            : DailyStreak.Next(player.LastClaimDay, window.DayIndex, player.StreakCount);
        var reward = DailyReward.Compute(_config, questDtos.Count(q => q.Done), rewardStreak);

        return new Shared.DailyStatusDto(
            window.DayIndex, window.End.ToUnixTimeSeconds(), claimedToday, player.StreakCount,
            _config.DailyBaseSats, questDtos,
            ClaimableNowSats: claimedToday ? 0 : reward.Total,
            ProjectedSats: reward.Total);
    }

    /// <summary>Claim the daily reward: base + bonus per completed quest, streak-scaled, paid from the
    /// treasury. Once per UTC day; the day is consumed durably BEFORE the payout (a crash mid-payout must
    /// not let a restart re-pay it), and a cleanly failed payout releases it in memory so the player can
    /// retry without losing the day.</summary>
    public async Task<Shared.DailyClaimResultDto> ClaimDailyAsync(Player player, CancellationToken ct)
    {
        // Refused outright unless the operator opened the faucet. Default-off because on an open signup
        // this pays real sats to anyone who can make a keypair, and a keypair is free.
        if (!_options.DailyRewardEnabled)
            throw new GameRuleException("The daily reward is not available on this server.");

        // A wallet with no heroes is not a player, it is an address. Requiring one raises the cost of a
        // farmed account from "generate a key" to "also claim starters", which is the gate that actually
        // has a price attached — and it keeps the faucet pointed at people who are playing. Cheap enough
        // to check before taking the lock: it moves no money, and racing a concurrent starter claim can
        // only refuse a reward the player can immediately retry for.
        if (!store.Heroes.Values.Any(h => h.OwnerId == player.Id))
            throw new GameRuleException("Claim your starter heroes before collecting a daily reward.");

        // Per-player gate: the claimed-today check, the day-consuming write, and the payout below must
        // be one atomic step — the client poll-retries, so two concurrent claims would otherwise both
        // pass the guard and the faucet would pay twice off the same treasury reading.
        using var gate = await store.LockAsync($"daily:{player.Id}", ct);
        var window = Daily.ForDay(DateTimeOffset.UtcNow);
        if (player.LastClaimDay == window.DayIndex)
            throw new GameRuleException("Daily reward already claimed today.");

        var (heroIds, receipts) = DailyReceiptsInWindow(player, window);
        var quests = Shared.DailyQuests.ForDay(window.DayIndex, _config.DailyQuestsPerDay);
        var completed = quests.Where(q => Shared.DailyQuests.IsComplete(q, receipts, heroIds)).ToList();

        var newStreak = DailyStreak.Next(player.LastClaimDay, window.DayIndex, player.StreakCount);
        var reward = DailyReward.Compute(_config, completed.Count, newStreak);

        // Faucet governor: sats are real BTC — the treasury is a finite, fee-funded pot it can't inflate.
        // Never overdraw it: pay only what it can afford (down to zero) rather than throwing "Treasury cannot
        // cover". The streak still advances (the player showed up), and emission auto-tracks treasury health.
        // Reserve = the permanent floor, plus (opt-in) the current season pot so daily emission can't drain the
        // sats the upcoming season settlement owes — the season-starvation gap a fixed floor can't auto-track.
        var reserved = _config.TreasuryReserveFloorSats;
        if (_config.ReserveSeasonPot)
        {
            var season = Season.Current(DateTimeOffset.UtcNow, _config.SeasonLengthDays);
            reserved += _config.SeasonPotBaseSats + store.SeasonFeeAccrual.GetValueOrDefault(season.Number);
        }
        var affordable = Math.Clamp(await chain.TreasuryBalanceAsync(ct) - reserved, 0, reward.Total);

        // Consume the day BEFORE any sat moves, and durably — mirroring the starter reservation and the
        // tournament resolved marker: PayoutAsync is NOT idempotent, so a crash between a payout and a
        // later write would let a restart rehydrate this player as unclaimed and pay the same day twice.
        var prevClaimDay = player.LastClaimDay;
        var prevStreak = player.StreakCount;
        player.LastClaimDay = window.DayIndex;   // consume the day even at a partial/zero payout
        player.StreakCount = newStreak;
        await persistence.SavePlayerAsync(player, ct);

        if (affordable > 0)
        {
            try
            {
                await chain.PayoutAsync(player.Id, affordable, $"daily:{window.DayIndex}", ct);
                await store.RecordOutflowAsync("daily", affordable, ct);
            }
            catch
            {
                // A cleanly failed payout releases the day IN MEMORY ONLY, so the player can retry in this
                // process. Never re-persist the release: if the payout actually settled before throwing,
                // the durable consume is the one thing keeping a restart from paying this day twice.
                player.LastClaimDay = prevClaimDay;
                player.StreakCount = prevStreak;
                throw;
            }
        }

        return new Shared.DailyClaimResultDto(
            affordable, newStreak, reward.Base, reward.QuestBonus, reward.StreakBonusPct,
            completed.Select(q => q.Id).ToList());
    }

    /// <summary>The player's heroes' receipts falling inside a day window, plus the hero-id set.</summary>
    private (HashSet<string> HeroIds, List<Shared.ProgressionReceiptDto> Receipts) DailyReceiptsInWindow(
        Player player, DailyWindow window)
    {
        var heroIds = store.Heroes.Values.Where(h => h.OwnerId == player.Id).Select(h => h.Id).ToHashSet();
        var startUnix = window.Start.ToUnixTimeSeconds();
        var endUnix = window.End.ToUnixTimeSeconds();
        var receipts = store.ReceiptsByHero
            .Where(kv => heroIds.Contains(kv.Key))
            .SelectMany(kv => kv.Value)
            .Where(r => r.UnixSeconds >= startUnix && r.UnixSeconds < endUnix)
            .DistinctBy(r => r.Id)
            .ToList();
        return (heroIds, receipts);
    }

    public async Task<(MatchSession Session, BattleResult Result, string ServerSeedHex, string EntropyHex,
        long ChallengerXp, long DefenderXp,
        Shared.HeroDto ChallengerSnapshot, Shared.HeroDto DefenderSnapshot, long WinnerPayout,
        Shared.ProgressionReceiptDto Receipt)>
        FightAsync(Player player, string matchId, string nonce, CancellationToken ct)
    {
        if (!store.Matches.TryGetValue(matchId, out var session) || session.ChallengerPlayerId != player.Id)
            throw new GameRuleException($"Unknown match '{matchId}'.");
        // Per-match gate: status-check → fight → pot payout → resolved-set must be one atomic step, or
        // two concurrent resolves both see "accepted" — double XP in any mode, and in invoice mode the
        // treasury pays the pot twice.
        using var gate = await store.LockAsync($"match:{session.Id}", ct);
        var fightable = session.Status == "accepted" || (session.Status == "open" && session.WagerSats == 0);
        if (!fightable)
            throw new GameRuleException(session.Status == "open"
                ? "This wagered match is waiting for the defender's owner to accept."
                : "Match already resolved.");
        if (string.IsNullOrWhiteSpace(nonce)) throw new GameRuleException("A nonce is required.");

        // Wagered: both stakes must actually sit on-chain — at the invoice
        // addresses (invoice mode) or at the escrow covenant (covenant mode).
        if (session.WagerSats > 0)
        {
            if (session.Mode == "covenant")
            {
                if (!await chain.IsEscrowFundedAsync(session.Id, ct))
                    throw new GameRuleException(
                        $"The escrow is not fully funded — each player must stake {session.WagerSats} sats to their own escrow address.");
            }
            else
            {
                if (!await chain.IsInvoicePaidAsync(session.ChallengerInvoiceId!, ct))
                    throw new GameRuleException("Your stake invoice is unpaid — pay it from your wallet first.");
                if (session.DefenderInvoiceId is null || !await chain.IsInvoicePaidAsync(session.DefenderInvoiceId, ct))
                    throw new GameRuleException("The defender's stake invoice is unpaid.");
            }

            // Both fighters must have paid their per-character match fee (the
            // level-proportional sats sink), whichever mode holds the stakes.
            if (session.ChallengerFeeInvoiceId is null || !await chain.IsInvoicePaidAsync(session.ChallengerFeeInvoiceId, ct))
                throw new GameRuleException("Your match fee is unpaid — pay the per-character fee invoice from your wallet first.");
            if (session.DefenderFeeInvoiceId is null || !await chain.IsInvoicePaidAsync(session.DefenderFeeInvoiceId, ct))
                throw new GameRuleException("The defender's match fee is unpaid.");
        }

        var challenger = GetHero(session.ChallengerHeroId);
        var defender = GetHero(session.DefenderHeroId);
        // Match fees (staked fights; both confirmed above) — treasury captures, tallied once per invoice.
        if (session.ChallengerFeeInvoiceId is not null)
            await store.RecordInflowAsync(session.ChallengerFeeInvoiceId, "match", Leveling.MatchFee(challenger.Level, _config), ct);
        if (session.DefenderFeeInvoiceId is not null)
            await store.RecordInflowAsync(session.DefenderFeeInvoiceId, "match", Leveling.MatchFee(defender.Level, _config), ct);

        // Snapshot pre-fight state (level, equipment) — what the engine actually
        // fights with — so clients can replay and verify.
        var challengerSnapshot = challenger.ToDto();
        var defenderSnapshot = defender.ToDto();

        var entropy = CommitReveal.DeriveEntropy(
            session.ServerSeed, session.Id, challenger.Id, defender.Id, nonce);
        var result = BattleEngine.Fight(challenger, defender, entropy, _config);

        var challengerWon = result.WinnerId == challenger.Id;
        var (winner, loser) = challengerWon ? (challenger, defender) : (defender, challenger);

        // Wager settlement — chain FIRST, latch + in-memory effects after (the breed-reveal /
        // death-match pattern): if the settle or payout faults, the match stays "accepted" and the
        // heroes untouched, so the escrowed pot can be retried (same seed + nonce → the same winner)
        // instead of stranded behind a "resolved" flag with the winner unpaid. Covenant mode sweeps
        // the escrow to the winner via the emulator-enforced covenant (revealing the committed seed);
        // invoice mode pays out from the treasury.
        long winnerPayout = 0;
        if (session.WagerSats > 0)
        {
            winnerPayout = session.WagerSats * 2;
            if (session.Mode == "covenant")
            {
                // The oracle authorization: the game key signs exactly one
                // (match, winner-branch) message the covenant script pins.
                var settleMessage = Chain.Covenants.ArkadeCovenants.SettleMessage(session.Id, challengerWon);
                var oracleSignature = receipts.SignDigest(settleMessage);
                await chain.SettleWagerEscrowAsync(session.Id, challengerWon, session.ServerSeed, oracleSignature, ct);
            }
            else
            {
                var winnerOwnerId = challengerWon ? session.ChallengerPlayerId : session.DefenderPlayerId!;
                await chain.PayoutAsync(winnerOwnerId, winnerPayout, $"wager-pot:{session.Id}", ct);
                await store.RecordOutflowAsync("wager", winnerPayout, ct);
            }

            // Season prize pool: a slice of this staked match's fees accrues to the current season's pot.
            var seasonFee = Leveling.MatchFee(challenger.Level, _config) + Leveling.MatchFee(defender.Level, _config);
            var seasonAccrue = seasonFee * _config.SeasonFeeAccrualPct / 100;
            if (seasonAccrue > 0)
                store.SeasonFeeAccrual.AddOrUpdate(
                    Season.Current(DateTimeOffset.UtcNow, _config.SeasonLengthDays).Number,
                    seasonAccrue, (_, cur) => cur + seasonAccrue);
        }

        // Staked fights only: XP is a CONSERVED transfer from loser to winner,
        // scaled by the level gap (pre-fight levels). Friendly fights are
        // practice — no XP. The loser can DELEVEL, so a champion is held by
        // winning, not bought. No on-chain XP mirror: a losable ladder can't be a
        // non-custodial asset you'd have to claw back — progression stays
        // receipt-based (the receipts are the audit trail; the server is the ledger).
        var transfer = session.WagerSats > 0
            ? Leveling.PayableTransfer(winner.Level, loser.Level, loser.Xp, _config)
            : 0;
        ApplyXp(winner, transfer);
        ApplyXp(loser, -transfer);
        var challengerDelta = challengerWon ? transfer : -transfer;
        var defenderDelta = -challengerDelta;

        session.Status = "resolved";
        session.Result = result;
        session.ChallengerSnapshot = challengerSnapshot;   // persist the fight-time snapshots for spectator replay
        session.DefenderSnapshot = defenderSnapshot;
        session.Nonce = nonce;
        session.EntropyHex = Convert.ToHexString(entropy).ToLowerInvariant();
        session.ConfigVersion = ConfigVersion;   // stamp the rules this resolved under
        session.ContentVersion = ContentVersion; // …and the gear/dungeons it resolved with

        var serverSeedHexOut = Convert.ToHexString(session.ServerSeed).ToLowerInvariant();
        // Friendly (unstaked) fights are practice: they carry no XP and must NOT feed the
        // ranked leaderboard (else a lone player could farm free wins to #1). Tag them so
        // LeaderboardBuilder — which counts only "match" receipts — ignores them.
        var receipt = IssueReceipt(new Shared.ProgressionReceiptDto(
                session.WagerSats > 0 ? "match" : "friendly", session.Id, challenger.Id, defender.Id, result.WinnerId,
                serverSeedHexOut, nonce, session.CommitmentHex,
                challengerDelta,
                defenderDelta,
                challenger.Level, defender.Level,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "", ""),
            challenger.Id, defender.Id);

        return (session, result,
            serverSeedHexOut,
            session.EntropyHex,
            challengerDelta,
            defenderDelta,
            challengerSnapshot, defenderSnapshot, winnerPayout, receipt);
    }

    // ── Team 3v3 squad matches: a positional best-of-3 relay, reusing the wager escrow + BattleEngine ──

    private IReadOnlyList<Hero> ValidateLineup(Player player, IReadOnlyList<string> lineup, bool owned)
    {
        if (lineup.Count != SquadBattle.LineupSize)
            throw new GameRuleException($"A squad lineup must be exactly {SquadBattle.LineupSize} heroes.");
        if (lineup.Distinct().Count() != lineup.Count)
            throw new GameRuleException("A squad lineup must be three distinct heroes.");
        return lineup.Select(id => owned ? GetOwnedHero(player, id) : GetHero(id)).ToList();
    }

    public async Task<(SquadMatchSession Session, FeeInvoice? StakeInvoice, FeeInvoice? MatchFeeInvoice)> OpenSquadMatchAsync(
        Player player, Shared.OpenSquadMatchRequest req, CancellationToken ct)
    {
        RequireAcceptedTerms(player);
        var challengerLineup = ValidateLineup(player, req.ChallengerLineup, owned: true);
        var defenderLineup = ValidateLineup(player, req.DefenderLineup, owned: false);
        if (req.ChallengerLineup.Intersect(req.DefenderLineup).Any())
            throw new GameRuleException("The two lineups must not share a hero.");
        if (req.WagerSats < 0) throw new GameRuleException("Wager cannot be negative.");
        if (req.Mode is not ("invoice" or "covenant")) throw new GameRuleException("Match mode must be 'invoice' or 'covenant'.");
        if (req.Mode == "covenant" && req.WagerSats <= 0) throw new GameRuleException("Covenant matches are for wagers — set WagerSats.");

        var defenderOwner = defenderLineup[0].OwnerId;
        if (req.WagerSats > 0 && (defenderOwner == player.Id || defenderLineup.Any(h => h.OwnerId != defenderOwner)))
            throw new GameRuleException("A wagered squad match needs one opponent who owns all three defender heroes.");

        var seed = CommitReveal.NewSeed();
        var commitmentHex = CommitReveal.Commit(seed);
        var matchId = NewId("squad");

        FeeInvoice? invoice = null, feeInvoice = null;
        string? escrowChallenger = null, escrowDefender = null;
        long? refundAfterUnix = null;
        if (req.WagerSats > 0)
        {
            if (req.Mode == "covenant")
            {
                var escrow = await chain.CreateWagerEscrowAsync(matchId, player.Id, defenderOwner, req.WagerSats,
                    Convert.FromHexString(commitmentHex), receipts.PublicKeyHex,
                    DateTimeOffset.UtcNow.Add(_options.WagerEscrowRefundAfter).ToUnixTimeSeconds(), ct);
                escrowChallenger = escrow.ChallengerEscrowAddress;
                escrowDefender = escrow.DefenderEscrowAddress;
                refundAfterUnix = escrow.RefundAfterUnixSeconds;
            }
            else
            {
                invoice = await chain.CreateFeeInvoiceAsync("squad-stake:challenger", req.WagerSats, ct);
            }
            // One match fee per side, based on the lineup's TOP level (not 3×).
            feeInvoice = await chain.CreateFeeInvoiceAsync($"squad-fee:challenger:{matchId}",
                Leveling.MatchFee(challengerLineup.Max(h => h.Level), _config), ct);
        }

        var session = new SquadMatchSession
        {
            Id = matchId,
            ChallengerPlayerId = player.Id,
            ChallengerLineup = req.ChallengerLineup.ToList(),
            DefenderLineup = req.DefenderLineup.ToList(),
            ServerSeed = seed,
            CommitmentHex = commitmentHex,
            WagerSats = req.WagerSats,
            Mode = req.Mode,
            EscrowChallengerAddress = escrowChallenger,
            EscrowDefenderAddress = escrowDefender,
            ChallengerInvoiceId = invoice?.InvoiceId,
            ChallengerFeeInvoiceId = feeInvoice?.InvoiceId,
            RefundAfterUnixSeconds = refundAfterUnix,
            DefenderPlayerId = req.WagerSats > 0 ? defenderOwner : null,
        };
        store.SquadMatches[session.Id] = session;
        return (session, invoice, feeInvoice);
    }

    public async Task<(SquadMatchSession Session, FeeInvoice? StakeInvoice, FeeInvoice? MatchFeeInvoice)> AcceptSquadMatchAsync(
        Player player, string matchId, CancellationToken ct)
    {
        RequireAcceptedTerms(player);
        if (!store.SquadMatches.TryGetValue(matchId, out var session))
            throw new GameRuleException($"Unknown squad match '{matchId}'.");
        if (session.WagerSats == 0) throw new GameRuleException("Friendly squad matches don't need acceptance.");
        if (session.Status != "open") throw new GameRuleException($"Squad match is {session.Status}, not open.");
        var defenders = session.DefenderLineup.Select(GetHero).ToList();
        if (defenders.Any(h => h.OwnerId != player.Id))
            throw new GameRuleException("Only the defender lineup's owner can accept this squad match.");

        FeeInvoice? invoice = null;
        if (session.Mode == "invoice")
        {
            invoice = await chain.CreateFeeInvoiceAsync($"squad-stake:defender:{matchId}", session.WagerSats, ct);
            session.DefenderInvoiceId = invoice.InvoiceId;
        }
        var feeInvoice = await chain.CreateFeeInvoiceAsync($"squad-fee:defender:{matchId}",
            Leveling.MatchFee(defenders.Max(h => h.Level), _config), ct);
        session.DefenderFeeInvoiceId = feeInvoice.InvoiceId;
        session.DefenderPlayerId = player.Id;
        session.Status = "accepted";
        return (session, invoice, feeInvoice);
    }

    public async Task<(SquadMatchSession Session, SquadResult Result, string ServerSeedHex, string EntropyHex,
        IReadOnlyList<Shared.HeroDto> ChallengerSnapshots, IReadOnlyList<Shared.HeroDto> DefenderSnapshots,
        long WinnerPayout, IReadOnlyList<Shared.ProgressionReceiptDto> Receipts)>
        ResolveSquadMatchAsync(Player player, string matchId, string nonce, CancellationToken ct)
    {
        if (!store.SquadMatches.TryGetValue(matchId, out var session) || session.ChallengerPlayerId != player.Id)
            throw new GameRuleException($"Unknown squad match '{matchId}'.");
        // Per-match gate: the same double-resolve race as FightAsync (per-duel XP + one pot payout).
        using var gate = await store.LockAsync($"squad:{session.Id}", ct);
        var fightable = session.Status == "accepted" || (session.Status == "open" && session.WagerSats == 0);
        if (!fightable)
            throw new GameRuleException(session.Status == "open"
                ? "This wagered squad match is waiting for the defender's owner to accept."
                : "Squad match already resolved.");
        if (string.IsNullOrWhiteSpace(nonce)) throw new GameRuleException("A nonce is required.");

        if (session.WagerSats > 0)
        {
            if (session.Mode == "covenant")
            {
                if (!await chain.IsEscrowFundedAsync(session.Id, ct))
                    throw new GameRuleException($"The escrow is not fully funded — each player must stake {session.WagerSats} sats to their own escrow address.");
            }
            else
            {
                if (!await chain.IsInvoicePaidAsync(session.ChallengerInvoiceId!, ct))
                    throw new GameRuleException("Your stake invoice is unpaid — pay it from your wallet first.");
                if (session.DefenderInvoiceId is null || !await chain.IsInvoicePaidAsync(session.DefenderInvoiceId, ct))
                    throw new GameRuleException("The defender's stake invoice is unpaid.");
            }
            if (session.ChallengerFeeInvoiceId is null || !await chain.IsInvoicePaidAsync(session.ChallengerFeeInvoiceId, ct))
                throw new GameRuleException("Your match fee is unpaid.");
            if (session.DefenderFeeInvoiceId is null || !await chain.IsInvoicePaidAsync(session.DefenderFeeInvoiceId, ct))
                throw new GameRuleException("The defender's match fee is unpaid.");
        }

        var challengers = session.ChallengerLineup.Select(GetHero).ToList();
        var defenders = session.DefenderLineup.Select(GetHero).ToList();
        // Squad match fees (both confirmed above) — treasury captures, tallied once per invoice.
        if (session.ChallengerFeeInvoiceId is not null)
            await store.RecordInflowAsync(session.ChallengerFeeInvoiceId, "squad-fee", Leveling.MatchFee(challengers.Max(h => h.Level), _config), ct);
        if (session.DefenderFeeInvoiceId is not null)
            await store.RecordInflowAsync(session.DefenderFeeInvoiceId, "squad-fee", Leveling.MatchFee(defenders.Max(h => h.Level), _config), ct);
        var challengerSnapshots = challengers.Select(h => h.ToDto()).ToList();
        var defenderSnapshots = defenders.Select(h => h.ToDto()).ToList();

        var entropy = CommitReveal.DeriveEntropy(session.ServerSeed, "squad", session.Id, nonce);
        var result = SquadBattle.Resolve(challengers, defenders, entropy, _config);
        var challengerWon = result.ChallengerWon;

        // Settle the pot ONCE, to the best-of-3 winner (reuses the wager escrow / treasury payout) —
        // chain FIRST, latch + per-duel effects after (the FightAsync pattern): a faulted settle leaves
        // the match "accepted" and the duels unscored, so the escrowed pot can be retried (same seed +
        // nonce → the same relay) instead of stranded behind a "resolved" flag.
        long winnerPayout = 0;
        if (session.WagerSats > 0)
        {
            winnerPayout = session.WagerSats * 2;
            if (session.Mode == "covenant")
            {
                var settleMessage = Chain.Covenants.ArkadeCovenants.SettleMessage(session.Id, challengerWon);
                var oracleSignature = receipts.SignDigest(settleMessage);
                await chain.SettleWagerEscrowAsync(session.Id, challengerWon, session.ServerSeed, oracleSignature, ct);
            }
            else
            {
                var winnerOwnerId = challengerWon ? session.ChallengerPlayerId : session.DefenderPlayerId!;
                await chain.PayoutAsync(winnerOwnerId, winnerPayout, $"squad-pot:{session.Id}", ct);
                await store.RecordOutflowAsync("squad", winnerPayout, ct);
            }
            var seasonFee = Leveling.MatchFee(challengers.Max(h => h.Level), _config) + Leveling.MatchFee(defenders.Max(h => h.Level), _config);
            var seasonAccrue = seasonFee * _config.SeasonFeeAccrualPct / 100;
            if (seasonAccrue > 0)
                store.SeasonFeeAccrual.AddOrUpdate(
                    Season.Current(DateTimeOffset.UtcNow, _config.SeasonLengthDays).Number,
                    seasonAccrue, (_, cur) => cur + seasonAccrue);
        }

        // Per-duel conserved XP transfer + one "match" receipt per duel (feeds the season ladder + prize pool).
        var serverSeedHexOut = Convert.ToHexString(session.ServerSeed).ToLowerInvariant();
        var duelReceipts = new List<Shared.ProgressionReceiptDto>();
        foreach (var duel in result.Duels)
        {
            var c = challengers[duel.Slot];
            var d = defenders[duel.Slot];
            var cWon = duel.Result.WinnerId == c.Id;
            var (w, l) = cWon ? (c, d) : (d, c);
            var transfer = session.WagerSats > 0
                ? Leveling.PayableTransfer(w.Level, l.Level, l.Xp, _config)
                : 0;
            ApplyXp(w, transfer);
            ApplyXp(l, -transfer);
            var cDelta = cWon ? transfer : -transfer;
            duelReceipts.Add(IssueReceipt(new Shared.ProgressionReceiptDto(
                    session.WagerSats > 0 ? "match" : "friendly", $"{session.Id}:{duel.Slot}", c.Id, d.Id, duel.Result.WinnerId,
                    serverSeedHexOut, nonce, session.CommitmentHex, cDelta, -cDelta, c.Level, d.Level,
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "", ""),
                c.Id, d.Id));
        }

        session.Status = "resolved";
        session.Result = result;
        session.ChallengerSnapshots = challengerSnapshots;
        session.DefenderSnapshots = defenderSnapshots;
        session.Nonce = nonce;
        session.EntropyHex = Convert.ToHexString(entropy).ToLowerInvariant();
        session.ConfigVersion = ConfigVersion;   // stamp the rules this resolved under
        session.ContentVersion = ContentVersion; // …and the gear/dungeons it resolved with

        return (session, result, serverSeedHexOut, session.EntropyHex, challengerSnapshots, defenderSnapshots, winnerPayout, duelReceipts);
    }

    private void ApplyXp(Hero hero, long award)
    {
        var (level, xp, _) = Leveling.Apply(hero.Level, hero.Xp, award, _config);
        hero.Level = level;
        hero.Xp = xp;
        // Every level/XP change funnels through here (gauntlet, duels, squads) — one mark covers them all.
        // PROGRESSION rides the periodic flush, not an inline save: XP moves on every fight, and grinding
        // is re-earnable in a way identity is not, so the bounded flush window is an accepted loss.
        store.MarkHeroDirty(hero.Id);
    }

    // ── Hero transfer: the player's wallet moves the asset; we verify ──

    public async Task<Hero> ConfirmTransferAsync(
        Player player, string heroId, string toPlayerId, CancellationToken ct)
    {
        var hero = GetOwnedHero(player, heroId);
        if (toPlayerId == player.Id)
            throw new GameRuleException("Hero already belongs to you.");
        if (!store.Players.ContainsKey(toPlayerId))
            throw new GameRuleException($"Unknown player '{toPlayerId}'.");

        // Non-custodial: the owner's wallet performs the asset spend itself.
        // We only verify the chain now shows the recipient holding the asset.
        var moved = await chain.VerifyHeroOwnershipAsync(toPlayerId, hero.AssetId ?? hero.Id, ct);
        if (!moved)
            throw new GameRuleException(
                "The chain does not show the recipient holding this hero yet — send the hero asset from your wallet first, then confirm.");

        // Item assets stay in the sender's wallet, so the loadout can't travel.
        foreach (var slot in hero.Equipment.Slots.Keys.ToList())
            hero.Equipment.Unequip(slot);

        hero.OwnerId = toPlayerId;
        // TRANSFER is an identity event: a crash inside the flush window must not rehydrate the hero back
        // to the sender — the chain already shows the recipient holding the asset. (Captures the stripped
        // loadout in the same write.)
        await persistence.SaveHeroAsync(hero, ct);
        return hero;
    }

    // ── Equipment: invoice → client pays → claim delivers the unit ─────

    public async Task<(ItemPurchase Purchase, FeeInvoice Invoice)> CreateItemInvoiceAsync(
        Player player, string itemId, CancellationToken ct)
    {
        RequireAcceptedTerms(player);
        var item = Core.Equipment.ItemCatalog.Find(itemId)
            ?? throw new GameRuleException($"Unknown item '{itemId}'.");

        var invoice = await chain.CreateFeeInvoiceAsync($"item:{itemId}", item.PriceSats, ct);
        var purchase = new ItemPurchase
        {
            InvoiceId = invoice.InvoiceId,
            PlayerId = player.Id,
            ItemId = item.Id,
        };
        store.ItemPurchases[invoice.InvoiceId] = purchase;
        // Durable BEFORE the player is handed an address to pay: if they pay and the server bounces before
        // they claim, the purchase must still be there. (No-op unless persistence is configured.)
        await persistence.SaveItemPurchaseAsync(purchase, ct);
        return (purchase, invoice);
    }

    public async Task<(string ItemAssetId, string ArkTxId, ulong UnitsHeld)> ClaimItemAsync(
        Player player, string invoiceId, CancellationToken ct)
    {
        if (!store.ItemPurchases.TryGetValue(invoiceId, out var purchase) || purchase.PlayerId != player.Id)
            throw new GameRuleException($"Unknown purchase '{invoiceId}'.");

        // Idempotent success: a claimed purchase re-reports its delivery.
        if (purchase.Status == "claimed")
        {
            var heldAlready = await chain.GetItemAssetBalanceAsync(player.Id, purchase.ItemId, ct);
            return (purchase.ItemAssetId!, purchase.DeliveryTxId!, heldAlready);
        }

        if (!await chain.IsInvoicePaidAsync(invoiceId, ct))
            throw new GameRuleException("The item invoice has not been paid yet — pay it from your wallet, then claim.");

        // pending → delivering, exactly one claimer at a time; a failed delivery
        // returns to pending so the paid purchase stays claimable.
        lock (purchase.Gate)
        {
            if (purchase.Status == "delivering")
                throw new GameRuleException("Delivery already in progress — retry in a moment.");
            if (purchase.Status == "claimed")
                throw new GameRuleException("Purchase already claimed.");
            purchase.Status = "delivering";
        }

        try
        {
            var item = Core.Equipment.ItemCatalog.Find(purchase.ItemId)!;
            var delivery = await chain.DeliverItemAssetAsync(player.Id, item.Id, item.Name, ct);
            purchase.ItemAssetId = delivery.ItemAssetId;
            purchase.DeliveryTxId = delivery.ArkTxId;
            purchase.Status = "claimed";
            await persistence.SaveItemPurchaseAsync(purchase, ct);   // delivered — record it so it can't be re-delivered
            await store.RecordInflowAsync(invoiceId, "item", item.PriceSats, ct);
            var held = await chain.GetItemAssetBalanceAsync(player.Id, item.Id, ct);
            return (delivery.ItemAssetId, delivery.ArkTxId, held);
        }
        catch
        {
            purchase.Status = "pending";
            throw;
        }
    }

    /// <summary>
    /// The catalog item ids the player currently holds at least one unit of — the shop marks these
    /// as owned. Ownership is on-chain (the same balance the equip check reads), so it survives
    /// across sessions with no server-side inventory bookkeeping.
    /// </summary>
    public async Task<List<string>> OwnedItemIdsAsync(Player player, CancellationToken ct)
    {
        var owned = new List<string>();
        foreach (var item in Core.Equipment.ItemCatalog.All)
            if (await chain.GetItemAssetBalanceAsync(player.Id, item.Id, ct) > 0)
                owned.Add(item.Id);
        return owned;
    }

    public async Task<Hero> EquipAsync(Player player, string heroId, string itemId, CancellationToken ct)
    {
        var hero = GetOwnedHero(player, heroId);
        var item = Core.Equipment.ItemCatalog.Find(itemId)
            ?? throw new GameRuleException($"Unknown item '{itemId}'.");

        // Tier gate: the top set is grown into, not bought into. Checked on EQUIP rather than on purchase, so
        // a player can still buy ahead (and trade), and so a loadout that predates the gate keeps working —
        // this rejects a new equip, it never strips a hero.
        if (hero.Level < item.MinLevel)
            throw new GameRuleException(
                $"{item.Name} needs a level-{item.MinLevel} hero — {hero.Name} is level {hero.Level}.");

        var unitsHeld = await chain.GetItemAssetBalanceAsync(player.Id, item.Id, ct);
        var unitsAllocated = store.Heroes.Values.Count(h =>
            h.OwnerId == player.Id &&
            h.Id != hero.Id &&
            h.Equipment.Slots.Values.Contains(item.Id));
        var alreadyOnTargetSlot = hero.Equipment.Slots.TryGetValue(item.Slot, out var current) && current == item.Id;
        if (!alreadyOnTargetSlot && (ulong)unitsAllocated >= unitsHeld)
            throw new GameRuleException(
                $"You hold {unitsHeld} unit(s) of {item.Name} and {unitsAllocated} are already equipped — buy another with 'buy {item.Id}'.");

        hero.Equipment.Equip(item);
        store.MarkHeroDirty(hero.Id);   // the loadout is progression — the flush persists it
        return hero;
    }

    public Hero Unequip(Player player, string heroId, string slotName)
    {
        var hero = GetOwnedHero(player, heroId);
        if (!Enum.TryParse<Core.Equipment.EquipmentSlot>(slotName, ignoreCase: true, out var slot))
            throw new GameRuleException($"Unknown slot '{slotName}' (Weapon/Armor/Trinket).");
        if (!hero.Equipment.Unequip(slot))
            throw new GameRuleException($"{hero.Name} has nothing equipped in {slot}.");
        store.MarkHeroDirty(hero.Id);   // the loadout is progression — the flush persists it
        return hero;
    }

    // ── Marketplace: resting item offers (covenant-enforced, buyer-funded) ──

    /// <summary>
    /// Lists one spare unit of an item for sale: builds the resting-offer
    /// covenant and returns the address the seller deposits the item into. The
    /// covenant pins the seller as payee and enforces the ask, so fulfilment is
    /// trustless — the server is only the discovery index.
    /// </summary>
    public async Task<(OfferListing Listing, OfferInfo Info)> CreateOfferAsync(
        Player player, string itemId, long askSats, CancellationToken ct)
    {
        var item = Core.Equipment.ItemCatalog.Find(itemId)
            ?? throw new GameRuleException($"Unknown item '{itemId}'.");
        if (askSats <= 0) throw new GameRuleException("The ask must be a positive number of sats.");

        // Reconcile this seller's existing listings first, so a just-deposited
        // offer is counted as active (item already gone from their wallet) rather
        // than pending (item still reserved in it).
        foreach (var existing in store.Offers.Values
                     .Where(o => o.SellerId == player.Id && o.ItemId == item.Id && o.Status != "closed").ToList())
            await ReconcileOfferAsync(existing, ct);

        // The seller must hold a FREE unit — not one already equipped, nor one reserved in an offer
        // still awaiting its deposit (that item is in their wallet, so it is counted in `held`). An
        // offer whose asset has landed no longer reserves anything — gate on the deposit itself rather
        // than on Status, so this stays correct however the status vocabulary evolves.
        var held = await chain.GetItemAssetBalanceAsync(player.Id, item.Id, ct);
        var equipped = (ulong)store.Heroes.Values.Count(h =>
            h.OwnerId == player.Id && h.Equipment.Slots.Values.Contains(item.Id));
        var reserved = (ulong)store.Offers.Values.Count(o =>
            o.SellerId == player.Id && o.ItemId == item.Id && o.Status == "pending" && !o.AssetDeposited);
        if (held <= equipped + reserved)
            throw new GameRuleException(
                $"You hold {held} unit(s) of {item.Name}; {equipped} equipped and {reserved} awaiting deposit — none free to sell.");

        var fee = MarketplaceFeeFor(askSats, item.Name);
        var offerId = NewId("offer");
        var info = await chain.CreateOfferAsync(offerId, player.Id, item.Id, askSats,
            DateTimeOffset.UtcNow.Add(_options.WagerEscrowRefundAfter).ToUnixTimeSeconds(), fee, ct);
        var listing = new OfferListing
        {
            Id = offerId, SellerId = player.Id, ItemId = item.Id, AskSats = askSats,
            OfferAddress = info.OfferAddress, ItemAssetId = info.ItemAssetId,
            OfferValueSats = info.OfferValueSats, RefundAfterUnixSeconds = info.RefundAfterUnixSeconds,
            ListingFeeSats = fee,
        };
        store.Offers[offerId] = listing;
        // Durable BEFORE the seller is handed an address to deposit into: the covenant's params already
        // survive a restart, but this row is the only thing that can NAME the offer afterwards, and the
        // deposit becomes possible the moment this response lands. (No-op unless persistence is configured.)
        await persistence.SaveOfferAsync(listing, ct);
        return (listing, info);
    }

    /// <summary>
    /// The marketplace fee this listing's covenant will enforce — taken from the SALE, not billed at
    /// listing: the buyer pays the ask, and the fulfil leaf routes <c>ask − fee</c> to the seller and the
    /// fee to the treasury. Nothing is owed if the item never sells, and a sale cannot skip the cut.
    /// The ask must clear the fee, or the seller's payout would be zero or negative and the covenant
    /// could not be built at all — refused here with a message the seller can act on.
    /// </summary>
    private long MarketplaceFeeFor(long askSats, string what)
    {
        var fee = _options.OfferListingFeeSats;
        if (fee <= 0) return 0;
        if (askSats <= fee)
            throw new GameRuleException(
                $"The ask for {what} must be more than the {fee}-sat marketplace fee (it is taken from the sale).");
        return fee;
    }

    /// <summary>Active (funded, buyable) offers, each reconciled against on-chain truth first.</summary>
    public async Task<IReadOnlyList<OfferListing>> ListOffersAsync(CancellationToken ct)
    {
        foreach (var offer in store.Offers.Values.Where(o => o.Status != "closed").ToList())
            await ReconcileOfferAsync(offer, ct);
        return store.Offers.Values.Where(o => o.Status == "active")
            .OrderBy(o => o.CreatedAt).ToList();
    }

    /// <summary>Recently sold (closed) offers — the marketplace's "just changed hands" strip.</summary>
    public async Task<IReadOnlyList<OfferListing>> ListSoldOffersAsync(int take, CancellationToken ct)
    {
        // Reconcile still-active offers first, so one that just sold surfaces here immediately.
        foreach (var offer in store.Offers.Values.Where(o => o.Status == "active").ToList())
            await ReconcileOfferAsync(offer, ct);
        return store.Offers.Values.Where(o => o.Status == "closed")
            .OrderByDescending(o => o.CreatedAt).Take(Math.Clamp(take, 1, 24)).ToList();
    }

    /// <summary>One offer's current listing, reconciled against on-chain truth.</summary>
    public async Task<OfferListing> GetOfferAsync(string offerId, CancellationToken ct)
    {
        if (!store.Offers.TryGetValue(offerId, out var offer))
            throw new GameRuleException($"Unknown offer '{offerId}'.");
        await ReconcileOfferAsync(offer, ct);
        return offer;
    }

    /// <summary>
    /// Drives the listing's status from on-chain truth: once the item is
    /// observed at the offer address it is <c>active</c>; when it later leaves
    /// (fulfilled by a buyer or reclaimed by the seller) it becomes <c>closed</c>.
    /// </summary>
    private async Task ReconcileOfferAsync(OfferListing offer, CancellationToken ct)
    {
        if (offer.Status == "closed") return;
        var statusBefore = offer.Status;
        // `pending` means exactly one thing again: the asset has not landed yet. The marketplace fee is
        // enforced by the covenant at fulfil, so there is no invoice to wait on and a listing goes live
        // the moment its asset is observed. AssetDeposited is kept as the explicit fact the free-to-sell
        // check reads, rather than re-deriving it from a status that once meant two things.
        offer.AssetDeposited = await chain.IsOfferFundedAsync(offer.Id, ct);
        if (offer.AssetDeposited)
            offer.Status = "active";
        else if (offer.Status == "active")
        {
            // The asset left a live listing — it sold, OR the seller reclaimed it. The two spends are
            // NOT interchangeable on-chain: only a fulfil pays the treasury the covenant's cut, in the
            // very transaction that spends the offer. Book on that proof and nothing else — a sale the
            // chain cannot confirm is left uncounted, because under-reporting is survivable and
            // over-stating the income of a treasury holding real bitcoin is not.
            // Keyed on the offer, so this and ClaimPurchasedHeroAsync can never book the same sale twice.
            offer.Status = "closed";
            if (offer.ListingFeeSats > 0)
            {
                if (await chain.WasOfferSoldAsync(offer.Id, ct))
                    await store.RecordInflowAsync(OfferSaleInflowId(offer.Id), "listing", offer.ListingFeeSats, ct);
                else
                    // The tripwire. This is the EXPECTED path for a reclaim, so it is not an error — but it
                    // is also exactly what a broken detector looks like, and the two are indistinguishable
                    // from here. Named at warning so a persistent fault leaves a greppable trail per offer;
                    // EconomyHealthDto.UnbookedClosedFeeOffers is the same fact as a countable trend.
                    logger?.LogWarning(
                        "Offer {OfferId} closed holding a {FeeSats}-sat listing fee, but no sale could be "
                        + "attributed on-chain, so nothing was booked. Either the seller reclaimed it (normal, "
                        + "and the common case) or the sale detector has stopped matching the treasury's fee "
                        + "output. A count of these climbing against flat booked listing income is the latter.",
                        offer.Id, offer.ListingFeeSats);
            }
        }

        // Latch the new status durably — but ONLY on a real transition. Reconcile runs over every live offer
        // on every market read, and an unchanged listing must not buy a database write per page load.
        //
        // This is chain-FIRST-then-latch: the chain was just read and is the source of truth, so the row only
        // records what was observed. That it comes AFTER the fee booking above is deliberate. A crash between
        // the two rehydrates the offer as still `active`, so the next reconcile closes it again and re-books
        // under the same once-only key (see OfferSaleInflowId) — which the treasury's inflow dedup absorbs, so
        // the fee is counted exactly once. Latching `closed` FIRST would instead filter the offer out at load
        // (a closed row is never rehydrated) and lose the fee permanently. Under-booking is survivable and
        // double-booking real-bitcoin income is not, so the safe half of that trade is the one taken here.
        if (offer.Status != statusBefore) await persistence.SaveOfferAsync(offer, ct);
    }

    /// <summary>The once-only key a listing fee is booked under. ReconcileOfferAsync and
    /// ClaimPurchasedHeroAsync both prove the same sale by different means and both book under this, so the
    /// store's inflow dedup makes it count once — and the unbooked-close tripwire asks about the same key,
    /// which is the only reason it can tell "nothing was booked for this offer" from "nothing was booked".</summary>
    private static string OfferSaleInflowId(string offerId) => $"offer-sale:{offerId}";

    /// <summary>
    /// This player's covenant escrows that may still hold their assets with no path forward: a listing
    /// whose asset is deposited but has not closed, breed/merge deposits that were never revealed, and
    /// the two abandonable STAKES — a wagered duel's sats and a death-match's hero (plus its gear).
    /// The timelocked reclaim leaf recovers each one from the player's OWN wallet, so this is DISCOVERY
    /// ONLY — someone who knows the id can always reclaim whether or not this endpoint lists it.
    /// Breed and merge are listed whenever the session is unfinished, NOT only when the escrow reads
    /// fully funded: a run that died between the two parent deposits leaves assets escrowed while
    /// IsBreedEscrowFundedAsync still reports false, and that is precisely the case worth surfacing.
    /// Reclaiming an escrow that turns out to be empty is harmless; hiding one that holds a hero is not.
    ///
    /// The two stakes follow that same ruling with the precision each escrow shape allows. A wager has
    /// PER-PARTY escrows, so GetWagerEscrowFundingAsync answers exactly the question that matters — is
    /// THIS player's stake on-chain — and the row appears only when it is. A death-match instead has ONE
    /// JOINT escrow with no per-side funding probe: IsDeathMatchEscrowFundedAsync is true only once BOTH
    /// heroes are in, so gating on it would hide the half-funded escrow, which is the very case a stranded
    /// hero sits in. That escrow IS recoverable half-funded — the reclaim{Side} leaf is per-side and
    /// structural (NumAssetGroupsIs + AssetInputSumIs over MY assets only, never the counterparty's), so
    /// one side reclaims without the other ever having staked. Death-matches therefore list like breed and
    /// merge, on the session being unfinished. The defender is the one exception: they are handed the joint
    /// address only BY accepting, so before accept they have staked nothing and a row would be pure noise.
    /// </summary>
    public async Task<IReadOnlyList<Shared.ReclaimableDto>> ListReclaimableAsync(Player player, CancellationToken ct)
    {
        var items = new List<Shared.ReclaimableDto>();

        foreach (var offer in store.Offers.Values
                     .Where(o => o.SellerId == player.Id && o.Status != "closed").ToList())
        {
            await ReconcileOfferAsync(offer, ct);
            // Only a DEPOSITED asset is at stake; an offer still awaiting its deposit holds nothing.
            if (offer.Status == "closed" || !offer.AssetDeposited) continue;
            var what = offer.Kind == "hero"
                ? store.Heroes.TryGetValue(offer.HeroId ?? "", out var hero) ? hero.Name : "A hero"
                : Core.Equipment.ItemCatalog.Find(offer.ItemId)?.Name ?? offer.ItemId;
            items.Add(new Shared.ReclaimableDto("offer", offer.Id,
                $"{what} is resting on the market at {offer.AskSats} sats.",
                offer.RefundAfterUnixSeconds));
        }

        foreach (var breed in store.Breedings.Values
                     .Where(b => b.PlayerId == player.Id && !b.Completed && b.Mode == "covenant").ToList())
            if (await chain.GetBreedEscrowParamsAsync(breed.Id, ct) is { } p)
                items.Add(new Shared.ReclaimableDto("breed", breed.Id,
                    $"An unfinished breeding — both parents and the {breed.FeeSats}-sat fee may be escrowed.",
                    p.RefundAfterUnixSeconds));

        foreach (var merge in store.Merges.Values
                     .Where(m => m.PlayerId == player.Id && !m.Completed && m.Mode == "covenant").ToList())
            if (await chain.GetMergeEscrowParamsAsync(merge.Id, ct) is { } p)
                items.Add(new Shared.ReclaimableDto("merge", merge.Id,
                    $"An unfinished fusion — the base and sacrifice heroes and the {merge.FeeSats}-sat fee may be escrowed.",
                    p.RefundAfterUnixSeconds));

        // Probe the chain for abandonment first, exactly as the offer branch reconciles first — a match
        // whose stake was already refunded must not keep offering a reclaim that can only fail.
        await ReconcileAbandonedMatchesAsync(ct);
        foreach (var match in store.Matches.Values
                     .Where(m => m.Mode == "covenant" && m.Status != "resolved"
                                 && (m.ChallengerPlayerId == player.Id || m.DefenderPlayerId == player.Id)).ToList())
        {
            // Only THIS player's side matters. The stake is sats at a per-party address, so a funded probe
            // of the other side says nothing about whether this player has anything to recover.
            var funding = await chain.GetWagerEscrowFundingAsync(match.Id, ct);
            if (funding is null) continue;
            var mine = match.ChallengerPlayerId == player.Id ? funding.ChallengerFunded : funding.DefenderFunded;
            if (!mine) continue;
            if (await chain.GetWagerEscrowParamsAsync(match.Id, ct) is { } p)
                items.Add(new Shared.ReclaimableDto("wager", match.Id,
                    $"An unfinished duel — your {match.WagerSats}-sat stake is escrowed.",
                    p.RefundAfterUnixSeconds));
        }

        foreach (var dm in store.DeathMatches.Values
                     .Where(d => !d.Completed
                                 && (d.ChallengerPlayerId == player.Id
                                     // Accepting IS staking, so before it the defender holds their hero still.
                                     || (d.DefenderPlayerId == player.Id && d.Accepted))).ToList())
            if (await chain.GetDeathMatchEscrowParamsAsync(dm.Id, ct) is { } p)
            {
                var heroId = dm.ChallengerPlayerId == player.Id ? dm.ChallengerHeroId : dm.DefenderHeroId;
                var name = store.Heroes.TryGetValue(heroId, out var hero) ? hero.Name : "Your hero";
                items.Add(new Shared.ReclaimableDto("deathmatch", dm.Id,
                    $"An unfinished death-match — {name} and any gear staked with them may be escrowed.",
                    p.RefundAfterUnixSeconds));
            }

        // Soonest-unlockable first, tie-broken on the unique id so the order is total (see #113).
        return items.OrderBy(i => i.ReclaimAfterUnixSeconds)
            .ThenBy(i => i.Id, StringComparer.Ordinal).ToList();
    }

    // ── Marketplace: hero sales (unique-asset offers) ──────────────────

    /// <summary>
    /// Lists one of the player's HEROES for sale: the hero is a unique asset, so
    /// this reuses the same offer covenant as items — the seller deposits the
    /// hero asset into the offer address, any buyer pays the ask to take it. The
    /// buyer then claims game-side ownership via <see cref="ClaimPurchasedHeroAsync"/>.
    /// </summary>
    public async Task<(OfferListing Listing, OfferInfo Info)> CreateHeroOfferAsync(
        Player player, string heroId, long askSats, CancellationToken ct)
    {
        RequireAcceptedTerms(player);
        var hero = GetOwnedHero(player, heroId); // verifies the seller owns it
        if (askSats <= 0) throw new GameRuleException("The ask must be a positive number of sats.");
        if (string.IsNullOrEmpty(hero.AssetId))
            throw new GameRuleException($"{hero.Name} has no on-chain asset to sell.");
        if (store.Offers.Values.Any(o => o.Kind == "hero" && o.HeroId == heroId && o.Status is "pending" or "active"))
            throw new GameRuleException($"{hero.Name} is already listed for sale.");

        var fee = MarketplaceFeeFor(askSats, hero.Name);
        var offerId = NewId("offer");
        var info = await chain.CreateHeroOfferAsync(offerId, player.Id, hero.AssetId!, askSats,
            DateTimeOffset.UtcNow.Add(_options.WagerEscrowRefundAfter).ToUnixTimeSeconds(), fee, ct);
        var listing = new OfferListing
        {
            Id = offerId, SellerId = player.Id, Kind = "hero", ItemId = "", HeroId = heroId,
            AskSats = askSats, OfferAddress = info.OfferAddress, ItemAssetId = info.ItemAssetId,
            OfferValueSats = info.OfferValueSats, RefundAfterUnixSeconds = info.RefundAfterUnixSeconds,
            ListingFeeSats = fee,
        };
        store.Offers[offerId] = listing;
        // Durable before the deposit address reaches the seller, as for item offers — and the stakes are
        // higher here: the hero itself is persisted, so a lost offer row leaves the game believing the seller
        // still owns a hero whose asset is escrowed in the covenant.
        await persistence.SaveOfferAsync(listing, ct);
        return (listing, info);
    }

    /// <summary>
    /// The buyer claims game-side ownership after fulfilling a hero offer from
    /// their own wallet: non-custodial, so the server only VERIFIES the chain now
    /// shows the buyer holding the hero asset, then reassigns the hero record and
    /// strips its equipment (loadouts stay in the seller's wallet, as on transfer).
    /// </summary>
    public async Task<Hero> ClaimPurchasedHeroAsync(Player buyer, string offerId, CancellationToken ct)
    {
        if (!store.Offers.TryGetValue(offerId, out var offer) || offer.Kind != "hero")
            throw new GameRuleException($"Unknown hero offer '{offerId}'.");
        if (offer.SellerId == buyer.Id)
            throw new GameRuleException("You can't buy your own hero.");
        var hero = GetHero(offer.HeroId!);

        var held = await chain.VerifyHeroOwnershipAsync(buyer.Id, hero.AssetId ?? hero.Id, ct);
        if (!held)
            throw new GameRuleException(
                "The chain does not show you holding this hero yet — fulfil the offer from your wallet first, then claim.");

        foreach (var slot in hero.Equipment.Slots.Keys.ToList())
            hero.Equipment.Unequip(slot);
        hero.OwnerId = buyer.Id;
        // The marketplace's transfer moment — same identity event as ConfirmTransferAsync, same rule:
        // the buyer's ownership goes durable now, not at the next flush.
        await persistence.SaveHeroAsync(hero, ct);
        offer.Status = "closed";
        // A CONFIRMED sale — the chain shows the buyer holding the hero, so the fulfil ran and its
        // covenant paid the treasury its cut. ReconcileOfferAsync now proves the same thing a second
        // way (the treasury leg of the spending transaction), and both book under this one key, so the
        // store's inflow dedup makes the sale once-only however often either path runs.
        if (offer.ListingFeeSats > 0)
            await store.RecordInflowAsync(OfferSaleInflowId(offer.Id), "listing", offer.ListingFeeSats, ct);
        // The close goes durable last, for the same reason it does in ReconcileOfferAsync: a crash before this
        // point leaves the offer rehydrating as `active`, and reconcile then closes it and re-books under the
        // same once-only key. The hero's new ownership is already durable above, so the only thing still at
        // stake here is whether the sale can be found again — never who owns the hero.
        await persistence.SaveOfferAsync(offer, ct);
        return hero;
    }
}
