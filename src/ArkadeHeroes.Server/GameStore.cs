using System.Collections.Concurrent;
using System.Threading;
using ArkadeHeroes.Core.Combat;
using ArkadeHeroes.Core.Heroes;

namespace ArkadeHeroes.Server;

public class Player
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Token { get; init; }
    public bool StarterClaimed { get; set; }
    /// <summary>The wallet's stable x-only login pubkey (hex), when registered — enables "sign in with your wallet" resume.</summary>
    public string? LoginPubKeyHex { get; set; }

    /// <summary>Daily-loop streak: consecutive UTC days claimed (0 = never). In-memory, like all player state.</summary>
    public int StreakCount { get; set; }
    /// <summary>The last day-index the player claimed the daily reward (null = never) — enforces once/day.</summary>
    public int? LastClaimDay { get; set; }

    /// <summary>The Terms-of-Use version this player explicitly accepted (null = never). Load-bearing:
    /// this game stakes real bitcoin and burns assets permanently, so the record of WHAT the player agreed
    /// to — and when — is the only evidence the disclosure was ever made.</summary>
    public int? TermsAcceptedVersion { get; set; }
    /// <summary>When that acceptance happened, in UTC.</summary>
    public DateTimeOffset? TermsAcceptedAtUtc { get; set; }
}

/// <summary>The first hero ever to express a named Fancy set, and who owned it at that moment — the prize
/// in the discovery race. Immutable once claimed.</summary>
public sealed record FancyDiscovery(string Title, string HeroId, string HeroName, string OwnerId, long UnixSeconds);

/// <summary>A hero's Fancy set and its edition number — "Sovereign #1" is the discoverer, "#7" the seventh
/// ever found. Assigned once, at the moment the hero enters the game, and never renumbered.</summary>
public sealed record FancyEdition(string Title, int Edition);

/// <summary>The complete stamped fact for one Fancy find — a hero's set, its edition, and who found it.
/// The union of what a <see cref="FancyDiscovery"/> and a <see cref="FancyEdition"/> each hold, and exactly
/// what durability persists so "first to breed this set, forever" survives a restart.</summary>
public sealed record FancyFind(string Title, string HeroId, string HeroName, string OwnerId, long UnixSeconds, int Edition);

