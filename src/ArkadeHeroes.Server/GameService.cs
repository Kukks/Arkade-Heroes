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
    Persistence.IGameStatePersistence persistence, Persistence.IAuditLog audit,
    ILogger<GameService>? logger = null)
{
    private readonly GameOptions _options = options.Value;

    /// <summary>
    /// Appends one entry to the append-only audit log. NEVER throws (see <see cref="Persistence.IAuditLog"/>
    /// for why that is the safe direction on a money path), so every call site below can sit inside a flow
    /// that has already moved sats without the log being able to unwind it.
    ///
    /// <paramref name="dedupKey"/> is supplied wherever the action has a natural once-only key, so a client
    /// that poll-retries — which they all do — logs the action once. Where an action can genuinely recur
    /// (a mint, a listing, an equip) it is left null, because there the repetition IS the fact.
    /// </summary>
    private Task AuditAsync(string eventType, string? actorPlayerId, string?[] subjectIds, object payload,
        string? dedupKey = null)
        => audit.RecordAsync(new Persistence.AuditEntry(eventType, actorPlayerId, subjectIds, payload, dedupKey));

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
        // The first entry in this player's history.
        //
        // DELIBERATELY MINIMAL. The bearer token is never logged — it is a session credential, and the same
        // reason it is kept out of the durable player row keeps it out of here. The name, the wallet
        // address and the login pubkey are left out for a different and stronger reason: they already live
        // on the player row, which is mutable and erasable, and copying them into a table the database
        // itself refuses to update or delete would make this log a permanent second home for personal data
        // that cannot be corrected or removed. The actor id resolves to all three for anyone with a reason
        // to look. Whether the pseudonymous id itself is enough is a retention question for counsel, not
        // one to answer by writing more of it down.
        await AuditAsync(Persistence.AuditEventType.PlayerRegistered, player.Id, [player.Id],
            new { acceptedTermsVersion, hasLoginKey = loginKey is not null },
            $"player-registered:{player.Id}");
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
        // The player row holds only the LATEST acceptance (it moves forward on each bump). The log keeps
        // every one of them, which is what turns "they accepted v3" into "they accepted v1 on this date,
        // then v3 on that one" — the shape the evidence has to take if it is ever actually needed.
        await AuditAsync(Persistence.AuditEventType.PlayerAcceptedTerms, player.Id, [player.Id],
            new { version, acceptedAtUtc = player.TermsAcceptedAtUtc },
            $"terms-accepted:{player.Id}:{version}");
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

    // ── Provenance: everything that ever happened to one hero ──────────

    /// <summary>The receipt types that MINT the hero they name in <c>ResultHeroId</c> — this hero's birth,
    /// when it names this hero. Deliberately a closed set rather than "anything with a ResultHeroId":
    /// <c>gauntlet</c> puts the runner there on a full clear, <c>trials</c> always does, and
    /// <c>deathmatch</c> puts the WINNER there. Treating any of those as a birth would have a hero born
    /// again every time it cleared a dungeon.</summary>
    private static readonly string[] BirthReceiptTypes = ["breeding", "merge", "absorb"];

    /// <summary>
    /// A hero's full provenance, newest first: how it came to exist, every fight it has been in, what it
    /// was traded for, and what it consumed or was consumed by.
    ///
    /// Almost all of it is DERIVED rather than separately recorded — the progression receipt ledger
    /// already files every breed, fusion, absorb, duel, spar, death-match, gauntlet and trials run under
    /// each hero it names, and the hero's own lineage columns carry its parents. The one thing no
    /// derivation could recover is what a hero SOLD for, which is why that alone gets a durable row.
    ///
    /// The ledger is in memory, so for a hero restored from disk this is a partial history — see
    /// <see cref="Shared.HeroTimelineDto.Complete"/>, which says so rather than letting a life that
    /// begins at boot read as a whole one.
    /// </summary>
    public Shared.HeroTimelineDto HeroTimeline(string heroId)
    {
        // A DESTROYED hero still has a history, and it is the history that matters most: this is the page a
        // player lands on after a death-match. Its row is erased, so the headstone stands in for it — the
        // one thing that survives the burn. Only an id nothing has ever heard of is a 404.
        var hero = store.Heroes.GetValueOrDefault(heroId);
        var grave = hero is null ? store.HeroTombstones.GetValueOrDefault(heroId) : null;
        if (hero is null && grave is null) throw new GameRuleException($"Unknown hero '{heroId}'.");
        var events = new List<Shared.HeroTimelineEventDto>();

        // Copy under the SAME lock IssueReceipt appends beneath: the value is a plain List, so enumerating
        // it while a fight files a receipt is a torn read.
        Shared.ProgressionReceiptDto[] receipts = [];
        if (store.ReceiptsByHero.TryGetValue(heroId, out var filed))
            lock (filed) { receipts = filed.ToArray(); }

        var birth = receipts.FirstOrDefault(r =>
            r.ResultHeroId == heroId && BirthReceiptTypes.Contains(r.Type));

        foreach (var r in receipts)
        {
            if (ReferenceEquals(r, birth)) continue;   // rendered separately below, as the origin
            if (BuildFightEvent(r, heroId) is { } e) events.Add(e);
        }

        // Read the identity facts from whichever record exists, EXPLICITLY. `hero?.ParentAId ?? grave!.…`
        // would look equivalent and throw on every gen-0 starter: its ParentAId is legitimately null, so
        // the `??` falls through to the headstone that a living hero does not have.
        var generation = hero is not null ? hero.Generation : grave!.Generation;
        var parentAId = hero is not null ? hero.ParentAId : grave!.ParentAId;
        var parentBId = hero is not null ? hero.ParentBId : grave!.ParentBId;
        events.Add(BirthEvent(generation, parentAId, parentBId, birth));

        // The DEATH, from the headstone rather than from a receipt. The receipt ledger already renders a
        // "burned" line for a fusion or an absorb, but it is in memory and a classic death-match loser never
        // gets one on its OWN page at all (the receipt names the winner as the result). This is the durable
        // fact, and it is the one thing a destroyed hero's page must not be able to omit.
        if (grave is not null)
            events.Add(new Shared.HeroTimelineEventDto("destroyed", grave.DestroyedAtUnixSeconds,
                DestructionSummary(grave), DestructionRelated(grave),
                WatchMatchId: grave.Reason.StartsWith("deathmatch", StringComparison.Ordinal) ? grave.SessionId : null,
                Outcome: "lost",
                Detail: $"level {grave.Level} at the time · this hero no longer exists"));

        foreach (var sale in store.HeroSales.Values.Where(s => s.HeroId == heroId))
        {
            var buyer = sale.BuyerId is { } b ? store.Players.GetValueOrDefault(b)?.Name ?? Short(b) : null;
            var seller = store.Players.GetValueOrDefault(sale.SellerId)?.Name ?? Short(sale.SellerId);
            events.Add(new Shared.HeroTimelineEventDto(
                "sold", sale.SoldAtUnixSeconds,
                $"Sold on the marketplace for {sale.AskSats:N0} sats.",
                [],
                Sats: sale.AskSats,
                // The buyer is genuinely unknown on a sale proven only by the covenant's treasury leg —
                // say that, rather than leaving the line looking like it simply forgot to mention them.
                Detail: buyer is null
                    ? $"sold by {seller} · buyer not recorded"
                    : $"{seller} → {buyer}"));
        }

        // Newest first, with the origin last. A birth whose moment was never recorded carries 0 and lands
        // there anyway, which is where it belongs.
        var ordered = events
            .OrderByDescending(e => e.UnixSeconds)
            .ThenByDescending(e => e.Kind, StringComparer.Ordinal)
            .ToList();

        var rehydrated = store.RehydratedHeroes.ContainsKey(heroId);
        return new Shared.HeroTimelineDto(heroId, ordered, !rehydrated,
            rehydrated
                ? "This hero predates the arena's last restart. Fights, breeds and fusions are derived from "
                  + "the in-memory receipt ledger, which the restart cleared — sales and lineage survived, "
                  + "the rest of its earlier life did not."
                : null);
    }

    /// <summary>How this hero came to exist. The birth RECEIPT is the good answer — it carries the moment
    /// and both inputs. Without one (a gen-0 starter, or a hero whose receipt a restart dropped) this falls
    /// back to the durable lineage columns, which know the parents but NOT when: nothing in the system
    /// stamps a hero with its birth time, so the event carries 0 and the page says the moment is unknown
    /// instead of printing the epoch.</summary>
    private Shared.HeroTimelineEventDto BirthEvent(
        int generation, string? parentAId, string? parentBId, Shared.ProgressionReceiptDto? birth)
    {
        var at = birth?.UnixSeconds ?? 0;
        if (birth is { Type: "merge" })
            return new Shared.HeroTimelineEventDto("fused", at,
                $"Forged in a fusion from {Display(birth.HeroAId)} and {Display(birth.HeroBId)} — both were burned for it.",
                [Ref(birth.HeroAId), Ref(birth.HeroBId)],
                Detail: $"inherited its base's level {birth.LevelA}");

        if (birth is { Type: "absorb" })
            return new Shared.HeroTimelineEventDto("absorbed", at,
                $"Rose from a death-match absorb between {Display(birth.HeroAId)} and {Display(birth.HeroBId)} — both were burned for it.",
                [Ref(birth.HeroAId), Ref(birth.HeroBId)],
                WatchMatchId: birth.Id,
                Detail: $"kept the winner's level {birth.LevelA}");

        if (birth is { Type: "breeding" })
            return new Shared.HeroTimelineEventDto("bred", at,
                $"Bred from {Display(birth.HeroAId)} and {Display(birth.HeroBId)}.",
                [Ref(birth.HeroAId), Ref(birth.HeroBId)],
                Detail: $"generation {generation}");

        // No birth receipt. The lineage columns are durable, so they still answer WHAT — just not when.
        if (parentAId is { } pa && parentBId is { } pb)
            return new Shared.HeroTimelineEventDto("bred", 0,
                $"Descended from {Display(pa)} and {Display(pb)}.",
                [Ref(pa), Ref(pb)],
                Detail: $"generation {generation} · the moment was not recorded");

        return new Shared.HeroTimelineEventDto("born", 0,
            "Recruited into the arena as a gen-0 founder.",
            [],
            Detail: "no parents — the moment was not recorded");
    }

    /// <summary>The player-facing line for a destruction, in the vocabulary the reason codes carry.</summary>
    private string DestructionSummary(HeroTombstone grave) => grave.Reason switch
    {
        "merge-input" => grave.ReplacedByHeroId is { } into
            ? $"Burned as a fusion input — {Display(into)} was forged from it."
            : "Burned as a fusion input.",
        "deathmatch-absorb-winner" => grave.ReplacedByHeroId is { } into
            ? $"Won its death-match and was consumed by the absorb — {Display(into)} rose from it."
            : "Won its death-match and was consumed by the absorb.",
        "deathmatch-absorb-loser" => grave.ReplacedByHeroId is { } into
            ? $"Lost a death-match and was burned into the absorb — {Display(into)} rose from it."
            : "Lost a death-match and was burned into the absorb.",
        "deathmatch-loser" => "Lost a death-match and was permanently destroyed.",
        _ => "Permanently destroyed.",
    };

    private Shared.TimelineHeroRefDto[] DestructionRelated(HeroTombstone grave) =>
        grave.ReplacedByHeroId is { } into ? [Ref(into)] : [];

    /// <summary>One non-birth receipt as a timeline line, or null when it names this hero only in a role
    /// the hero's own page has nothing to say about.</summary>
    private Shared.HeroTimelineEventDto? BuildFightEvent(Shared.ProgressionReceiptDto r, string heroId)
    {
        var isA = r.HeroAId == heroId;
        var isB = r.HeroBId == heroId;
        // THE attribution gate. A receipt that does not NAME this hero is not this hero's history,
        // whatever else it says. The caller reads a per-hero index and so only ever passes receipts
        // already filed under this hero — but that is the CALLER's invariant, not this method's, and
        // relying on it silently is how another hero's death-match ends up on this page the first time
        // anything hands this a wider set. Stated once, here, so every arm below inherits it.
        if (!isA && !isB) return null;
        var swing = isA ? r.XpAwardA : r.XpAwardB;
        // The opponent is whichever side this hero is NOT — except in a self-duel, where both are it.
        var otherId = isA ? r.HeroBId : r.HeroAId;
        var other = string.IsNullOrEmpty(otherId) || otherId == heroId ? null : otherId;
        Shared.TimelineHeroRefDto[] related = other is null ? [] : [Ref(other)];
        var vs = other is null ? "itself" : Display(other);

        switch (r.Type)
        {
            case "merge":
                return new Shared.HeroTimelineEventDto("burned", r.UnixSeconds,
                    $"Burned in a fusion — {Display(r.ResultHeroId ?? "")} was forged from it and {vs}.", related);

            case "absorb":
                return new Shared.HeroTimelineEventDto("burned", r.UnixSeconds,
                    $"Burned in a death-match absorb against {vs} — {Display(r.ResultHeroId ?? "")} rose from it.",
                    related, WatchMatchId: r.Id);

            case "breeding":
            {
                Shared.TimelineHeroRefDto[] parties = other is null
                    ? [Ref(r.ResultHeroId ?? "")]
                    : [Ref(other), Ref(r.ResultHeroId ?? "")];
                return new Shared.HeroTimelineEventDto("bred-with", r.UnixSeconds,
                    $"Bred with {vs} — {Display(r.ResultHeroId ?? "")} was born.",
                    parties,
                    Detail: $"level {(isA ? r.LevelA : r.LevelB)} at the time");
            }

            case "deathmatch":
            {
                var won = r.ResultHeroId == heroId;
                return new Shared.HeroTimelineEventDto("deathmatch", r.UnixSeconds,
                    won
                        ? $"Survived a death-match against {vs}, which was burned."
                        : $"Died in a death-match against {vs}.",
                    related, WatchMatchId: r.Id, Outcome: won ? "won" : "lost");
            }

            case "gauntlet":
                return new Shared.HeroTimelineEventDto("gauntlet", r.UnixSeconds,
                    r.ResultHeroId == heroId ? "Cleared the gauntlet." : "Ran the gauntlet.",
                    [], Detail: XpDetail(swing));

            case "trials":
                // The run's score rides in XpAwardB (the receipt attests it); no XP ever changes hands.
                return new Shared.HeroTimelineEventDto("trials", r.UnixSeconds,
                    $"Reached wave {r.XpAwardB} of the endless Trials.", []);

            case "match" or "friendly":
            {
                var won = r.ResultHeroId == heroId;
                var staked = r.Type == "match";
                // A squad duel's receipt id is "{squadId}:{slot}" — a slot inside a 3v3 relay, which
                // /watch cannot serve (it replays whole duels and death-matches). Naming it there would
                // be a link to a "no replay" card, so it gets none.
                var squad = r.Id.Contains(':');
                var kind = squad ? "squad" : staked ? "duel" : "spar";
                var verb = other is null
                    ? "Fought itself"
                    : won ? $"Beat {vs}" : "Lost to " + vs;
                var arena = squad ? " in a squad relay" : staked ? " in a wagered duel" : " in a friendly spar";
                return new Shared.HeroTimelineEventDto(kind, r.UnixSeconds,
                    verb + arena + ".", related,
                    WatchMatchId: squad ? null : r.Id,
                    Outcome: other is null ? null : won ? "won" : "lost",
                    Detail: XpDetail(swing));
            }

            default:
                return null;
        }
    }

    private static string? XpDetail(long swing) =>
        swing == 0 ? null : swing > 0 ? $"+{swing:N0} xp" : $"{swing:N0} xp";

    /// <summary>
    /// A hero reference for the wire: its id always, its name whenever anything can still stand behind one,
    /// and whether it is DESTROYED.
    ///
    /// <para>Name and Destroyed are independent on purpose. A live hero is named and not destroyed; a
    /// destroyed hero with a headstone is named AND destroyed; a destroyed hero from before headstones
    /// existed is destroyed with no name at all. Encoding "gone" as a null name — as this used to — made
    /// the third case indistinguishable from the second and left the page unable to say who died.</para>
    /// </summary>
    private Shared.TimelineHeroRefDto Ref(string heroId) =>
        store.Heroes.TryGetValue(heroId, out var h) ? new(heroId, h.Name)
        : store.HeroTombstones.TryGetValue(heroId, out var stone) ? new(heroId, stone.Name, true)
        : new(heroId, null, true);

    /// <summary>How a hero reads inside a summary line: its name while it exists, its name and its fate once
    /// it doesn't, and a shortened id when even that is gone. Still never a placeholder — a name here comes
    /// off a headstone written at the burn site, or it is not printed.</summary>
    private string Display(string heroId) =>
        string.IsNullOrEmpty(heroId) ? "an unrecorded hero"
        : store.Heroes.TryGetValue(heroId, out var h) ? h.Name
        : store.HeroTombstones.TryGetValue(heroId, out var stone) ? $"{stone.Name} (destroyed)"
        : $"a destroyed hero ({Short(heroId)})";

    private static string Short(string id) => id.Length <= 12 ? id : $"{id[..6]}…{id[^4..]}";

    /// <summary>
    /// Lays a headstone for a hero about to be erased — the ONE call every burn site makes before removing
    /// the hero, and the only reason a destroyed hero can be named afterwards at all.
    ///
    /// <para>Best-effort on the durable write, like the audit log and for the same reason: this sits inside
    /// merge and death-match settles whose chain work is already done, and a throw here would abort a flow
    /// that has burned assets on-chain. The in-memory row lands regardless, so the running process can
    /// always name the hero; only a RESTART would lose it, and losing it costs a name on a page, never
    /// money. Named at warning so a persistent fault leaves a greppable trail.</para>
    /// </summary>
    private async Task RecordTombstoneAsync(
        Hero hero, string reason, string sessionId, string? replacedByHeroId, CancellationToken ct)
    {
        var stone = new HeroTombstone(
            hero.Id, hero.Name, hero.OwnerId, hero.Generation, hero.Level, hero.Genome.ToHex(),
            reason, sessionId, replacedByHeroId, DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            hero.ParentAId, hero.ParentBId);
        // TryAdd semantics: a hero dies once, so a retried settle re-running this tail keeps the FIRST
        // headstone, which is the one written while the hero was still whole.
        if (store.RecordTombstone(stone) is not { } fresh) return;
        try { await persistence.SaveHeroTombstoneAsync(fresh, ct); }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Hero {HeroId} ({HeroName}) was destroyed ({Reason}) but its headstone "
                + "could not be persisted — after a restart nothing will be able to name it.",
                hero.Id, hero.Name, reason);
        }
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
            // Deliberately logged, and deliberately marked as re-using an already-paid fee: this is the
            // branch where a player retargets a name they have ALREADY paid for, so a log that showed only
            // the first request would make the second name look like it arrived from nowhere.
            await AuditAsync(Persistence.AuditEventType.HeroRenameRequested, player.Id, [heroId],
                new { requestedName = normalized, feeSats = _options.HeroRenameFeeSats, feeInvoiceId = priorInvoice, reusedPaidFee = true });
            return null;   // already paid for — nothing further to settle before confirming
        }

        var fee = _options.HeroRenameFeeSats > 0
            ? await chain.CreateFeeInvoiceAsync($"rename:{heroId}", _options.HeroRenameFeeSats, ct)
            : null;
        store.Renames[heroId] = new RenameSession { HeroId = heroId, NewName = normalized, FeeInvoiceId = fee?.InvoiceId };
        await AuditAsync(Persistence.AuditEventType.HeroRenameRequested, player.Id, [heroId],
            new { requestedName = normalized, feeSats = fee is null ? 0 : _options.HeroRenameFeeSats, feeInvoiceId = fee?.InvoiceId, reusedPaidFee = false });
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

        var previousName = hero.Name;
        hero.Name = pending.NewName;
        store.Renames.TryRemove(heroId, out _);
        // RENAME is an identity event: the name was bought (a real-sats fee) and is globally unique —
        // losing it to a crash would both refund nothing and free the name for someone else to claim.
        await persistence.SaveHeroAsync(hero, ct);
        // No dedup key: a hero can legitimately be renamed again and again, and each is its own fact. A
        // RETRY cannot double-log anyway — the session is removed above, so a second confirm is refused
        // before it reaches here. The old name is recorded because the durable hero row keeps only the new
        // one, and "what was this hero called when it won that match" is otherwise unanswerable.
        await AuditAsync(Persistence.AuditEventType.HeroRenamed, player.Id, [heroId],
            new { previousName, newName = hero.Name, feeSats = pending.FeeInvoiceId is null ? 0 : _options.HeroRenameFeeSats, feeInvoiceId = pending.FeeInvoiceId });
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
            // A stud proposal is "still in play" until it is bred or refused — an accepted one may be
            // holding the proposer's paid fees, which is exactly what an operator wants counted.
            new("stud", store.StudProposals.Values.Count(s => !s.Completed && !s.Declined), store.StudProposals.Count),
            new("merge", store.Merges.Values.Count(m => !m.Completed), store.Merges.Count),
            // Same ruling as stud: a bid is in play until it settles or unwinds, and an ACCEPTED one may be
            // holding the bidder's paid sats waiting on a hero that was never delivered.
            new("bid", store.HeroBids.Values.Count(b => b.IsLive), store.HeroBids.Count),
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
        await AuditAsync(Persistence.AuditEventType.TournamentOpened, player.Id, [session.Id, heroId],
            new { buyInSats, size, heroId, buyInInvoiceId = buyIn.InvoiceId, commitmentHex = session.CommitmentHex },
            $"tournament-opened:{session.Id}");
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
            // One entrant per player is already enforced above, so the player+bracket pair IS the once-only
            // key — a retried join is refused before it gets here, and the key holds the line if it ever isn't.
            await AuditAsync(Persistence.AuditEventType.TournamentJoined, player.Id, [session.Id, heroId],
                new
                {
                    buyInSats = session.BuyInSats, heroId, buyInInvoiceId = buyIn.InvoiceId,
                    entrants = session.Entrants.Count, size = session.Size, status = session.Status,
                },
                $"tournament-joined:{session.Id}:{player.Id}");
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
            // Logged AFTER the podium loop so the entry reflects what was actually attempted, and keyed on
            // the bracket so the "already resolved" guard above and this key say the same thing. The prize
            // MOVEMENTS are logged separately by the treasury choke point (one treasury.outflow per prize
            // that really left) — this entry is the outcome, not a second copy of the payments.
            await AuditAsync(Persistence.AuditEventType.TournamentResolved, player.Id,
                [session.Id, .. session.Entrants.Select(e => e.HeroId)],
                new
                {
                    championHeroId = result.ChampionId, potSats = pot, rakePct = _config.TournamentRakePct,
                    prizePoolSats = prizePool, prizes, podium, entrants = session.Entrants.Count,
                    nonce, serverSeedHex = Convert.ToHexString(session.ServerSeed).ToLowerInvariant(),
                    entropyHex = session.EntropyHex, configVersion = session.ConfigVersion,
                    contentVersion = session.ContentVersion,
                },
                $"tournament-resolved:{session.Id}");
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
            // Actor is NULL: this is reachable from the operator console with no player behind it, and the
            // player-facing route is a bystander triggering a refund of OTHER people's buy-ins — neither
            // is an actor in the sense the log means. The bracket and its entrants are the subjects, which
            // is what a "why did my buy-in come back" question is actually asked against.
            await AuditAsync(Persistence.AuditEventType.TournamentRefunded, null,
                [session.Id, .. session.Entrants.Select(e => e.HeroId)],
                new
                {
                    entrantsRefunded = refunded, refundedSats, buyInSats = session.BuyInSats,
                    entrants = session.Entrants.Count, size = session.Size,
                },
                $"tournament-refunded:{session.Id}");
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
        // Keyed on the INVOICE, not the player: recruits are buyable as often as the player pays for them,
        // so a player has many of these — but each outstanding invoice is billed exactly once, and the
        // re-request path above returns without reaching here precisely so it cannot bill twice.
        await AuditAsync(Persistence.AuditEventType.StarterRequested, player.Id, [player.Id],
            new { feeSats = StarterClaimFeeSats, feeInvoiceId = invoice.InvoiceId, heroes = StarterHeroCount },
            $"starter-requested:{invoice.InvoiceId}");
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
        // Keyed on the invoice this claim SPENT, so a claim retried against the same paid invoice logs once
        // — the same thing the invoice-clearing above makes true of the sats. A free claim (no fee
        // configured) has no such key and is left un-deduped rather than given an invented one.
        await AuditAsync(Persistence.AuditEventType.StarterClaimed, player.Id,
            [player.Id, .. minted.Select(h => h.Id)],
            new { feeSats = StarterClaimFeeSats, feeInvoiceId = invoiceId, heroIds = minted.Select(h => h.Id).ToList() },
            invoiceId is null ? null : $"starter-claimed:{invoiceId}");
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
        FancyFind? fancyFind = null;
        if (FancySets.TitleFor(hero.Genome, _config) is { } fancy
            && store.RecordFancyFind(fancy, hero.Id, hero.Name, player.Id, DateTimeOffset.UtcNow.ToUnixTimeSeconds()) is { } find)
        {
            fancyFind = find;
            await persistence.SaveFancyFindAsync(find, ct);
        }
        // MINT is an identity event: durable NOW, not at the next flush — the chain can't enumerate a
        // player's heroes back, so an unsaved hero is unrecoverable if the process dies. Saved AFTER the
        // on-chain mint (this isn't a payout latch; the asset exists whether or not the row lands, and a
        // faulted save re-throws into a retryable flow). (No-op unless persistence is configured.)
        await persistence.SaveHeroAsync(hero, ct);
        // THE ONE MINT CHOKE POINT. Every hero that has ever existed passes through here — starters,
        // recruits, bred, fused, absorbed, the dev lever — so one call covers every way a hero can come
        // into being, and a future mint path cannot be added that skips it without also skipping the store.
        // Keyed on the hero id, which IS the on-chain asset id and is therefore unique for all time.
        // The Fancy find is folded into this payload rather than given its own event: it is a FACT OF THIS
        // MINT (the set and edition are stamped in the same instant), and it already has its own durable
        // row — the log complements that record rather than duplicating it.
        await AuditAsync(Persistence.AuditEventType.HeroMinted, player.Id, [hero.Id, parentA, parentB],
            new
            {
                heroId = hero.Id, name = hero.Name, generation, genomeHex = genome.ToHex(),
                parentAId = parentA, parentBId = parentB, assetId = mint.AssetId, mintArkTxId = mint.ArkTxId,
                serverSeedHex, playerNonce, entropyHex,
                fancyTitle = fancyFind?.Title, fancyEdition = fancyFind?.Edition,
            },
            $"hero-minted:{hero.Id}");
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
            await AuditAsync(Persistence.AuditEventType.BreedCommitted, player.Id,
                [covenantSession.Id, parentAId, parentBId],
                new
                {
                    mode = "covenant", feeSats = breedFee, escrowAddress = covenantSession.EscrowAddress,
                    parentAId, parentBId, commitmentHex = covenantSession.CommitmentHex,
                },
                $"breed-committed:{covenantSession.Id}");
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
        await AuditAsync(Persistence.AuditEventType.BreedCommitted, player.Id,
            [session.Id, parentAId, parentBId],
            new
            {
                mode = "invoice", feeSats = breedFee, feeInvoiceId = invoice.InvoiceId,
                parentAId, parentBId, commitmentHex = session.CommitmentHex,
            },
            $"breed-committed:{session.Id}");
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

        // The CHILD's own existence is already logged by the mint choke point. This entry is the BREED —
        // which session, whose fee, which parents and what it cost them in cooldown — keyed on the session
        // so it survives the poll-retry the client does around a reveal.
        await AuditAsync(Persistence.AuditEventType.BreedRevealed, player.Id,
            [session.Id, session.ParentAId, session.ParentBId, child.Id],
            new
            {
                mode = session.Mode, feeSats = session.FeeSats, feeInvoiceId = session.FeeInvoiceId,
                parentAId = session.ParentAId, parentBId = session.ParentBId, childHeroId = child.Id,
                childGeneration = child.Generation, parentABreedCount = parentA.BreedCount,
                parentBBreedCount = parentB.BreedCount, nonce, serverSeedHex, entropyHex,
                receiptId = receipt.Id,
            },
            $"breed-revealed:{session.Id}");

        return (child, serverSeedHex, entropyHex, receipt);
    }

    // ── Stud service: propose → the stud's owner CONSENTS → reveal ─────
    //
    // Ordinary breeding requires the caller to own BOTH parents, so consent never had to be modelled. A
    // stud service is the cross-owner case, and it is shaped exactly like the death-match's open → accept
    // → settle for the same reason: the second hero belongs to someone who has to say yes. The difference
    // is where the value moves — a death-match burns a hero, a stud pays its owner in sats.

    /// <summary>
    /// Proposes breeding one of the caller's heroes with ANOTHER player's, optionally offering that owner a
    /// stud fee. Nothing is billed and nothing is minted here: this is an offer, and its counterparty has
    /// not agreed to anything yet. The seed is committed NOW so the stud's owner is consenting to a breed
    /// whose randomness is already sealed, rather than to one the proposer could still re-roll.
    /// </summary>
    public async Task<StudProposal> ProposeStudAsync(
        Player player, string myHeroId, string studHeroId, long studFeeSats, CancellationToken ct)
    {
        RequireAcceptedTerms(player);
        if (studFeeSats < 0) throw new GameRuleException("A stud fee cannot be negative.");
        var mine = GetOwnedHero(player, myHeroId);
        var stud = GetHero(studHeroId);
        if (mine.Id == stud.Id)
            throw new GameRuleException("A hero cannot stud with itself.");
        // The whole point of the flow: the stud belongs to someone else. Owning both is ordinary breeding,
        // which needs no proposal and no fee routed back to yourself.
        if (stud.OwnerId == player.Id)
            throw new GameRuleException("You own both heroes — breed them directly instead of proposing a stud.");

        if (Sterility.IsSterile(mine.Genome, _config))
            throw new GameRuleException($"{mine.Name} is sterile — it cannot breed.");
        if (Sterility.IsSterile(stud.Genome, _config))
            throw new GameRuleException($"{stud.Name} is sterile — it cannot breed.");
        if (BreedingService.Validate(mine, stud, DateTimeOffset.UtcNow) is { } error)
            throw new GameRuleException(error);

        var seed = CommitReveal.NewSeed();
        var proposal = new StudProposal
        {
            Id = NewId("stud"),
            ProposerPlayerId = player.Id,
            StudOwnerPlayerId = stud.OwnerId,
            ProposerHeroId = myHeroId,
            StudHeroId = studHeroId,
            ServerSeed = seed,
            CommitmentHex = CommitReveal.Commit(seed),
            StudFeeSats = studFeeSats,
        };
        store.StudProposals[proposal.Id] = proposal;
        await persistence.SaveStudProposalAsync(proposal, ct);
        // Nothing is billed here and nothing can mint — but the OFFER is what the stud's owner later
        // consents to, so it is the thing a dispute over "what did I agree to" is settled against.
        await AuditAsync(Persistence.AuditEventType.StudProposed, player.Id,
            [proposal.Id, myHeroId, studHeroId, proposal.StudOwnerPlayerId],
            new
            {
                proposerHeroId = myHeroId, studHeroId, studOwnerPlayerId = proposal.StudOwnerPlayerId,
                studFeeSats, commitmentHex = proposal.CommitmentHex,
            },
            $"stud-proposed:{proposal.Id}");
        return proposal;
    }

    /// <summary>
    /// The stud owner's CONSENT — the gate the whole flow hangs on, and the only place the invoices are
    /// created. Before this returns, the proposer has been billed nothing and the stud's owner is owed
    /// nothing; after it, the proposer may pay and reveal exactly once.
    /// </summary>
    public async Task<(StudProposal Proposal, FeeInvoice BreedFeeInvoice, FeeInvoice? StudFeeInvoice)> AcceptStudAsync(
        Player player, string proposalId, CancellationToken ct)
    {
        RequireAcceptedTerms(player);
        if (!store.StudProposals.TryGetValue(proposalId, out var proposal))
            throw new GameRuleException($"Unknown stud proposal '{proposalId}'.");
        // Per-proposal gate: the accepted-check → invoice → accepted-set must be one atomic step, or two
        // concurrent accepts of one consent bill the proposer twice. The SAME key the reveal takes, so an
        // accept can never interleave with the reveal it authorises.
        using var gate = await store.LockAsync($"stud:{proposalId}", ct);
        if (proposal.StudOwnerPlayerId != player.Id)
            throw new GameRuleException("Only the stud's owner can accept this proposal.");
        if (proposal.Accepted) throw new GameRuleException("Stud proposal already accepted.");
        if (proposal.Declined) throw new GameRuleException("Stud proposal already declined.");
        if (proposal.Completed) throw new GameRuleException("Stud proposal already bred.");
        // Consent has to come from whoever owns the hero NOW — a stud sold since the proposal was made
        // took its owner's say-so with it.
        var stud = GetOwnedHero(player, proposal.StudHeroId);
        var mine = GetHero(proposal.ProposerHeroId);

        // Re-validate at consent: cooldowns and breed counts move between proposal and acceptance, and it
        // is this moment — not the proposal — that the fees are priced off and the breed is authorised.
        if (Sterility.IsSterile(mine.Genome, _config))
            throw new GameRuleException($"{mine.Name} is sterile — it cannot breed.");
        if (Sterility.IsSterile(stud.Genome, _config))
            throw new GameRuleException($"{stud.Name} is sterile — it cannot breed.");
        if (BreedingService.Validate(mine, stud, DateTimeOffset.UtcNow) is { } error)
            throw new GameRuleException(error);

        // TWO invoices, deliberately: the breed fee is the treasury's to keep and the stud fee is only
        // passing through on its way to the stud's owner. One invoice could carry both amounts but not
        // both meanings — the inflow tally dedups by invoice id, so a single id can be booked under a
        // single tag exactly once, and "breed income" and "sats owed onward" would become one number.
        var breedFee = BreedingPolicy.FeeSats(_config.BreedingFeeSats, mine.BreedCount + stud.BreedCount, _config);
        var breedInvoice = await chain.CreateFeeInvoiceAsync($"stud-breed:{proposal.Id}", breedFee, ct);
        var studInvoice = proposal.StudFeeSats > 0
            ? await chain.CreateFeeInvoiceAsync($"stud-fee:{proposal.Id}", proposal.StudFeeSats, ct)
            : null;

        proposal.BreedFeeSats = breedFee;
        proposal.BreedFeeInvoiceId = breedInvoice.InvoiceId;
        proposal.StudFeeInvoiceId = studInvoice?.InvoiceId;
        proposal.Accepted = true;
        // Durable NOW, before the proposer is handed anything to pay: the invoice ids are the only link
        // between sats about to leave a wallet and the consent that justified them.
        await persistence.SaveStudProposalAsync(proposal, ct);
        // THE CONSENT. The single most important entry in this flow: it records who agreed, to what, at
        // what price, and which invoices that agreement created. Actor is the STUD'S OWNER — they are the
        // one taking the action, even though it is the proposer's sats that move next.
        await AuditAsync(Persistence.AuditEventType.StudAccepted, player.Id,
            [proposal.Id, proposal.ProposerHeroId, proposal.StudHeroId, proposal.ProposerPlayerId],
            new
            {
                proposerPlayerId = proposal.ProposerPlayerId, proposerHeroId = proposal.ProposerHeroId,
                studHeroId = proposal.StudHeroId, breedFeeSats = breedFee,
                breedFeeInvoiceId = breedInvoice.InvoiceId, studFeeSats = proposal.StudFeeSats,
                studFeeInvoiceId = studInvoice?.InvoiceId,
            },
            $"stud-accepted:{proposal.Id}");
        return (proposal, breedInvoice, studInvoice);
    }

    /// <summary>
    /// Re-reads the invoices an accepted proposal bills. Needed because the accept response lands in the
    /// STUD OWNER's browser while the sats are the PROPOSER's to send — without this the party who owes the
    /// money has no way to learn what it is. Either party may read it; neither is billed by looking.
    /// </summary>
    public async Task<(StudProposal Proposal, FeeInvoice BreedFeeInvoice, FeeInvoice? StudFeeInvoice)> GetStudInvoicesAsync(
        Player player, string proposalId, CancellationToken ct)
    {
        if (!store.StudProposals.TryGetValue(proposalId, out var proposal))
            throw new GameRuleException($"Unknown stud proposal '{proposalId}'.");
        if (proposal.ProposerPlayerId != player.Id && proposal.StudOwnerPlayerId != player.Id)
            throw new GameRuleException("Only this proposal's parties can see its invoices.");
        // Nothing is billed before consent, so there is nothing here to show — and saying so plainly is the
        // point: an un-accepted proposal has no price yet.
        if (!proposal.Accepted)
            throw new GameRuleException("The stud's owner hasn't accepted this proposal yet — nothing is billed until they do.");
        var breedInvoice = await chain.GetFeeInvoiceAsync(proposal.BreedFeeInvoiceId!, ct)
            ?? throw new GameRuleException("This proposal's breeding fee invoice is no longer available.");
        var studInvoice = proposal.StudFeeInvoiceId is { } id ? await chain.GetFeeInvoiceAsync(id, ct) : null;
        return (proposal, breedInvoice, studInvoice);
    }

    /// <summary>The stud owner's refusal — terminal, and the counterpart to <see cref="AcceptStudAsync"/>.
    /// Nothing to unwind: a declined proposal was never billed.</summary>
    public async Task<StudProposal> DeclineStudAsync(Player player, string proposalId, CancellationToken ct)
    {
        if (!store.StudProposals.TryGetValue(proposalId, out var proposal))
            throw new GameRuleException($"Unknown stud proposal '{proposalId}'.");
        using var gate = await store.LockAsync($"stud:{proposalId}", ct);
        if (proposal.StudOwnerPlayerId != player.Id)
            throw new GameRuleException("Only the stud's owner can decline this proposal.");
        if (proposal.Completed) throw new GameRuleException("Stud proposal already bred.");
        // An acceptance has already priced fees the proposer may have paid against — it is not the stud
        // owner's to take back unilaterally.
        if (proposal.Accepted) throw new GameRuleException("Stud proposal already accepted.");
        proposal.Declined = true;
        await persistence.SaveStudProposalAsync(proposal, ct);
        await AuditAsync(Persistence.AuditEventType.StudDeclined, player.Id,
            [proposal.Id, proposal.ProposerHeroId, proposal.StudHeroId, proposal.ProposerPlayerId],
            new
            {
                proposerPlayerId = proposal.ProposerPlayerId, proposerHeroId = proposal.ProposerHeroId,
                studHeroId = proposal.StudHeroId, studFeeSats = proposal.StudFeeSats,
            },
            $"stud-declined:{proposal.Id}");
        return proposal;
    }

    /// <summary>
    /// The proposer reveals: both fees must be paid, the stud's owner is paid theirs, and the child mints
    /// to the proposer. The stud's owner is paid in sats, not in offspring.
    /// </summary>
    public async Task<(Hero Child, string ServerSeedHex, string EntropyHex, long StudFeePaidSats, Shared.ProgressionReceiptDto Receipt)> RevealStudAsync(
        Player player, string proposalId, string nonce, CancellationToken ct)
    {
        if (!store.StudProposals.TryGetValue(proposalId, out var proposal) || proposal.ProposerPlayerId != player.Id)
            throw new GameRuleException($"Unknown stud proposal '{proposalId}'.");
        // Per-proposal gate: consent-check → completed-check → payout → mint → complete-set must be one
        // atomic step, or two concurrent reveals of one acceptance both pass the guards and mint twice off
        // a single consent.
        using var gate = await store.LockAsync($"stud:{proposalId}", ct);

        // ── THE CONSENT GATE ──────────────────────────────────────────────
        // Everything below this line spends the proposer's sats and mints a hero out of a stud that is not
        // theirs. None of it may happen on the proposer's say-so alone.
        if (!proposal.Accepted)
            throw new GameRuleException("The stud's owner hasn't accepted this proposal yet.");
        if (proposal.Declined)
            throw new GameRuleException("The stud's owner declined this proposal.");
        // One acceptance buys one breed: the latch is what stops a paid consent being replayed for a
        // second free child.
        if (proposal.Completed) throw new GameRuleException("Stud proposal already bred.");
        if (string.IsNullOrWhiteSpace(nonce)) throw new GameRuleException("A nonce is required.");

        if (!await chain.IsInvoicePaidAsync(proposal.BreedFeeInvoiceId!, ct))
            throw new GameRuleException("The breeding fee invoice has not been paid yet — pay it from your wallet, then reveal.");
        if (proposal.StudFeeInvoiceId is { } studInvoiceId && !await chain.IsInvoicePaidAsync(studInvoiceId, ct))
            throw new GameRuleException("The stud fee invoice has not been paid yet — pay it from your wallet, then reveal.");

        // Both fees are now in the treasury (paid Receive invoices) — tally them, deduped by invoice id.
        // The stud fee is booked as income here and as an outflow again below: it really does land in the
        // treasury before it is forwarded, and a ledger that showed only the payout would read as a gift.
        await store.RecordInflowAsync(proposal.BreedFeeInvoiceId!, "breed", proposal.BreedFeeSats, ct);
        if (proposal.StudFeeInvoiceId is { } paidStudInvoiceId)
            await store.RecordInflowAsync(paidStudInvoiceId, "stud", proposal.StudFeeSats, ct);

        var parentA = GetOwnedHero(player, proposal.ProposerHeroId);
        var parentB = GetHero(proposal.StudHeroId);
        // The consent came from the owner at accept time. A stud sold since then is a hero whose current
        // owner never agreed to this, and whose cooldown this breed would spend.
        if (parentB.OwnerId != proposal.StudOwnerPlayerId)
            throw new GameRuleException($"{parentB.Name} has changed hands since its owner consented — this proposal no longer holds.");
        var now = DateTimeOffset.UtcNow;
        if (BreedingService.Validate(parentA, parentB, now) is { } error)
            throw new GameRuleException(error);

        // The stud fee is REAL BITCOIN owed to another player, so it moves BEFORE the mint and behind its
        // own durable latch (the daily-claim pattern): consume the latch first, then pay. Paying after the
        // mint instead would leave the only ordering where a fault strands the stud's owner unpaid with the
        // child already minted — and latching after the payout would let a crash in between pay twice.
        if (proposal.StudFeeSats > 0 && !proposal.StudFeePaid)
        {
            proposal.StudFeePaid = true;
            await persistence.SaveStudProposalAsync(proposal, ct);
            try
            {
                await chain.PayoutAsync(proposal.StudOwnerPlayerId, proposal.StudFeeSats, $"stud-fee:{proposal.Id}", ct);
                await store.RecordOutflowAsync("stud", proposal.StudFeeSats, ct);
            }
            catch
            {
                // A cleanly failed payout releases the latch IN MEMORY ONLY, so the proposer can retry in
                // this process. Never re-persist the release: if the payout actually settled before
                // throwing, the durable latch is the one thing keeping a restart from paying it twice.
                proposal.StudFeePaid = false;
                throw;
            }
        }

        var entropy = CommitReveal.DeriveEntropy(proposal.ServerSeed, proposal.ProposerHeroId, proposal.StudHeroId, nonce);
        var policy = new BreedingPolicy(_options.BreedingCooldownBaseUnit);
        var outcome = BreedingService.Breed(parentA, parentB, entropy, policy, _config);
        var serverSeedHex = Convert.ToHexString(proposal.ServerSeed).ToLowerInvariant();
        var entropyHex = Convert.ToHexString(entropy).ToLowerInvariant();

        // The child mints to the PROPOSER — that is what the stud fee bought.
        var child = await MintHeroAsync(player, outcome.ChildGenome, outcome.ChildGeneration,
            proposal.ProposerHeroId, proposal.StudHeroId, serverSeedHex, nonce, entropyHex, ct);

        // Chain FIRST, latch + in-memory effects after (the breed-reveal pattern): if the mint faults, the
        // proposal stays open and both parents untouched, so the already-paid fees can be retried instead
        // of stranded behind a Completed flag. The stud-fee latch above makes that retry safe — it pays
        // the stud's owner once, however many times the mint is attempted.
        proposal.Completed = true;
        proposal.ChildHeroId = child.Id;
        await persistence.SaveStudProposalAsync(proposal, ct);
        parentA.BreedCount++;
        parentA.BreedCooldownUntil = now + outcome.ParentACooldown;
        parentB.BreedCount++;
        parentB.BreedCooldownUntil = now + outcome.ParentBCooldown;
        // Parent breed-counts + cooldowns are progression — flushed, not saved inline (the CHILD's mint
        // above already saved durably), exactly as the ordinary breed reveal treats them.
        store.MarkHeroDirty(parentA.Id);
        store.MarkHeroDirty(parentB.Id);

        // Typed "breeding" like any other breed, because it is one: the same client-side
        // FairnessAudit.VerifyBreeding recompute applies, and the quests, season pass and Breeder badge
        // that count bred heroes have no reason to treat this child as a lesser one.
        var receipt = IssueReceipt(new Shared.ProgressionReceiptDto(
                "breeding", proposal.Id, proposal.ProposerHeroId, proposal.StudHeroId, child.Id,
                serverSeedHex, nonce, proposal.CommitmentHex,
                0, 0, parentA.Level, parentB.Level,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "", ""),
            proposal.ProposerHeroId, proposal.StudHeroId, child.Id);

        // The stud fee's actual MOVEMENT (in to the treasury, out to the stud's owner) is logged by the
        // treasury choke point, once each, under the invoice id. This entry is the breed it paid for —
        // both parties, both heroes, and who ended up with the child.
        await AuditAsync(Persistence.AuditEventType.StudRevealed, player.Id,
            [proposal.Id, proposal.ProposerHeroId, proposal.StudHeroId, child.Id, proposal.StudOwnerPlayerId],
            new
            {
                proposerPlayerId = proposal.ProposerPlayerId, studOwnerPlayerId = proposal.StudOwnerPlayerId,
                proposerHeroId = proposal.ProposerHeroId, studHeroId = proposal.StudHeroId,
                childHeroId = child.Id, childGeneration = child.Generation,
                breedFeeSats = proposal.BreedFeeSats, breedFeeInvoiceId = proposal.BreedFeeInvoiceId,
                studFeeSats = proposal.StudFeeSats, studFeeInvoiceId = proposal.StudFeeInvoiceId,
                studFeePaid = proposal.StudFeePaid, nonce, serverSeedHex, entropyHex, receiptId = receipt.Id,
            },
            $"stud-revealed:{proposal.Id}");

        return (child, serverSeedHex, entropyHex, proposal.StudFeePaid ? proposal.StudFeeSats : 0, receipt);
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
        await AuditAsync(Persistence.AuditEventType.GauntletOpened, player.Id, [session.Id, heroId],
            new { heroId, heroLevel = hero.Level, feeSats = fee, feeInvoiceId = invoice.InvoiceId, commitmentHex = session.CommitmentHex },
            $"gauntlet-opened:{session.Id}");
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

        // The full-clear item DELIVERY is recorded here rather than as its own event: it is a reward this
        // run produced, not a purchase, and there is no invoice to key it on. The fee's capture is logged
        // by the treasury choke point under the fee invoice id.
        await AuditAsync(Persistence.AuditEventType.GauntletRun, player.Id, [session.Id, session.HeroId],
            new
            {
                heroId = session.HeroId, feeSats = session.FeeSats, feeInvoiceId = session.FeeInvoiceId,
                wavesCleared = run.WavesCleared, xpAwarded = xpAward, preRunLevel, postRunLevel = hero.Level,
                itemAwarded, itemAssetId, nonce, serverSeedHex, entropyHex, receiptId = receipt.Id,
            },
            $"gauntlet-run:{session.Id}");

        return (run, xpAward, heroSnapshot, itemAwarded, itemAssetId, serverSeedHex, entropyHex, receipt);
    }

    // ── Endless PvE Trials: open (commit, FREE) → run (endless ghost ladder, score-only) ──

    /// <summary>Per-player cap on open Trials sessions. Trials is the one FREE, chainless open-flow, so
    /// without a bound a single player's open-loop would grow the in-memory store without limit (a
    /// one-player memory DoS). Generous for honest open→run play; a completed run is evicted in
    /// <see cref="RunTrials"/> (its score lives in the signed receipt + best-by-hero, not the session).</summary>
    public const int MaxOpenTrialsPerPlayer = 8;

    /// <summary>Async only because it appends to the audit log — the flow itself touches no I/O.</summary>
    public async Task<TrialsSession> OpenTrialsAsync(Player player, string heroId)
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
        await AuditAsync(Persistence.AuditEventType.TrialsOpened, player.Id, [session.Id, heroId],
            new { heroId, affix = session.Affix.ToString(), commitmentHex = session.CommitmentHex },
            $"trials-opened:{session.Id}");
        return session;
    }

    /// <summary>Async only because it appends to the audit log — the run itself touches no I/O.</summary>
    public async Task<(TrialsRun Run, Shared.HeroDto HeroSnapshot, string? Title, int BestScore, TrialsAffix Affix, string ServerSeedHex, string EntropyHex, Shared.ProgressionReceiptDto Receipt)> RunTrialsAsync(
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

        // Trials moves no sats and mints nothing, so it is not a money path — but it does change state (a
        // hero's personal best) and it is an action the player took, which is the bar for being here.
        await AuditAsync(Persistence.AuditEventType.TrialsRun, player.Id, [session.Id, session.HeroId],
            new
            {
                heroId = session.HeroId, wavesCleared = run.WavesCleared, title, bestScore = best,
                affix = session.Affix.ToString(), nonce, serverSeedHex, entropyHex, receiptId = receipt.Id,
            },
            $"trials-run:{session.Id}");

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
        await AuditAsync(Persistence.AuditEventType.MergeCommitted, player.Id, [session.Id, baseId, sacrificeId],
            new
            {
                mode, baseHeroId = baseId, sacrificeHeroId = sacrificeId, feeSats = session.FeeSats,
                escrowAddress = escrow, commitmentHex = session.CommitmentHex,
            },
            $"merge-committed:{session.Id}");
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
        // The headstone goes down FIRST, while both heroes are still in hand to be read: after the removal
        // below there is nothing left to name them with.
        await RecordTombstoneAsync(baseHero, "merge-input", session.Id, fused.Id, ct);
        await RecordTombstoneAsync(sacrificeHero, "merge-input", session.Id, fused.Id, ct);
        store.Heroes.TryRemove(session.BaseId, out _);
        store.Heroes.TryRemove(session.SacrificeId, out _);
        store.RecordBurn(); store.RecordBurn();
        await persistence.DeleteHeroAsync(session.BaseId, ct);
        await persistence.DeleteHeroAsync(session.SacrificeId, ct);
        // A BURN is the one state change with no durable row left behind to inspect afterwards — the hero's
        // row is erased on purpose, so without this entry the fact that it ever existed survives only in the
        // mint event. Keyed on the hero id: a hero can be burned exactly once, for all time.
        await AuditAsync(Persistence.AuditEventType.HeroBurned, player.Id, [session.BaseId, session.Id],
            new { heroId = session.BaseId, name = baseHero.Name, generation = baseHero.Generation, level = baseHero.Level, reason = "merge-input", sessionId = session.Id, replacedByHeroId = fused.Id },
            $"hero-burned:{session.BaseId}");
        await AuditAsync(Persistence.AuditEventType.HeroBurned, player.Id, [session.SacrificeId, session.Id],
            new { heroId = session.SacrificeId, name = sacrificeHero.Name, generation = sacrificeHero.Generation, level = sacrificeHero.Level, reason = "merge-input", sessionId = session.Id, replacedByHeroId = fused.Id },
            $"hero-burned:{session.SacrificeId}");

        var receipt = IssueReceipt(new Shared.ProgressionReceiptDto(
                "merge", session.Id, session.BaseId, session.SacrificeId, fused.Id,
                serverSeedHex, nonce, session.CommitmentHex,
                0, 0, baseHero.Level, sacrificeHero.Level,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "", ""),
            session.BaseId, session.SacrificeId, fused.Id);

        await AuditAsync(Persistence.AuditEventType.MergeRevealed, player.Id,
            [session.Id, session.BaseId, session.SacrificeId, fused.Id],
            new
            {
                mode = session.Mode, baseHeroId = session.BaseId, sacrificeHeroId = session.SacrificeId,
                fusedHeroId = fused.Id, fusedGeneration, fusedLevel = fused.Level, feeSats = session.FeeSats,
                nonce, serverSeedHex, entropyHex, receiptId = receipt.Id,
            },
            $"merge-revealed:{session.Id}");

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
        // PERMADEATH is on the table from this point, so the terms of the wager — which heroes, whose gear,
        // absorb or classic, and what each side was billed — are recorded before either party can stake.
        await AuditAsync(Persistence.AuditEventType.DeathMatchOpened, player.Id,
            [session.Id, challengerHeroId, defenderHeroId, defender.OwnerId],
            new
            {
                challengerHeroId, defenderHeroId, defenderPlayerId = defender.OwnerId, absorb,
                challengerGearItemIds = challengerGearIds, defenderGearItemIds = defenderGearIds,
                challengerFeeSats = Leveling.DeathMatchFee(challenger.Level, absorb, _config),
                challengerFeeInvoiceId = feeInvoice.InvoiceId, jointEscrowAddress = escrow,
                refundAfterUnixSeconds = refundAfter, commitmentHex = commitment,
            },
            $"deathmatch-opened:{session.Id}");
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
        // The defender's CONSENT to risk their hero permanently. Recorded for the same reason the stud
        // accept is: it is what a "I never agreed to that" dispute is settled against.
        await AuditAsync(Persistence.AuditEventType.DeathMatchAccepted, player.Id,
            [session.Id, session.ChallengerHeroId, session.DefenderHeroId, session.ChallengerPlayerId],
            new
            {
                challengerPlayerId = session.ChallengerPlayerId, challengerHeroId = session.ChallengerHeroId,
                defenderHeroId = session.DefenderHeroId, absorb = session.Absorb,
                defenderFeeSats = Leveling.DeathMatchFee(defender.Level, session.Absorb, _config),
                defenderFeeInvoiceId = feeInvoice.InvoiceId, jointEscrowAddress = session.JointEscrowAddress,
            },
            $"deathmatch-accepted:{session.Id}");
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
                // Headstones first, while both are still readable (see the merge burn).
                await RecordTombstoneAsync(winner, "deathmatch-absorb-winner", session.Id, absorbed.Id, ct);
                await RecordTombstoneAsync(loser, "deathmatch-absorb-loser", session.Id, absorbed.Id, ct);
                store.Heroes.TryRemove(winner.Id, out _);
                store.Heroes.TryRemove(loser.Id, out _);
                store.RecordBurn(); store.RecordBurn();
                await persistence.DeleteHeroAsync(winner.Id, ct);
                await persistence.DeleteHeroAsync(loser.Id, ct);
                // BOTH heroes are gone in absorb mode — the winner's too. Their rows are erased, so these
                // are the last records that they existed at all beyond their mint events.
                await AuditAsync(Persistence.AuditEventType.HeroBurned, winner.OwnerId, [winner.Id, session.Id],
                    new { heroId = winner.Id, name = winner.Name, generation = winner.Generation, level = winner.Level, reason = "deathmatch-absorb-winner", sessionId = session.Id, replacedByHeroId = absorbed.Id },
                    $"hero-burned:{winner.Id}");
                await AuditAsync(Persistence.AuditEventType.HeroBurned, loser.OwnerId, [loser.Id, session.Id],
                    new { heroId = loser.Id, name = loser.Name, generation = loser.Generation, level = loser.Level, reason = "deathmatch-absorb-loser", sessionId = session.Id, replacedByHeroId = absorbed.Id },
                    $"hero-burned:{loser.Id}");

                var absorbReceipt = IssueReceipt(new Shared.ProgressionReceiptDto(
                        "absorb", session.Id, session.ChallengerHeroId, session.DefenderHeroId, absorbed.Id,
                        serverSeedHex, nonce, session.CommitmentHex,
                        0, 0, winner.Level, loser.Level,
                        DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "", ""),
                    session.ChallengerHeroId, session.DefenderHeroId, absorbed.Id);
                await AuditAsync(Persistence.AuditEventType.DeathMatchSettled, player.Id,
                    [session.Id, session.ChallengerHeroId, session.DefenderHeroId, absorbed.Id],
                    new
                    {
                        outcome = "absorb-minted", challengerWon, winnerHeroId = result.WinnerId,
                        loserHeroId = loser.Id, absorbedHeroId = absorbed.Id,
                        traitsAbsorbed = outcome.TraitsAbsorbed, newGenomeHex = outcome.Result.ToHex(),
                        challengerFeeInvoiceId = session.ChallengerFeeInvoiceId,
                        defenderFeeInvoiceId = session.DefenderFeeInvoiceId,
                        settledByPlayerId = player.Id, nonce, serverSeedHex, entropyHex,
                        configVersion = session.ConfigVersion, contentVersion = session.ContentVersion,
                        receiptId = absorbReceipt.Id,
                    },
                    $"deathmatch-settled:{session.Id}");
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
        // The headstone first, while the loser is still readable (see the merge burn) — nothing is
        // replacedBy here: a classic death-match loser simply ends.
        await RecordTombstoneAsync(loser, "deathmatch-loser", session.Id, null, ct);
        store.Heroes.TryRemove(loser.Id, out _);
        store.RecordBurn();
        // The loser is burned on-chain — erase its durable row too, or a restart resurrects it.
        await persistence.DeleteHeroAsync(loser.Id, ct);
        await AuditAsync(Persistence.AuditEventType.HeroBurned, loser.OwnerId, [loser.Id, session.Id],
            new { heroId = loser.Id, name = loser.Name, generation = loser.Generation, level = loser.Level, reason = "deathmatch-loser", sessionId = session.Id, replacedByHeroId = (string?)null },
            $"hero-burned:{loser.Id}");

        var receipt = IssueReceipt(new Shared.ProgressionReceiptDto(
                "deathmatch", session.Id, session.ChallengerHeroId, session.DefenderHeroId, result.WinnerId,
                serverSeedHex, nonce, session.CommitmentHex,
                0, 0, challenger.Level, defender.Level,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "", ""),
            session.ChallengerHeroId, session.DefenderHeroId);

        await AuditAsync(Persistence.AuditEventType.DeathMatchSettled, player.Id,
            [session.Id, session.ChallengerHeroId, session.DefenderHeroId],
            new
            {
                outcome = "keep", challengerWon, winnerHeroId = result.WinnerId, loserHeroId = loser.Id,
                absorbedHeroId = (string?)null, absorbRequested = session.Absorb,
                challengerFeeInvoiceId = session.ChallengerFeeInvoiceId,
                defenderFeeInvoiceId = session.DefenderFeeInvoiceId,
                settledByPlayerId = player.Id, nonce, serverSeedHex, entropyHex,
                configVersion = session.ConfigVersion, contentVersion = session.ContentVersion,
                receiptId = receipt.Id,
            },
            $"deathmatch-settled:{session.Id}");

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
        // Self-duels allowed: a hero fighting itself is a legitimate way to see the engine work. A
        // WAGERED one is still refused below — staking against yourself is not a match, it is a fee.
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
        await AuditAsync(Persistence.AuditEventType.MatchOpened, player.Id,
            [session.Id, challengerHeroId, defenderHeroId, defender.OwnerId],
            new
            {
                challengerHeroId, defenderHeroId, defenderPlayerId = defender.OwnerId, wagerSats, mode,
                stakeInvoiceId = invoice?.InvoiceId,
                matchFeeSats = wagerSats > 0 ? Leveling.MatchFee(challenger.Level, _config) : 0,
                matchFeeInvoiceId = feeInvoice?.InvoiceId,
                escrowChallengerAddress = escrowChallenger, escrowDefenderAddress = escrowDefender,
                refundAfterUnixSeconds = refundAfterUnix, commitmentHex,
            },
            $"match-opened:{session.Id}");
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
        await AuditAsync(Persistence.AuditEventType.MatchAccepted, player.Id,
            [session.Id, session.ChallengerHeroId, session.DefenderHeroId, session.ChallengerPlayerId],
            new
            {
                challengerPlayerId = session.ChallengerPlayerId, challengerHeroId = session.ChallengerHeroId,
                defenderHeroId = session.DefenderHeroId, wagerSats = session.WagerSats, mode = session.Mode,
                stakeInvoiceId = invoice?.InvoiceId,
                matchFeeSats = Leveling.MatchFee(defender.Level, _config),
                matchFeeInvoiceId = feeInvoice.InvoiceId,
            },
            $"match-accepted:{session.Id}");
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
            if (!abandoned) continue;
            var wasStatus = m.Status;
            m.Status = "expired";
            // Actor NULL: nobody DID this — the refund window simply passed and the chain says the stakes
            // are not both there. It runs lazily on every match listing, so the dedup key is what keeps one
            // expiry to one entry however many anonymous page loads observe it.
            await AuditAsync(Persistence.AuditEventType.MatchExpired, null,
                [m.Id, m.ChallengerHeroId, m.DefenderHeroId],
                new
                {
                    fromStatus = wasStatus, wagerSats = m.WagerSats, mode = m.Mode,
                    challengerPlayerId = m.ChallengerPlayerId, defenderPlayerId = m.DefenderPlayerId,
                    challengerFunded = funding.ChallengerFunded, defenderFunded = funding.DefenderFunded,
                    refundAfterUnixSeconds = m.RefundAfterUnixSeconds,
                },
                $"match-expired:{m.Id}");
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
                // Actor NULL: nobody asked for this — it happens as a side effect of somebody reading the
                // season board. The settled marker is in-memory and drops on restart (deliberately, with
                // the receipts that define the winners), so the dedup key here is what stops a restarted
                // server logging the same season a second time even though it will not re-pay it.
                await AuditAsync(Persistence.AuditEventType.SeasonSettled, null,
                    [.. standings.Select(e => e.OwnerId)],
                    new
                    {
                        season = s, potSats = pot,
                        winners = standings.Select((e, i) => new { rank = e.Rank, e.OwnerId, e.HeroId, e.Name, shareSats = shares[i] }).ToList(),
                    },
                    $"season-settled:{s}");
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

        // Logged AFTER the payout so a claim that threw on a failed payout — and released the day in
        // memory so the player can retry — is not recorded as a claim that happened. The day index is the
        // once-only key, exactly as it is for the day itself: one claim per player per UTC day, one entry.
        await AuditAsync(Persistence.AuditEventType.DailyClaimed, player.Id, [player.Id],
            new
            {
                dayIndex = window.DayIndex, paidSats = affordable, entitledSats = reward.Total,
                baseSats = reward.Base, questBonusSats = reward.QuestBonus,
                streakBonusPct = reward.StreakBonusPct, streak = newStreak,
                questsCompleted = completed.Select(q => q.Id).ToList(),
                cappedByTreasury = affordable < reward.Total,
            },
            $"daily-claimed:{player.Id}:{window.DayIndex}");

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

        await AuditAsync(Persistence.AuditEventType.MatchResolved, player.Id,
            [session.Id, challenger.Id, defender.Id],
            new
            {
                challengerHeroId = challenger.Id, defenderHeroId = defender.Id,
                challengerPlayerId = session.ChallengerPlayerId, defenderPlayerId = session.DefenderPlayerId,
                winnerHeroId = result.WinnerId, wagerSats = session.WagerSats, mode = session.Mode,
                winnerPayoutSats = winnerPayout, challengerXpDelta = challengerDelta,
                defenderXpDelta = defenderDelta, staked = session.WagerSats > 0,
                nonce, serverSeedHex = serverSeedHexOut, entropyHex = session.EntropyHex,
                configVersion = session.ConfigVersion, contentVersion = session.ContentVersion,
                receiptId = receipt.Id,
            },
            $"match-resolved:{session.Id}");

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
        await AuditAsync(Persistence.AuditEventType.SquadOpened, player.Id,
            [session.Id, .. req.ChallengerLineup, .. req.DefenderLineup, defenderOwner],
            new
            {
                challengerLineup = req.ChallengerLineup, defenderLineup = req.DefenderLineup,
                defenderPlayerId = session.DefenderPlayerId, wagerSats = req.WagerSats, mode = req.Mode,
                stakeInvoiceId = invoice?.InvoiceId,
                matchFeeSats = req.WagerSats > 0 ? Leveling.MatchFee(challengerLineup.Max(h => h.Level), _config) : 0,
                matchFeeInvoiceId = feeInvoice?.InvoiceId,
                escrowChallengerAddress = escrowChallenger, escrowDefenderAddress = escrowDefender,
                refundAfterUnixSeconds = refundAfterUnix, commitmentHex,
            },
            $"squad-opened:{session.Id}");
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
        await AuditAsync(Persistence.AuditEventType.SquadAccepted, player.Id,
            [session.Id, .. session.ChallengerLineup, .. session.DefenderLineup, session.ChallengerPlayerId],
            new
            {
                challengerPlayerId = session.ChallengerPlayerId,
                challengerLineup = session.ChallengerLineup, defenderLineup = session.DefenderLineup,
                wagerSats = session.WagerSats, mode = session.Mode, stakeInvoiceId = invoice?.InvoiceId,
                matchFeeSats = Leveling.MatchFee(defenders.Max(h => h.Level), _config),
                matchFeeInvoiceId = feeInvoice.InvoiceId,
            },
            $"squad-accepted:{session.Id}");
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

        await AuditAsync(Persistence.AuditEventType.SquadResolved, player.Id,
            [session.Id, .. session.ChallengerLineup, .. session.DefenderLineup],
            new
            {
                challengerPlayerId = session.ChallengerPlayerId, defenderPlayerId = session.DefenderPlayerId,
                challengerLineup = session.ChallengerLineup, defenderLineup = session.DefenderLineup,
                challengerWon, wagerSats = session.WagerSats, mode = session.Mode,
                winnerPayoutSats = winnerPayout, staked = session.WagerSats > 0,
                duels = result.Duels.Select(d => new { d.Slot, winnerHeroId = d.Result.WinnerId }).ToList(),
                nonce, serverSeedHex = serverSeedHexOut, entropyHex = session.EntropyHex,
                configVersion = session.ConfigVersion, contentVersion = session.ContentVersion,
                receiptIds = duelReceipts.Select(r => r.Id).ToList(),
            },
            $"squad-resolved:{session.Id}");

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
        var strippedGear = hero.Equipment.Slots.Values.ToList();
        foreach (var slot in hero.Equipment.Slots.Keys.ToList())
            hero.Equipment.Unequip(slot);

        hero.OwnerId = toPlayerId;
        // TRANSFER is an identity event: a crash inside the flush window must not rehydrate the hero back
        // to the sender — the chain already shows the recipient holding the asset. (Captures the stripped
        // loadout in the same write.)
        await persistence.SaveHeroAsync(hero, ct);
        // No dedup key: a hero can change hands any number of times and each move is its own fact. The
        // durable hero row keeps only the CURRENT owner, so this chain of entries is the only place the
        // custody history of a hero exists at all. A retry cannot double-log — the ownership check above
        // refuses a second confirm from a sender who no longer owns it.
        await AuditAsync(Persistence.AuditEventType.HeroTransferred, player.Id, [heroId, toPlayerId],
            new { heroId, fromPlayerId = player.Id, toPlayerId, assetId = hero.AssetId, strippedGear, reason = "wallet-transfer" });
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
        await AuditAsync(Persistence.AuditEventType.ItemInvoiced, player.Id, [invoice.InvoiceId],
            new { itemId = item.Id, itemName = item.Name, priceSats = item.PriceSats, invoiceId = invoice.InvoiceId },
            $"item-invoiced:{invoice.InvoiceId}");
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
            // Keyed on the invoice, which is the purchase's identity: re-delivery after a crash is
            // DELIBERATE in this flow, so the key is what stops one paid purchase logging two deliveries.
            await AuditAsync(Persistence.AuditEventType.ItemClaimed, player.Id, [invoiceId, delivery.ItemAssetId],
                new
                {
                    itemId = item.Id, itemName = item.Name, priceSats = item.PriceSats, invoiceId,
                    itemAssetId = delivery.ItemAssetId, deliveryTxId = delivery.ArkTxId,
                },
                $"item-claimed:{invoiceId}");
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

        var displaced = current;   // whatever this slot held before, if anything
        hero.Equipment.Equip(item);
        store.MarkHeroDirty(hero.Id);   // the loadout is progression — the flush persists it
        // No dedup key: re-gearing is ordinary, repeatable play and every change is its own fact. Gear is
        // a COMBAT INPUT and it is staked in a death-match, so "what was this hero wearing, and when did
        // that change" is a question the replay stamps cannot answer on their own.
        await AuditAsync(Persistence.AuditEventType.HeroEquipped, player.Id, [heroId],
            new { heroId, itemId = item.Id, itemName = item.Name, slot = item.Slot.ToString(), displacedItemId = displaced, heroLevel = hero.Level });
        return hero;
    }

    /// <summary>Async only because it appends to the audit log — the unequip itself touches no I/O.</summary>
    public async Task<Hero> UnequipAsync(Player player, string heroId, string slotName)
    {
        var hero = GetOwnedHero(player, heroId);
        if (!Enum.TryParse<Core.Equipment.EquipmentSlot>(slotName, ignoreCase: true, out var slot))
            throw new GameRuleException($"Unknown slot '{slotName}' (Weapon/Armor/Trinket).");
        var removed = hero.Equipment.Slots.GetValueOrDefault(slot);
        if (!hero.Equipment.Unequip(slot))
            throw new GameRuleException($"{hero.Name} has nothing equipped in {slot}.");
        store.MarkHeroDirty(hero.Id);   // the loadout is progression — the flush persists it
        await AuditAsync(Persistence.AuditEventType.HeroUnequipped, player.Id, [heroId],
            new { heroId, itemId = removed, slot = slot.ToString(), heroLevel = hero.Level });
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
        await AuditAsync(Persistence.AuditEventType.OfferListed, player.Id, [offerId],
            new
            {
                offerId, kind = "item", itemId = item.Id, itemName = item.Name, heroId = (string?)null,
                askSats, listingFeeSats = fee, offerAddress = info.OfferAddress,
                itemAssetId = info.ItemAssetId, offerValueSats = info.OfferValueSats,
                refundAfterUnixSeconds = info.RefundAfterUnixSeconds,
            },
            $"offer-listed:{offerId}");
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
            // Was this close a SALE? Asked once and read for THREE purposes now: booking the listing fee,
            // recording what a hero fetched, and saying which way the listing ended in the audit log. This
            // is the LAST moment any of them can be answered: `closed` is the same state a seller's reclaim
            // lands in, so a step from here the difference is gone for good. The call is skipped only when
            // none of the purposes applies (no fee to book, and a fungible item unit that has no page to
            // have a history on), and it costs at most one chain read per offer ever, since only a real
            // transition reaches this branch.
            var probed = offer.ListingFeeSats > 0 || offer.Kind == "hero";
            var sold = probed && await chain.WasOfferSoldAsync(offer.Id, ct);
            // The asset LEFT the covenant, which is the moment the listing's outcome is decided. Actor is
            // NULL because from here the two spends are only distinguishable by whether the treasury got
            // its cut — the buyer, if there was one, is not knowable from this observation. `sold: false`
            // is the normal reclaim, not an error.
            //
            // `sold` is TRI-STATE on the wire: null when the probe above was skipped, because a fee-free
            // ITEM offer is genuinely unknowable from here and recording `false` would assert a reclaim
            // that may not have happened. The probe stays gated exactly as it was — logging must not start
            // costing a chain call the game did not already need.
            //
            // The dedup key is SHARED with ClaimPurchasedHeroAsync's close (both prove the same close by
            // different means, and both run), so whichever observes it first records it and the other is
            // absorbed — mirroring exactly what OfferSaleInflowId does for the fee and what
            // RecordHeroSaleAsync does for the price.
            await AuditAsync(Persistence.AuditEventType.OfferClosed, null, [offer.Id, offer.HeroId],
                new
                {
                    offerId = offer.Id, kind = offer.Kind, sellerId = offer.SellerId, itemId = offer.ItemId,
                    heroId = offer.HeroId, askSats = offer.AskSats, listingFeeSats = offer.ListingFeeSats,
                    sold = probed ? sold : (bool?)null, observedBy = "reconcile",
                },
                $"offer-closed:{offer.Id}");
            // Buyer unknown by construction here: the covenant's treasury leg proves a sale happened, not
            // who it was to. A later claim fills them in under the same key.
            if (sold) await RecordHeroSaleAsync(offer, buyerId: null, ct);
            if (offer.ListingFeeSats > 0)
            {
                if (sold)
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

    /// <summary>
    /// Records a hero sale, once, from whichever proof got here first.
    ///
    /// Both callers have already established that the offer CLOSED ON A SALE — this only writes the fact
    /// down. Item offers fall straight through: a fungible unit has no page and no history. The store's
    /// keyed dedup decides whether anything is new, and only a real change reaches the disk, so this is
    /// safe to call from a retried settle and safe to call in either order.
    ///
    /// Deliberately swallows a write failure, for the same reason the treasury tally does: it sits behind
    /// code that has already moved sats and whose callers unwind in-memory state on a throw. A throw out
    /// of here would abandon <c>ClaimPurchasedHeroAsync</c> AFTER the hero had durably changed owner,
    /// leaving the buyer's claim reported as failed on a hero that is already theirs. A missing row costs
    /// one line of a hero's history; nothing else reads it.
    /// </summary>
    private async Task RecordHeroSaleAsync(OfferListing offer, string? buyerId, CancellationToken ct)
    {
        if (offer.Kind != "hero" || offer.HeroId is not { } heroId) return;
        var sale = new HeroSale(offer.Id, heroId, offer.SellerId, buyerId,
            offer.AskSats, offer.ListingFeeSats, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        if (store.RecordHeroSale(sale) is not { } changed) return;
        try
        {
            await persistence.SaveHeroSaleAsync(changed, ct);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Hero {HeroId} sold for {Sats} sat under offer {OfferId}, but the sale "
                                   + "was not persisted; its page will forget the price after a restart.",
                heroId, offer.AskSats, offer.Id);
        }
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

        // An accepted BID is the one row here that is not a covenant escrow: the bidder's sats sit in the
        // treasury under an invoice, and POST /api/bids/{id}/refund is what recovers them rather than a
        // reclaim leaf. It belongs on this list anyway, because it is the same question the page exists to
        // answer — "something of mine is tied up with no way forward, when can I get it back?" — and a
        // player should not have to know which mechanism holds their sats to find them.
        //
        // Listed for BOTH parties: the bidder needs their money back, and the owner needs the hero freed
        // from a funded bidder who went quiet. Only past acceptance, because nothing is billed before it.
        foreach (var bid in store.HeroBids.Values
                     .Where(b => b.Accepted && b.IsLive && !b.SellerPaid
                                 && (b.BidderPlayerId == player.Id || b.OwnerPlayerId == player.Id)).ToList())
        {
            var name = store.Heroes.TryGetValue(bid.HeroId, out var h) ? h.Name : "a hero";
            items.Add(new Shared.ReclaimableDto("bid", bid.Id,
                bid.BidderPlayerId == player.Id
                    ? $"An accepted bid on {name} — your {bid.BidSats} sats are held against a hero that hasn't arrived."
                    : $"An accepted bid on {name} — unwind it to free the hero if the bidder has gone quiet.",
                bid.ReclaimAfterUnixSeconds));
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
        await AuditAsync(Persistence.AuditEventType.OfferListed, player.Id, [offerId, heroId],
            new
            {
                offerId, kind = "hero", itemId = (string?)null, itemName = (string?)null, heroId,
                heroName = hero.Name, askSats, listingFeeSats = fee, offerAddress = info.OfferAddress,
                itemAssetId = info.ItemAssetId, offerValueSats = info.OfferValueSats,
                refundAfterUnixSeconds = info.RefundAfterUnixSeconds,
            },
            $"offer-listed:{offerId}");
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

        var sellerId = offer.SellerId;
        var strippedGear = hero.Equipment.Slots.Values.ToList();
        foreach (var slot in hero.Equipment.Slots.Keys.ToList())
            hero.Equipment.Unequip(slot);
        hero.OwnerId = buyer.Id;
        // The marketplace's transfer moment — same identity event as ConfirmTransferAsync, same rule:
        // the buyer's ownership goes durable now, not at the next flush.
        await persistence.SaveHeroAsync(hero, ct);
        // Logged as a TRANSFER as well as a sale, so "who has owned this hero" is one query on one event
        // type rather than a union the reader has to know to take — this is the only place a hero's whole
        // custody chain exists, since the hero row keeps only the CURRENT owner and a wallet transfer
        // creates no sale row at all.
        //
        // The price is carried too, but this is NOT the source of truth for it: PersistedHeroSale below is,
        // and the public hero timeline reads that. That record is narrower and purpose-built (one row per
        // sale, keyed by the offer, filled in by whichever prover knows the buyer), and nothing here
        // replaces or competes with it. The log complements it by covering the transfers it deliberately
        // does not model.
        await AuditAsync(Persistence.AuditEventType.HeroTransferred, buyer.Id, [hero.Id, sellerId, offer.Id],
            new { heroId = hero.Id, fromPlayerId = sellerId, toPlayerId = buyer.Id, assetId = hero.AssetId, strippedGear, reason = "marketplace-sale", offerId = offer.Id, salePriceSats = offer.AskSats });
        offer.Status = "closed";
        // A CONFIRMED sale — the chain shows the buyer holding the hero, so the fulfil ran and its
        // covenant paid the treasury its cut. ReconcileOfferAsync now proves the same thing a second
        // way (the treasury leg of the spending transaction), and both book under this one key, so the
        // store's inflow dedup makes the sale once-only however often either path runs.
        if (offer.ListingFeeSats > 0)
            await store.RecordInflowAsync(OfferSaleInflowId(offer.Id), "listing", offer.ListingFeeSats, ct);
        // The same confirmed sale, kept as history. This path is the one that knows the BUYER — the chain
        // was just asked whether they hold the asset — so it records what reconcile cannot, and fills in
        // the buyer on a row reconcile may already have written under the same key.
        await RecordHeroSaleAsync(offer, buyer.Id, ct);
        // The close goes durable last, for the same reason it does in ReconcileOfferAsync: a crash before this
        // point leaves the offer rehydrating as `active`, and reconcile then closes it and re-books under the
        // same once-only key. The hero's new ownership is already durable above, so the only thing still at
        // stake here is whether the sale can be found again — never who owns the hero.
        await persistence.SaveOfferAsync(offer, ct);
        // The buyer's own action, and a CONFIRMED sale — the chain shows them holding the hero, so unlike
        // the reconcile's observation this one knows there was a buyer and who it is.
        await AuditAsync(Persistence.AuditEventType.OfferHeroClaimed, buyer.Id, [offer.Id, hero.Id, sellerId],
            new
            {
                offerId = offer.Id, heroId = hero.Id, sellerId, buyerId = buyer.Id,
                salePriceSats = offer.AskSats, listingFeeSats = offer.ListingFeeSats,
            },
            $"offer-hero-claimed:{offer.Id}");
        // …and the CLOSE, under the key ReconcileOfferAsync also uses, so the offer's close is recorded
        // exactly once however many times either path observes it. `sold: true` is known here, not inferred.
        await AuditAsync(Persistence.AuditEventType.OfferClosed, null, [offer.Id, hero.Id],
            new
            {
                offerId = offer.Id, kind = offer.Kind, sellerId, itemId = offer.ItemId, heroId = hero.Id,
                askSats = offer.AskSats, listingFeeSats = offer.ListingFeeSats, sold = (bool?)true,
                observedBy = "hero-claim",
            },
            $"offer-closed:{offer.Id}");
        return hero;
    }

    // ── Bids: buy a hero that is NOT for sale (propose → the owner CONSENTS → deliver → settle) ──
    //
    // The marketplace only ever ran one way: an owner lists, a buyer fulfils. A hero nobody has listed had no
    // price and no door. This is that door, and it is shaped exactly like the stud service — propose →
    // accept → the value moves — because it needs the same thing: a counterparty who owns the asset and has
    // to say yes. NOTHING IS BILLED UNTIL THE OWNER ACCEPTS, so a bid that is ignored or refused costs the
    // bidder not one sat, because no invoice ever existed to pay.
    //
    // Where it must differ from stud is the tail. A stud reveal is executed BY THE SERVER, so an accepted
    // proposal always completes. A hero moves only by its owner's own wallet (the non-custodial mandate —
    // see ConfirmTransferAsync), so an accepted bid can be funded and then simply never honoured. That is
    // the one way this flow can strand real sats, and RefundBidAsync is its exit: past a timelock window,
    // either party unwinds the bid and the money goes home.

    /// <summary>
    /// Bids on ANOTHER player's hero. Nothing is billed and nothing moves: this is an offer, and its
    /// counterparty has not agreed to anything yet.
    /// </summary>
    public async Task<HeroBid> ProposeBidAsync(Player player, string heroId, long bidSats, CancellationToken ct)
    {
        RequireAcceptedTerms(player);
        var hero = GetHero(heroId);
        // The whole point of the flow: the hero belongs to someone else. Bidding on your own would route a
        // payment to yourself through a treasury that takes a cut of it.
        if (hero.OwnerId == player.Id)
            throw new GameRuleException("You already own this hero.");
        // Priced through the SAME knob a listing's ask is, and refused on the same boundary — an amount at
        // or below the fee would net the owner nothing or less, and no honest sale looks like that.
        var fee = MarketplaceFeeFor(bidSats, hero.Name);

        var live = store.HeroBids.Values.Where(b => b.IsLive).ToList();
        if (live.Any(b => b.BidderPlayerId == player.Id && b.HeroId == heroId))
            throw new GameRuleException($"You already have a live bid on {hero.Name} — withdraw it first to bid again.");
        var cap = _options.MaxOpenBidsPerPlayer;
        if (cap > 0 && live.Count(b => b.BidderPlayerId == player.Id) >= cap)
            throw new GameRuleException($"You already have {cap} live bids — settle or withdraw one before bidding again.");

        var bid = new HeroBid
        {
            Id = NewId("bid"),
            BidderPlayerId = player.Id,
            // Pinned, like a stud's owner: a hero sold on before the bid is answered has an owner who was
            // never offered anything, and AcceptBidAsync re-checks that this is still who holds it.
            OwnerPlayerId = hero.OwnerId,
            HeroId = heroId,
            BidSats = bidSats,
        };
        store.HeroBids[bid.Id] = bid;
        // Durable at PROPOSAL even though nothing is owed yet: the owner may accept against this row minutes
        // later, and the accept is what creates the invoice. A bid lost across a restart is an offer the
        // owner answered that nothing can name.
        await persistence.SaveHeroBidAsync(bid, ct);
        await AuditAsync(Persistence.AuditEventType.BidPlaced, player.Id, [bid.Id, heroId, hero.OwnerId],
            new
            {
                bidId = bid.Id, heroId, heroName = hero.Name, ownerPlayerId = hero.OwnerId,
                bidSats, projectedFeeSats = fee,
            },
            $"bid-placed:{bid.Id}");
        return bid;
    }

    /// <summary>
    /// The owner's CONSENT — the gate the whole flow hangs on, and the only place the invoice is created.
    /// Before this returns the bidder has been billed nothing; after it, they may fund the bid and the owner
    /// may deliver the hero.
    /// </summary>
    public async Task<(HeroBid Bid, FeeInvoice Invoice)> AcceptBidAsync(
        Player player, string bidId, CancellationToken ct)
    {
        RequireAcceptedTerms(player);
        if (!store.HeroBids.TryGetValue(bidId, out var bid))
            throw new GameRuleException($"Unknown bid '{bidId}'.");
        // Per-bid gate: the accepted-check → invoice → accepted-set must be one atomic step, or two
        // concurrent accepts of one bid bill the bidder twice. The SAME key settle and refund take, so an
        // accept can never interleave with the settle it authorises.
        using var gate = await store.LockAsync($"bid:{bidId}", ct);
        if (bid.OwnerPlayerId != player.Id)
            throw new GameRuleException("Only this hero's owner can accept a bid on it.");
        if (bid.Accepted) throw new GameRuleException("This bid is already accepted.");
        if (bid.Declined) throw new GameRuleException("This bid was already declined.");
        if (bid.Withdrawn) throw new GameRuleException("This bid was withdrawn by its bidder.");
        // Consent has to come from whoever owns the hero NOW — one sold since the bid was made took its
        // owner's say-so with it (the stud flow's rule, for the same reason).
        var hero = GetOwnedHero(player, bid.HeroId);
        if (string.IsNullOrEmpty(hero.AssetId))
            throw new GameRuleException($"{hero.Name} has no on-chain asset, so it cannot be sold.");
        // A hero resting in a live listing is ALREADY escrowed in an offer covenant — its owner cannot hand
        // it to a bidder as well, and accepting would promise the same hero to two buyers.
        if (store.Offers.Values.Any(o => o.Kind == "hero" && o.HeroId == bid.HeroId && o.Status is "pending" or "active"))
            throw new GameRuleException($"{hero.Name} is listed for sale — cancel the listing before accepting a bid.");
        // One live acceptance per hero, for the same reason: a second accepted bid would be a second
        // funded claim on one hero. The outstanding one has to resolve first — settled, or refunded past
        // its window, which either party can trigger.
        if (store.HeroBids.Values.Any(b => b.Id != bid.Id && b.HeroId == bid.HeroId && b.Accepted && b.IsLive))
            throw new GameRuleException(
                $"Another bid on {hero.Name} is already accepted and awaiting delivery — settle or unwind it first.");

        // Priced at CONSENT, off the same knob a listing uses: it is this moment, not the bid, that the sale
        // is authorised at. Re-checked here because the fee is configurable and may have moved since.
        var fee = MarketplaceFeeFor(bid.BidSats, hero.Name);
        var invoice = await chain.CreateFeeInvoiceAsync($"bid:{bid.Id}", bid.BidSats, ct);
        bid.FeeSats = fee;
        bid.BidInvoiceId = invoice.InvoiceId;
        bid.Accepted = true;
        // The bidder's exit, stamped now: past this the sats they are about to send can always come home,
        // however the owner behaves. A SERVER-clock window (these sats rest in the treasury under an
        // invoice, not behind a covenant reclaim leaf) — but the same duration the escrow refunds use, and
        // surfaced through the same ReclaimWindow vocabulary, so a player reads one kind of wait.
        bid.ReclaimAfterUnixSeconds = DateTimeOffset.UtcNow.Add(_options.WagerEscrowRefundAfter).ToUnixTimeSeconds();
        // Durable NOW, before the bidder is handed anything to pay: the invoice id is the only link between
        // sats about to leave a wallet and the consent that justified them, and ReclaimAfter is the only
        // thing that can bring them back.
        await persistence.SaveHeroBidAsync(bid, ct);
        // THE CONSENT. Who agreed, to what, at what price, and which invoice that agreement created.
        await AuditAsync(Persistence.AuditEventType.BidAccepted, player.Id,
            [bid.Id, bid.HeroId, bid.BidderPlayerId],
            new
            {
                bidId = bid.Id, heroId = bid.HeroId, heroName = hero.Name,
                bidderPlayerId = bid.BidderPlayerId, ownerPlayerId = bid.OwnerPlayerId,
                bidSats = bid.BidSats, feeSats = fee, sellerNetSats = bid.BidSats - fee,
                bidInvoiceId = invoice.InvoiceId, reclaimAfterUnixSeconds = bid.ReclaimAfterUnixSeconds,
            },
            $"bid-accepted:{bid.Id}");
        return (bid, invoice);
    }

    /// <summary>
    /// What an accepted bid bills, plus whether it is FUNDED yet. Either party may read it; neither is
    /// billed by looking. The accept response lands in the OWNER's browser while the sats are the BIDDER's
    /// to send, so without this the side that owes the money has no way to learn what it is — and the owner
    /// has no way to learn it arrived, which is the fact they must not deliver the hero without.
    /// </summary>
    public async Task<(HeroBid Bid, FeeInvoice Invoice, bool Funded)> GetBidInvoiceAsync(
        Player player, string bidId, CancellationToken ct)
    {
        if (!store.HeroBids.TryGetValue(bidId, out var bid))
            throw new GameRuleException($"Unknown bid '{bidId}'.");
        if (bid.BidderPlayerId != player.Id && bid.OwnerPlayerId != player.Id)
            throw new GameRuleException("Only this bid's parties can see its invoice.");
        // Nothing is billed before consent, so there is nothing to show — and saying so plainly is the
        // point: an un-accepted bid has no price yet.
        if (!bid.Accepted)
            throw new GameRuleException("This hero's owner hasn't accepted the bid yet — nothing is billed until they do.");
        var invoice = await chain.GetFeeInvoiceAsync(bid.BidInvoiceId!, ct)
            ?? throw new GameRuleException("This bid's invoice is no longer available.");
        return (bid, invoice, await chain.IsInvoicePaidAsync(bid.BidInvoiceId!, ct));
    }

    /// <summary>The owner's refusal — terminal, and the counterpart to <see cref="AcceptBidAsync"/>. Nothing
    /// to unwind: a declined bid was never billed.</summary>
    public async Task<HeroBid> DeclineBidAsync(Player player, string bidId, CancellationToken ct)
    {
        if (!store.HeroBids.TryGetValue(bidId, out var bid))
            throw new GameRuleException($"Unknown bid '{bidId}'.");
        using var gate = await store.LockAsync($"bid:{bidId}", ct);
        if (bid.OwnerPlayerId != player.Id)
            throw new GameRuleException("Only this hero's owner can decline a bid on it.");
        if (bid.Settled) throw new GameRuleException("This bid is already settled.");
        if (bid.Withdrawn) throw new GameRuleException("This bid was withdrawn by its bidder.");
        // An acceptance has already created an invoice the bidder may have paid, and may have been
        // delivered against — it is not the owner's to take back unilaterally. The reclaim window is the
        // exit from an accepted bid, and it refunds rather than merely cancelling.
        if (bid.Accepted) throw new GameRuleException(
            "This bid is already accepted — it can only be settled, or unwound after its reclaim window.");
        bid.Declined = true;
        await persistence.SaveHeroBidAsync(bid, ct);
        await AuditAsync(Persistence.AuditEventType.BidDeclined, player.Id,
            [bid.Id, bid.HeroId, bid.BidderPlayerId],
            new
            {
                bidId = bid.Id, heroId = bid.HeroId, bidderPlayerId = bid.BidderPlayerId,
                ownerPlayerId = bid.OwnerPlayerId, bidSats = bid.BidSats,
            },
            $"bid-declined:{bid.Id}");
        return bid;
    }

    /// <summary>The bidder's retraction — the mirror of a decline, and shut for the mirror reason once the
    /// owner has consented. Nothing to unwind: an un-accepted bid was never billed.</summary>
    public async Task<HeroBid> WithdrawBidAsync(Player player, string bidId, CancellationToken ct)
    {
        if (!store.HeroBids.TryGetValue(bidId, out var bid))
            throw new GameRuleException($"Unknown bid '{bidId}'.");
        using var gate = await store.LockAsync($"bid:{bidId}", ct);
        if (bid.BidderPlayerId != player.Id)
            throw new GameRuleException("Only the bidder can withdraw this bid.");
        if (bid.Settled) throw new GameRuleException("This bid is already settled.");
        if (bid.Declined) throw new GameRuleException("This bid was already declined.");
        // The owner may have consented and shipped the hero on the strength of it. Yanking the offer from
        // under a delivery in flight is exactly what the reclaim window exists to replace.
        if (bid.Accepted) throw new GameRuleException(
            "This hero's owner has already accepted — pay and settle, or unwind after the reclaim window.");
        bid.Withdrawn = true;
        await persistence.SaveHeroBidAsync(bid, ct);
        await AuditAsync(Persistence.AuditEventType.BidWithdrawn, player.Id,
            [bid.Id, bid.HeroId, bid.OwnerPlayerId],
            new
            {
                bidId = bid.Id, heroId = bid.HeroId, bidderPlayerId = bid.BidderPlayerId,
                ownerPlayerId = bid.OwnerPlayerId, bidSats = bid.BidSats,
            },
            $"bid-withdrawn:{bid.Id}");
        return bid;
    }

    /// <summary>
    /// Closes an accepted, funded, DELIVERED bid: the owner is paid, and the hero's record follows the asset
    /// to the bidder. Callable by either party — both have every reason to want it run, and letting one side
    /// alone hold the trigger would let them hold the other's money or hero hostage by going quiet.
    /// </summary>
    public async Task<Hero> SettleBidAsync(Player player, string bidId, CancellationToken ct)
    {
        if (!store.HeroBids.TryGetValue(bidId, out var bid))
            throw new GameRuleException($"Unknown bid '{bidId}'.");
        if (bid.BidderPlayerId != player.Id && bid.OwnerPlayerId != player.Id)
            throw new GameRuleException("Only this bid's parties can settle it.");
        // Per-bid gate: consent-check → paid-check → payout → transfer → settled-set must be one atomic
        // step, or two concurrent settles of one bid both pass the guards and pay the owner twice. The SAME
        // key accept and refund take, so a settle can never interleave with the refund that would undo it.
        using var gate = await store.LockAsync($"bid:{bidId}", ct);

        // ── THE CONSENT GATE ──────────────────────────────────────────────
        // Everything below this line moves a hero out of someone's roster and sats out of the treasury.
        // None of it may happen on the bidder's say-so alone.
        if (!bid.Accepted)
            throw new GameRuleException("This hero's owner hasn't accepted this bid yet.");
        if (bid.Declined) throw new GameRuleException("This bid was declined.");
        if (bid.Withdrawn) throw new GameRuleException("This bid was withdrawn.");
        if (bid.Refunded) throw new GameRuleException("This bid was already unwound and the bidder refunded.");
        // One acceptance buys one hero: the latch is what stops a paid consent being replayed for a second
        // payout out of a treasury that cannot print.
        if (bid.Settled) throw new GameRuleException("This bid is already settled.");

        if (!await chain.IsInvoicePaidAsync(bid.BidInvoiceId!, ct))
            throw new GameRuleException("The bid invoice has not been paid yet — pay it from your wallet, then settle.");

        var hero = GetHero(bid.HeroId);
        // Non-custodial: the owner's own wallet performs the asset spend. We only verify the chain now shows
        // the BIDDER holding it — the same proof ClaimPurchasedHeroAsync and ConfirmTransferAsync demand,
        // and the only evidence that the owner actually honoured the bid.
        if (!await chain.VerifyHeroOwnershipAsync(bid.BidderPlayerId, hero.AssetId ?? hero.Id, ct))
            throw new GameRuleException(
                "The chain does not show the bidder holding this hero yet — the owner must send the hero asset from their wallet first.");

        // The bid is now in the treasury (a paid Receive invoice) — tally it, deduped by invoice id. It is
        // booked as income here and as an outflow again below: it really does land in the treasury before
        // it is forwarded, and a ledger that showed only the payout would read as a gift.
        await store.RecordInflowAsync(bid.BidInvoiceId!, "bid", bid.BidSats, ct);

        // The owner's proceeds are REAL BITCOIN owed to another player, so they move BEFORE the hero record
        // does and behind their own durable latch (the stud-fee pattern): consume the latch first, then pay.
        // Paying after the transfer would leave the one ordering where a fault strands the seller unpaid
        // with the hero already gone — and latching after the payout would let a crash in between pay twice.
        var proceeds = bid.BidSats - bid.FeeSats;
        if (proceeds > 0 && !bid.SellerPaid)
        {
            bid.SellerPaid = true;
            await persistence.SaveHeroBidAsync(bid, ct);
            try
            {
                await chain.PayoutAsync(bid.OwnerPlayerId, proceeds, $"bid:{bid.Id}", ct);
                await store.RecordOutflowAsync("bid", proceeds, ct);
            }
            catch
            {
                // A cleanly failed payout releases the latch IN MEMORY ONLY, so the settle can be retried in
                // this process. Never re-persist the release: if the payout actually settled before
                // throwing, the durable latch is the one thing keeping a restart from paying it twice.
                bid.SellerPaid = false;
                throw;
            }
        }

        // Item assets stay in the seller's wallet, so the loadout can't travel — the transfer rule.
        var sellerId = bid.OwnerPlayerId;
        var strippedGear = hero.Equipment.Slots.Values.ToList();
        foreach (var slot in hero.Equipment.Slots.Keys.ToList())
            hero.Equipment.Unequip(slot);
        hero.OwnerId = bid.BidderPlayerId;
        // TRANSFER is an identity event: the buyer's ownership goes durable now, not at the next flush.
        await persistence.SaveHeroAsync(hero, ct);
        bid.Settled = true;
        await persistence.SaveHeroBidAsync(bid, ct);

        // The same sale, kept as history, under the bid that settled it — the marketplace row a hero's page
        // reads. Both parties are known here (unlike a listing proven only by the covenant's treasury leg),
        // so the buyer is never null on a bid.
        var sale = new HeroSale(bid.Id, hero.Id, sellerId, bid.BidderPlayerId,
            bid.BidSats, bid.FeeSats, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        if (store.RecordHeroSale(sale) is { } toPersist)
        {
            try { await persistence.SaveHeroSaleAsync(toPersist, ct); }
            catch (Exception ex)
            {
                // History, not money: the sats have moved and the hero has changed hands durably above.
                // Losing this row costs a line on a page, so it is named and absorbed rather than thrown.
                logger?.LogWarning(ex, "Hero {HeroId} sold for {Sats} sat under bid {BidId}, but the sale "
                    + "row could not be persisted — its price will be missing from the hero's history.",
                    hero.Id, bid.BidSats, bid.Id);
            }
        }

        await AuditAsync(Persistence.AuditEventType.HeroTransferred, bid.BidderPlayerId,
            [hero.Id, sellerId, bid.Id],
            new
            {
                heroId = hero.Id, fromPlayerId = sellerId, toPlayerId = bid.BidderPlayerId,
                assetId = hero.AssetId, strippedGear, reason = "bid-settled", bidId = bid.Id,
                salePriceSats = bid.BidSats,
            });
        await AuditAsync(Persistence.AuditEventType.BidSettled, player.Id,
            [bid.Id, hero.Id, sellerId, bid.BidderPlayerId],
            new
            {
                bidId = bid.Id, heroId = hero.Id, heroName = hero.Name, sellerId,
                buyerId = bid.BidderPlayerId, bidSats = bid.BidSats, feeSats = bid.FeeSats,
                sellerNetSats = proceeds, bidInvoiceId = bid.BidInvoiceId, settledByPlayerId = player.Id,
            },
            $"bid-settled:{bid.Id}");
        return hero;
    }

    /// <summary>
    /// Unwinds an accepted bid the owner never honoured, and sends the bidder's sats home. The one exit an
    /// accepted bid has, and the reason a bidder is never out of pocket for a hero they did not receive.
    ///
    /// <para>Open to EITHER party, and only past the window. The bidder needs it to recover their money; the
    /// owner needs it to free a hero a funded bidder has gone quiet on. It is the same shape the stranded
    /// tournament refund uses — mark terminal durably FIRST, then pay, behind a once-only latch — because
    /// the failure it must not have is the same one: paying the same sats back twice.</para>
    /// </summary>
    public async Task<(HeroBid Bid, long RefundedSats)> RefundBidAsync(
        Player player, string bidId, CancellationToken ct)
    {
        if (!store.HeroBids.TryGetValue(bidId, out var bid))
            throw new GameRuleException($"Unknown bid '{bidId}'.");
        if (bid.BidderPlayerId != player.Id && bid.OwnerPlayerId != player.Id)
            throw new GameRuleException("Only this bid's parties can unwind it.");
        using var gate = await store.LockAsync($"bid:{bidId}", ct);

        if (!bid.Accepted)
            throw new GameRuleException("This bid was never accepted, so nothing was ever billed — withdraw or decline it instead.");
        if (bid.Settled) throw new GameRuleException("This bid is already settled.");
        if (bid.Refunded) throw new GameRuleException("This bid was already unwound.");
        // THE anti-double-spend gate. Once the owner's proceeds have left the treasury the sale is
        // committed and only a settle may finish it — refunding here would pay BOTH sides out of a treasury
        // that cannot print. A settle that faulted after the payout is resumable; this is not its exit.
        if (bid.SellerPaid)
            throw new GameRuleException("This bid's seller has already been paid — settle it rather than unwinding it.");

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (!Shared.ReclaimWindow.IsUnlocked(bid.ReclaimAfterUnixSeconds, now))
            throw new GameRuleException(
                $"This bid is still within its delivery window — it {Shared.ReclaimWindow.Describe(bid.ReclaimAfterUnixSeconds, now)}.");

        var paid = await chain.IsInvoicePaidAsync(bid.BidInvoiceId!, ct);
        if (paid)
        {
            // The owner DID deliver — the sale is real even though nobody ran the settle, so this is a
            // settle waiting to happen and refunding it would take a paid-for hero back off the bidder.
            // Checked only when funded: an unpaid bid has nothing to give back either way.
            var hero = store.Heroes.GetValueOrDefault(bid.HeroId);
            if (hero is not null
                && await chain.VerifyHeroOwnershipAsync(bid.BidderPlayerId, hero.AssetId ?? hero.Id, ct))
                throw new GameRuleException(
                    "The chain shows the bidder already holding this hero — settle the bid instead of unwinding it.");
        }

        // Terminal FIRST and durable, before a single sat moves: a crash mid-refund must not let a restart
        // pay it again (the stranded-bracket refund's ordering, for its reason).
        bid.Refunded = true;
        await persistence.SaveHeroBidAsync(bid, ct);

        long refunded = 0;
        if (paid && !bid.RefundPaid)
        {
            bid.RefundPaid = true;
            await persistence.SaveHeroBidAsync(bid, ct);
            try
            {
                await chain.PayoutAsync(bid.BidderPlayerId, bid.BidSats, $"bid-refund:{bid.Id}", ct);
                await store.RecordOutflowAsync("bid-refund", bid.BidSats, ct);
                refunded = bid.BidSats;
            }
            catch
            {
                // In-memory release only, exactly as in the settle's payout: a durable release would let a
                // restart refund sats that may already have gone out.
                bid.RefundPaid = false;
                throw;
            }
        }

        await AuditAsync(Persistence.AuditEventType.BidRefunded, player.Id,
            [bid.Id, bid.HeroId, bid.BidderPlayerId, bid.OwnerPlayerId],
            new
            {
                bidId = bid.Id, heroId = bid.HeroId, bidderPlayerId = bid.BidderPlayerId,
                ownerPlayerId = bid.OwnerPlayerId, bidSats = bid.BidSats, refundedSats = refunded,
                wasFunded = paid, bidInvoiceId = bid.BidInvoiceId, unwoundByPlayerId = player.Id,
                reclaimAfterUnixSeconds = bid.ReclaimAfterUnixSeconds,
            },
            $"bid-refunded:{bid.Id}");
        return (bid, refunded);
    }

    /// <summary>Every live bid, newest first — the discovery path a browser needs to SEE an incoming bid on
    /// its own hero. Public like /stud and /deathmatch; the client filters to itself. No invoice is exposed
    /// here, only the offer and its state.</summary>
    public IReadOnlyList<HeroBid> ListBids() =>
        store.HeroBids.Values.OrderByDescending(b => b.CreatedAt).ToList();
}
