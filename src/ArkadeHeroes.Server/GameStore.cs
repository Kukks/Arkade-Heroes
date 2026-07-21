using System.Collections.Concurrent;
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
}

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
    /// <summary>The treasury fee-invoice the seller pays to list; null when no listing fee is charged.</summary>
    public string? ListingFeeInvoiceId { get; init; }
    /// <summary>Flat listing fee charged to the treasury at open (0 when disabled).</summary>
    public long ListingFeeSats { get; init; }
    /// <summary>Latched once the listing fee is observed paid — an offer stays <c>pending</c> until then
    /// (true immediately when no fee is due, so a disabled fee is a no-op).</summary>
    public bool ListingFeePaid { get; set; }
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
    public string? Nonce { get; set; }
    public string? EntropyHex { get; set; }
}

/// <summary>In-process game state. v1 keeps everything in memory; the chain is the durable layer for heroes.</summary>
public class GameStore
{
    public ConcurrentDictionary<string, Player> Players { get; } = new();
    public ConcurrentDictionary<string, Player> PlayersByToken { get; } = new();
    public ConcurrentDictionary<string, Hero> Heroes { get; } = new();
    public ConcurrentDictionary<string, BreedingSession> Breedings { get; } = new();
    public ConcurrentDictionary<string, GauntletSession> Gauntlets { get; } = new();

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
    public readonly SemaphoreSlim SettleLock = new(1, 1);                        // serialize settlement
}
