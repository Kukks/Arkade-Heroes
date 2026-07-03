namespace ArkadeHeroes.Chain;

public record ChainInfo(
    string Mode,
    string Network,
    string TreasuryAddress,
    string? SpeciesAssetId,
    /// <summary>Covenant co-signer (Arkade Script emulator) key, when the service is reachable.</summary>
    string? EmulatorSignerKey = null);

/// <summary>
/// A fee/stake invoice: a fresh treasury address unique to one game action, so
/// a client payment to it is unambiguously bound to that action. The client's
/// own wallet pays it; the server only observes.
/// </summary>
public record FeeInvoice(string InvoiceId, string PayToAddress, long AmountSats, string Memo);

/// <summary>Genesis data committed into a hero asset's metadata at mint time.</summary>
public record HeroMintData(
    string GenomeHex,
    int Generation,
    string? ParentAId,
    string? ParentBId,
    string? ServerSeedHex,
    string? PlayerNonce)
{
    public Dictionary<string, string> ToMetadata()
    {
        var metadata = new Dictionary<string, string>
        {
            ["genome"] = GenomeHex,
            ["generation"] = Generation.ToString(),
        };
        if (ParentAId is not null) metadata["parentA"] = ParentAId;
        if (ParentBId is not null) metadata["parentB"] = ParentBId;
        if (ServerSeedHex is not null) metadata["serverSeed"] = ServerSeedHex;
        if (PlayerNonce is not null) metadata["nonce"] = PlayerNonce;
        return metadata;
    }
}

public record HeroMintResult(string AssetId, string ArkTxId);

public record ItemDeliveryResult(string ItemAssetId, string ArkTxId);

/// <summary>
/// The game's view of Arkade under the non-custodial mandate: players are
/// known to the server ONLY as Arkade addresses they registered; the server's
/// treasury signs its own outputs (mints, deliveries, payouts) and verifies
/// player-side actions (fee payments, transfers) by observing the chain. The
/// server never holds or moves player keys or funds.
/// </summary>
public interface IChainService
{
    Task<ChainInfo> GetInfoAsync(CancellationToken ct = default);

    // ── Players = addresses ────────────────────────────────────────────

    /// <summary>Binds a player id to the Arkade address their own wallet controls.</summary>
    Task RegisterPlayerAddressAsync(string playerId, string arkadeAddress, CancellationToken ct = default);

    /// <summary>The player's registered Arkade address.</summary>
    Task<string> GetPlayerAddressAsync(string playerId, CancellationToken ct = default);

    /// <summary>Read-only convenience: on-chain sats currently sitting at the player's registered address (public data).</summary>
    Task<long> GetAddressBalanceSatsAsync(string playerId, CancellationToken ct = default);

    // ── Fees: client pays, server observes ─────────────────────────────

    /// <summary>Creates an invoice at a fresh treasury address for one game action.</summary>
    Task<FeeInvoice> CreateFeeInvoiceAsync(string memo, long amountSats, CancellationToken ct = default);

    /// <summary>True once the invoice address has received at least the invoiced amount.</summary>
    Task<bool> IsInvoicePaidAsync(string invoiceId, CancellationToken ct = default);

    // ── Treasury-signed actions ────────────────────────────────────────

    /// <summary>
    /// Mints a hero as an Arkade asset (amount 1, genome in genesis metadata,
    /// control = species asset) delivered to the player's registered address.
    /// </summary>
    Task<HeroMintResult> MintHeroAssetAsync(string toPlayerId, HeroMintData data, CancellationToken ct = default);

    /// <summary>Delivers one unit of an item's fungible asset (lazily issued, species-controlled) to the player's registered address.</summary>
    Task<ItemDeliveryResult> DeliverItemAssetAsync(string toPlayerId, string itemId, string itemName, CancellationToken ct = default);

    /// <summary>Pays out from the treasury to the player's registered address (wager winnings). Returns a payment reference.</summary>
    Task<string> PayoutAsync(string toPlayerId, long amountSats, string memo, CancellationToken ct = default);

    // ── On-chain reads (never DB trust) ────────────────────────────────

    /// <summary>True if the player's registered address currently holds the hero asset.</summary>
    Task<bool> VerifyHeroOwnershipAsync(string playerId, string assetId, CancellationToken ct = default);

    /// <summary>Units of the item's asset currently held at the player's registered address.</summary>
    Task<ulong> GetItemAssetBalanceAsync(string playerId, string itemId, CancellationToken ct = default);
}