/// <summary>A pending PvE gauntlet run (F1): the seed is committed at open; the run resolves once the
/// fee invoice is paid, awarding capped XP + a full-clear item, then rate-limits the hero.</summary>
public class GauntletSession
{
    public required string Id { get; init; }
    public required string PlayerId { get; init; }
    public required string HeroId { get; init; }
    public required byte[] ServerSeed { get; init; }
    public required string CommitmentHex { get; init; }
    public required string FeeInvoiceId { get; init; }
    public required long FeeSats { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool Completed { get; set; }
}

/// <summary>A pending endless-Trials run: the seed is committed at open; the run resolves on reveal (no fee,
/// no reward beyond a score + title), tracking the hero's personal best.</summary>
public class TrialsSession
{
    public required string Id { get; init; }
    public required string PlayerId { get; init; }
    public required string HeroId { get; init; }
    public required byte[] ServerSeed { get; init; }
    public required string CommitmentHex { get; init; }
    /// <summary>This week's ladder rule, PINNED when the run opens so the score and its replay agree even
    /// if the week rolls over before the run resolves (or before a client verifies it).</summary>
    public ArkadeHeroes.Core.Progression.TrialsAffix Affix { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool Completed { get; set; }
}

public class BreedingSession
{
    public required string Id { get; init; }
    public required string PlayerId { get; init; }
    public required string ParentAId { get; init; }
    public required string ParentBId { get; init; }
    public required byte[] ServerSeed { get; init; }
    public required string CommitmentHex { get; init; }
    /// <summary>"invoice" (fee-paid, treasury mint) or "covenant" (escrow deposit, emulator-enforced mint).</summary>
    public string Mode { get; init; } = "invoice";
    /// <summary>Fee invoice the player's own wallet must pay before reveal (invoice mode).</summary>
    public string? FeeInvoiceId { get; init; }
    /// <summary>Breed escrow address the player deposits both parents + fee into (covenant mode).</summary>
    public string? EscrowAddress { get; set; }
    /// <summary>The escalated breeding fee for this session (sats) — paid via the invoice or the breed escrow.</summary>
    public long FeeSats { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool Completed { get; set; }
    public string? ChildHeroId { get; set; }
}

/// <summary>A hero merge (fusion) awaiting its escrow deposit. Base + sacrifice + fee are deposited into the escrow; reveal retires the inputs and mints the fused hero.</summary>
public class MergeSession
{
    public required string Id { get; init; }
    public required string PlayerId { get; init; }
    public required string BaseId { get; init; }
    public required string SacrificeId { get; init; }
    public required byte[] ServerSeed { get; init; }
    public required string CommitmentHex { get; init; }
    /// <summary>"treasury" (rung 1, server-executed) or "covenant" (rung 2, emulator-enforced mint).</summary>
    public string Mode { get; init; } = "treasury";
    /// <summary>Merge escrow address the player deposits base + sacrifice + fee into.</summary>
    public string? EscrowAddress { get; set; }
    /// <summary>The flat merge fee for this session (sats) — paid via the merge escrow.</summary>
    public long FeeSats { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool Completed { get; set; }
    public string? FusedHeroId { get; set; }
}

/// <summary>A death-match awaiting both stakes. Each player deposits their hero into their own escrow; on settle the loser's hero is permanently burned and the winner keeps theirs.</summary>
public class DeathMatchSession
{
    public required string Id { get; init; }
    public required string ChallengerPlayerId { get; init; }
    public required string DefenderPlayerId { get; init; }
    public required string ChallengerHeroId { get; init; }
    public required string DefenderHeroId { get; init; }
    public required byte[] ServerSeed { get; init; }
    public required string CommitmentHex { get; init; }
    /// <summary>The ONE joint escrow both players stake their hero into (covenant-v2).</summary>
    public string? JointEscrowAddress { get; set; }
    /// <summary>Each side's equipped-item snapshot at OPEN — the gear units staked alongside the heroes.</summary>
    public IReadOnlyList<string> ChallengerGearItemIds { get; init; } = [];
    public IReadOnlyList<string> DefenderGearItemIds { get; init; } = [];
    /// <summary>Per-character death-match fee invoices (a level-scaled sats sink); BOTH must be paid before settle.</summary>
    public string? ChallengerFeeInvoiceId { get; set; }
    public string? DefenderFeeInvoiceId { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool Accepted { get; set; }
    public bool Completed { get; set; }
    public string? WinnerHeroId { get; set; }
    /// <summary>Opt-in absorb mode: on a seed-driven roll the winner re-mints absorbing the loser's traits (6-leaf escrow); a failed roll is the classic keep.</summary>
    public bool Absorb { get; init; }
    /// <summary>The species control asset the absorbed hero mints under (absorb mode only).</summary>
    public string SpeciesId { get; init; } = "";

    /// <summary>Fight-time replay data (persisted at settle) so ANY spectator can watch + verify the
    /// death-match later — mirrors MatchSession. Null until settled.</summary>
    public BattleResult? Result { get; set; }
    public string? Nonce { get; set; }
    public string? EntropyHex { get; set; }
    public Shared.HeroDto? ChallengerSnapshot { get; set; }
    public Shared.HeroDto? DefenderSnapshot { get; set; }
}

/// <summary>An item purchase awaiting its invoice payment. Status: pending → delivering → claimed (delivery failures return to pending so a paid purchase is always claimable).</summary>
public class ItemPurchase
{
    public required string InvoiceId { get; init; }
    public required string PlayerId { get; init; }
    public required string ItemId { get; init; }
    public string Status { get; set; } = "pending";
    public string? ItemAssetId { get; set; }
    public string? DeliveryTxId { get; set; }
    public object Gate { get; } = new();
}

public class MatchSession
{
    public required string Id { get; init; }
    public required string ChallengerPlayerId { get; init; }
    public required string ChallengerHeroId { get; init; }
    public required string DefenderHeroId { get; init; }
    public required byte[] ServerSeed { get; init; }
    public required string CommitmentHex { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Stake each side escrows with the treasury; 0 = friendly match.</summary>
    public long WagerSats { get; init; }

    /// <summary>"invoice" or "covenant" — how stakes are held and settled.</summary>
    public string Mode { get; init; } = "invoice";

    /// <summary>Covenant mode: PER-PARTY escrow addresses each player stakes into.</summary>
    public string? EscrowChallengerAddress { get; set; }
    public string? EscrowDefenderAddress { get; set; }

    /// <summary>Covenant mode: when each party's timelocked refund unlocks — also the abandonment threshold for expiring a stale match. Null in invoice mode.</summary>
    public long? RefundAfterUnixSeconds { get; set; }

    /// <summary>Stake invoices the players' own wallets must pay (invoice mode; null for friendly matches).</summary>
    public string? ChallengerInvoiceId { get; init; }
    public string? DefenderInvoiceId { get; set; }

    /// <summary>Per-character match-fee invoices (a level-proportional sats sink paid to the treasury); BOTH must be paid before a staked fight. Null for friendly matches.</summary>
    public string? ChallengerFeeInvoiceId { get; set; }
    public string? DefenderFeeInvoiceId { get; set; }

    /// <summary>Owner of the defender hero at open time (must accept wagered matches).</summary>
    public string? DefenderPlayerId { get; set; }

    public string Status { get; set; } = "open"; // open | accepted | resolved
    public BattleResult? Result { get; set; }
    public string? EntropyHex { get; set; }
    public string? Nonce { get; set; }

    /// <summary>Fight-time hero snapshots (level + equipment as actually fought) — persisted so ANY
    /// spectator can replay + verify the resolved match later, even after the heroes change. Null until resolved.</summary>
    public Shared.HeroDto? ChallengerSnapshot { get; set; }
    public Shared.HeroDto? DefenderSnapshot { get; set; }
}

/// <summary>
/// A resting item offer in the discovery index. The covenant lives on-chain (the
/// seller deposits the item into the offer address; a buyer fulfils it from their
/// own wallet); this record is the server's searchable listing, reconciled
/// against on-chain truth. Status: pending (created, item not yet deposited) →
/// active (funded, buyable) → closed (sold or reclaimed).
/// </summary>
public class OfferListing
{
    public required string Id { get; init; }
    public required string SellerId { get; init; }
    /// <summary>"item" (a fungible equipment unit) or "hero" (a unique character asset).</summary>
    public string Kind { get; init; } = "item";
    /// <summary>Game item id for item offers; empty for hero offers.</summary>
    public required string ItemId { get; init; }
    /// <summary>Game hero id for hero offers; null for item offers.</summary>
    public string? HeroId { get; init; }
    public required long AskSats { get; init; }
    public required string OfferAddress { get; init; }
    /// <summary>The on-chain asset resting in the offer — the item's shared asset, or the hero's own unique asset.</summary>
    public required string ItemAssetId { get; init; }
    public required long OfferValueSats { get; init; }
    public required long RefundAfterUnixSeconds { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string Status { get; set; } = "pending";
    /// <summary>The marketplace fee this offer's COVENANT routes to the treasury when it sells (0 when
    /// disabled). Nothing is billed at listing — the seller absorbs it out of the ask, so an offer that
    /// never sells costs nothing and a sale cannot skip the cut.</summary>
    public long ListingFeeSats { get; init; }
    /// <summary>Whether the asset is currently observed resting at the offer address, refreshed on each
    /// reconcile. Tracked apart from <see cref="Status"/> because a fee-gated offer stays <c>pending</c>
    /// even after its asset has LEFT the seller's wallet — so <c>pending</c> alone cannot answer "is the
    /// item still held?", which is what the free-to-sell check needs.</summary>
    public bool AssetDeposited { get; set; }
}

/// <summary>A pending/resolved 3v3 squad match: two 3-hero lineups sharing one wager escrow (reused from the
/// duel flow), resolved by a positional best-of-3 relay. Mirrors <see cref="MatchSession"/> with lineups.</summary>
public class SquadMatchSession
{
    public required string Id { get; init; }
    public required string ChallengerPlayerId { get; init; }
    public required IReadOnlyList<string> ChallengerLineup { get; init; }
    public required IReadOnlyList<string> DefenderLineup { get; init; }
    public required byte[] ServerSeed { get; init; }
    public required string CommitmentHex { get; init; }
    public long WagerSats { get; init; }
    public string Mode { get; init; } = "covenant";
    public string? EscrowChallengerAddress { get; set; }
    public string? EscrowDefenderAddress { get; set; }
    public string? ChallengerInvoiceId { get; set; }
    public string? ChallengerFeeInvoiceId { get; set; }
    public string? DefenderInvoiceId { get; set; }
    public string? DefenderFeeInvoiceId { get; set; }
    public long? RefundAfterUnixSeconds { get; set; }
    public string? DefenderPlayerId { get; set; }
    public string Status { get; set; } = "open";
    public SquadResult? Result { get; set; }
    public IReadOnlyList<Shared.HeroDto>? ChallengerSnapshots { get; set; }
    public IReadOnlyList<Shared.HeroDto>? DefenderSnapshots { get; set; }
    public string? Nonce { get; set; }
    public string? EntropyHex { get; set; }
}

/// <summary>A pending hero rename in the unique-name registry: the player pays the treasury fee, then
/// confirms to apply the claimed name. In-memory like the rest; a restart drops it with the fee marker.</summary>
public class RenameSession
{
    public required string HeroId { get; init; }
    public required string NewName { get; init; }
    /// <summary>The fee-invoice to pay; null when no rename fee is charged (then confirm applies immediately).</summary>
    public string? FeeInvoiceId { get; init; }
}

/// <summary>One entrant in a tournament bracket: the player, their hero, and the buy-in invoice they must pay.</summary>
public sealed class TournamentEntrant
{
    public required string PlayerId { get; init; }
    public required string HeroId { get; init; }
    public required string BuyInInvoiceId { get; init; }
}

/// <summary>A buy-in tournament bracket: entrants pay a buy-in into the treasury; once full it runs the pure
/// single-elimination resolver and pays the podium out of the pot minus the house rake. In-memory like the rest.</summary>
public sealed class TournamentSession
{
    public required string Id { get; init; }
    public required string OpenerPlayerId { get; init; }
    public required long BuyInSats { get; init; }
    public required int Size { get; init; }
    public required byte[] ServerSeed { get; init; }
    public required string CommitmentHex { get; init; }
    public List<TournamentEntrant> Entrants { get; } = new();
    public string Status { get; set; } = "open";   // open → full → resolved
    public ArkadeHeroes.Core.Combat.TournamentResult? Result { get; set; }
    /// <summary>Podium prizes paid at resolve (champion first) — surfaced for the Hall of Champions.</summary>
    public IReadOnlyList<long> Prizes { get; set; } = [];
    public string? Nonce { get; set; }
    public string? EntropyHex { get; set; }
    /// <summary>Entrant hero snapshots captured the moment the bracket FILLS — the locked fighting state
    /// the resolver runs over and any client re-runs (FairnessAudit.VerifyTournament), even after the
    /// heroes later change. NOT persisted: a restart-rehydrated bracket has none, can't honor its
    /// commitment, and lands in the strand refund instead.</summary>
    public IReadOnlyList<Shared.HeroDto>? EntrantSnapshots { get; set; }
    /// <summary>Domain-tagged hash of the canonical entrant set above, computed at the same fill instant
    /// (FairnessAudit.ComputeEntrantsCommitment) and published on the tournament DTO — so a client can pin
    /// the snapshots independently of the replay and a server can't substitute a genome/level/gear.</summary>
    public string? EntrantsCommitmentHex { get; set; }
}

/// <summary>In-process game state. v1 keeps everything in memory; the chain is the durable layer for heroes.</summary>
public class GameStore
{
    public ConcurrentDictionary<string, Player> Players { get; } = new();
    public ConcurrentDictionary<string, Player> PlayersByToken { get; } = new();
    public ConcurrentDictionary<string, Hero> Heroes { get; } = new();

