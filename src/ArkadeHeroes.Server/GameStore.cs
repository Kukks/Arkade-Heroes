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
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool Completed { get; set; }
    public string? ChildHeroId { get; set; }
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

    /// <summary>Stake invoices the players' own wallets must pay (invoice mode; null for friendly matches).</summary>
    public string? ChallengerInvoiceId { get; init; }
    public string? DefenderInvoiceId { get; set; }

    /// <summary>Owner of the defender hero at open time (must accept wagered matches).</summary>
    public string? DefenderPlayerId { get; set; }

    public string Status { get; set; } = "open"; // open | accepted | resolved
    public BattleResult? Result { get; set; }
    public string? EntropyHex { get; set; }
    public string? Nonce { get; set; }
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
    public required string ItemId { get; init; }
    public required long AskSats { get; init; }
    public required string OfferAddress { get; init; }
    public required string ItemAssetId { get; init; }
    public required long OfferValueSats { get; init; }
    public required long RefundAfterUnixSeconds { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string Status { get; set; } = "pending";
}

/// <summary>In-process game state. v1 keeps everything in memory; the chain is the durable layer for heroes.</summary>
public class GameStore
{
    public ConcurrentDictionary<string, Player> Players { get; } = new();
    public ConcurrentDictionary<string, Player> PlayersByToken { get; } = new();
    public ConcurrentDictionary<string, Hero> Heroes { get; } = new();
    public ConcurrentDictionary<string, BreedingSession> Breedings { get; } = new();
    public ConcurrentDictionary<string, MatchSession> Matches { get; } = new();
    public ConcurrentDictionary<string, ItemPurchase> ItemPurchases { get; } = new();
    public ConcurrentDictionary<string, OfferListing> Offers { get; } = new();

    /// <summary>Server-side receipt cache, keyed by hero id (receipts are signed public facts; players hold their own copies).</summary>
    public ConcurrentDictionary<string, List<Shared.ProgressionReceiptDto>> ReceiptsByHero { get; } = new();
}