    private long _heroesMinted;
    /// <summary>Cumulative heroes minted since this server started — every starter, bred, fused and absorbed
    /// hero passes through the one mint choke point. NOT persisted: a "since boot" churn counter, meant to be
    /// read as a RATE (delta over time) against the burn rate, not as a lifetime total.</summary>
    public long HeroesMinted => Interlocked.Read(ref _heroesMinted);
    public void RecordMint() => Interlocked.Increment(ref _heroesMinted);

    /// <summary>Burns counted DIRECTLY at each burn site, not inferred as (minted − supply).
    ///
    /// The subtraction was correct only while heroes were volatile, so a restart zeroed minted and supply
    /// together. Heroes are durable now, so after a restart minted starts at 0 against a surviving supply of
    /// N and the subtraction clamps to 0 — hiding every real burn until mints exceed the whole population.
    /// The mint half of the gauge kept counting, so the card read "mints, no burns": the alarm state, from
    /// a healthy game. Counting at the source keeps both halves on the same footing (deltas over one uptime)
    /// and drops the old "every hero removal is a burn" assumption — a future non-burn removal simply won't
    /// call this.</summary>
    public long HeroesBurned => Interlocked.Read(ref _heroesBurned);
    public void RecordBurn() => Interlocked.Increment(ref _heroesBurned);
    private long _heroesBurned;

    // ── Hero durability: the dirty set the periodic flush drains ──
    private readonly ConcurrentDictionary<string, byte> _dirtyHeroes = new();

    /// <summary>Marks a hero's PROGRESSION (level/XP, equipment, cooldowns, breed count) as changed since
    /// the last flush. Called at every progression mutation point; identity events (mint, burn, transfer,
    /// rename) persist inline instead and never pass through here. Cheap and idempotent — a hero mutated
    /// ten times between flushes is still one save.</summary>
    public void MarkHeroDirty(string heroId) => _dirtyHeroes.TryAdd(heroId, 0);

    /// <summary>Drains the dirty set for one flush pass. A mark that lands DURING the drain either makes
    /// this pass's list or stays in the set for the next one — never lost, at worst deferred one interval.</summary>
    public IReadOnlyList<string> DrainDirtyHeroes()
    {
        var drained = new List<string>();
        foreach (var heroId in _dirtyHeroes.Keys)
            if (_dirtyHeroes.TryRemove(heroId, out _))
                drained.Add(heroId);
        return drained;
    }

    public ConcurrentDictionary<string, BreedingSession> Breedings { get; } = new();
    // The Fancy discovery race: who FIRST bred a hero expressing each named Fancy set, plus how many have
    // ever been found. Pure bookkeeping — it never gates or changes an outcome, it just records the race.
    public ConcurrentDictionary<string, FancyDiscovery> FancyDiscoveries { get; } = new();
    public ConcurrentDictionary<string, int> FancyFindCount { get; } = new();
    /// <summary>heroId → the Fancy set it expresses and its EDITION: the Nth hero ever to express that set.
    /// Edition #1 is the discoverer; a low edition stays scarce however many turn up later.</summary>
    public ConcurrentDictionary<string, FancyEdition> FancyEditionByHero { get; } = new();

    /// <summary>Claim a Fancy set for its FIRST finder and stamp this hero's edition number. Called exactly
    /// once per hero (from the single point where a hero enters the store), and guarded so a repeat can't
    /// mint a second edition. TryAdd on the discovery means the first finder can never be displaced — later
    /// finds only take the next edition.</summary>
    /// <returns>The stamped fact if this hero was newly recorded, or <c>null</c> if it was already stamped
    /// (the re-number guard) — the caller persists a non-null result so the find survives a restart.</returns>
    public FancyFind? RecordFancyFind(string title, string heroId, string heroName, string ownerId, long unixSeconds)
    {
        if (FancyEditionByHero.ContainsKey(heroId)) return null;   // already stamped — never re-number a hero
        var edition = FancyFindCount.AddOrUpdate(title, 1, (_, n) => n + 1);
        FancyEditionByHero[heroId] = new FancyEdition(title, edition);
        FancyDiscoveries.TryAdd(title, new FancyDiscovery(title, heroId, heroName, ownerId, unixSeconds));
        return new FancyFind(title, heroId, heroName, ownerId, unixSeconds, edition);
    }

    /// <summary>Rehydrate one persisted Fancy find at boot. The edition is taken EXACTLY as stored (never
    /// re-derived), the per-title count advances so the next LIVE find takes the right number — without this
    /// a restart resets the count and the next hero of a set is minted as a second "#1" — and the edition-#1
    /// row restores the set's original discoverer. Idempotent per hero.</summary>
    public void LoadFancyFind(FancyFind find)
    {
        FancyEditionByHero[find.HeroId] = new FancyEdition(find.Title, find.Edition);
        FancyFindCount.AddOrUpdate(find.Title, find.Edition, (_, n) => Math.Max(n, find.Edition));
        if (find.Edition == 1)
            FancyDiscoveries[find.Title] = new FancyDiscovery(find.Title, find.HeroId, find.HeroName, find.OwnerId, find.UnixSeconds);
    }

    public ConcurrentDictionary<string, GauntletSession> Gauntlets { get; } = new();
    public ConcurrentDictionary<string, TrialsSession> Trials { get; } = new();
    /// <summary>Each hero's best endless-Trials waves-cleared to date — the personal-best leaderboard basis.</summary>
    public ConcurrentDictionary<string, int> TrialsBestByHero { get; } = new();

    public ConcurrentDictionary<string, MergeSession> Merges { get; } = new();

    public ConcurrentDictionary<string, DeathMatchSession> DeathMatches { get; } = new();
    public ConcurrentDictionary<string, MatchSession> Matches { get; } = new();
    public ConcurrentDictionary<string, SquadMatchSession> SquadMatches { get; } = new();
    public ConcurrentDictionary<string, ItemPurchase> ItemPurchases { get; } = new();
    public ConcurrentDictionary<string, OfferListing> Offers { get; } = new();
    public ConcurrentDictionary<string, RenameSession> Renames { get; } = new();
    public ConcurrentDictionary<string, TournamentSession> Tournaments { get; } = new();
    /// <summary>Serializes tournament join + resolve so a bracket can't be double-filled or double-paid.</summary>
    public SemaphoreSlim TournamentLock { get; } = new(1, 1);

    /// <summary>Outstanding single-use login nonces (hex) → issued time, for wallet-signature login.</summary>
    public ConcurrentDictionary<string, DateTimeOffset> LoginNonces { get; } = new();

    /// <summary>Server-side receipt cache, keyed by hero id (receipts are signed public facts; players hold their own copies).</summary>
    public ConcurrentDictionary<string, List<Shared.ProgressionReceiptDto>> ReceiptsByHero { get; } = new();

    // ── Season prize pool (in-memory, like the rest; a restart drops the marker + the winner-defining
    //    receipts together, which is what makes an in-memory settled-marker safe against double-pay) ──
    public int LastSettledSeason { get; set; }                                   // 0 = none settled yet
    public Shared.SeasonSettlementDto? LastSettlement { get; set; }              // snapshot of the most recent settled season
    public ConcurrentDictionary<int, long> SeasonFeeAccrual { get; } = new();    // seasonNumber → accrued sats

    // Economy telemetry: treasury OUTFLOW tallied by category ("daily"/"season"/"tournament"/"wager"/"squad").
    // Pure observability — recorded once per SUCCESSFUL payout; never gates or changes any behavior.
    public ConcurrentDictionary<string, long> TreasuryOutflowByTag { get; } = new();
    public void RecordOutflow(string tag, long sats) => TreasuryOutflowByTag.AddOrUpdate(tag, sats, (_, prev) => prev + sats);

    // Treasury INFLOW (fee captures) tallied by category. Deduped by invoice id, so a record call inside a
    // reconcile loop (e.g. the offer listing-fee latch) can never double-count. Pure observability.
    public ConcurrentDictionary<string, long> TreasuryInflowByTag { get; } = new();
    private readonly ConcurrentDictionary<string, byte> _talliedInflowInvoices = new();
    public void RecordInflow(string invoiceId, string tag, long sats)
    {
        if (_talliedInflowInvoices.TryAdd(invoiceId, 0))
            TreasuryInflowByTag.AddOrUpdate(tag, sats, (_, prev) => prev + sats);
    }

    public readonly SemaphoreSlim SettleLock = new(1, 1);                        // serialize settlement

    // ── Per-key async mutexes: the money-path once-only guards ──
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _keyedLocks = new();

    /// <summary>
    /// Serializes one keyed flow (a player's daily/starter claim, one session's reveal/resolve):
    /// guard-check → chain effect → guard-set must be one atomic step, and the chain calls await,
    /// so this is the awaitable analogue of <see cref="ItemPurchase.Gate"/>. Dispose the returned
    /// handle to release. Semaphores accrue one per key and are never removed — keys are player and
    /// session ids, which this in-memory store already retains for the process lifetime anyway.
    /// </summary>
    public async Task<IDisposable> LockAsync(string key, CancellationToken ct = default)
    {
        var gate = _keyedLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        return new KeyedLockReleaser(gate);
    }

    private sealed class KeyedLockReleaser(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;
        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}
