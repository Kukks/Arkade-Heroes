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
/// A per-match covenant escrow: PER-PARTY stake addresses (coinflip's shape).
/// Each party stakes into their own escrow VTXO; the settle branches sweep
/// both atomically, and each escrow carries a timelocked refund leaf paying
/// ONLY its own party — liveness without the server.
/// </summary>
public record WagerEscrowInfo(
    string MatchId,
    string ChallengerEscrowAddress,
    string DefenderEscrowAddress,
    long StakeSats,
    long PotSats,
    long RefundAfterUnixSeconds);

/// <summary>Which side of a covenant match's per-party escrows currently holds its stake on-chain — for detecting a refunded/abandoned match.</summary>
public record WagerEscrowFunding(bool ChallengerFunded, bool DefenderFunded);

/// <summary>The address a player deposits both parents plus the fee into for a covenant breed, and the refund window.</summary>
public record BreedEscrowInfo(
    string BreedingId,
    string EscrowAddress,
    long FeeSats,
    long RefundAfterUnixSeconds);

/// <summary>
/// A resting item offer: the address the seller deposits one item unit (plus
/// carrier dust) into, and the ask ANYONE may pay to take it. The seller is
/// pinned as the covenant's sole payee; the buyer funds the ask from their own
/// wallet. Recoverable by the seller after <see cref="RefundAfterUnixSeconds"/>.
/// </summary>
public record OfferInfo(
    string OfferId,
    string OfferAddress,
    string ItemAssetId,
    long AskSats,
    long OfferValueSats,
    long RefundAfterUnixSeconds);

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

    // ── Covenant wager escrows (emulator-enforced settlement) ──────────

    /// <summary>
    /// Builds the per-match escrow covenant (settle branches bound to the
    /// match's seed commitment and both players' registered addresses) and
    /// returns the address both players stake into from their own wallets.
    /// </summary>
    Task<WagerEscrowInfo> CreateWagerEscrowAsync(
        string matchId, string challengerPlayerId, string defenderPlayerId,
        long stakeSats, byte[] seedCommitment32, string oraclePubKeyHex,
        long refundAfterUnixSeconds, CancellationToken ct = default);

    /// <summary>True once BOTH exact-stake VTXOs sit at the escrow address.</summary>
    Task<bool> IsEscrowFundedAsync(string matchId, CancellationToken ct = default);

    /// <summary>Per-party funding of a covenant match's escrows — which side's stake currently sits on-chain. Null for invoice-mode or unknown matches.</summary>
    Task<WagerEscrowFunding?> GetWagerEscrowFundingAsync(string matchId, CancellationToken ct = default);

    /// <summary>
    /// The public escrow parameters persisted for a covenant match — everything
    /// a PLAYER needs to rebuild the per-party contracts independently (via
    /// <see cref="Covenants.WagerEscrowContracts.Build"/>) and reclaim a
    /// timelocked refund without trusting the server. Null when the match has
    /// no covenant escrow (invoice mode or unknown match).
    /// </summary>
    Task<Covenants.WagerEscrowParams?> GetWagerEscrowParamsAsync(string matchId, CancellationToken ct = default);

    // ── Covenant breeding escrows ──────────────────────────────────────

    /// <summary>
    /// Builds the breed escrow covenant (parents pinned, species control, fee,
    /// oracle key, timelocked refund) and returns the address the player
    /// deposits BOTH parents plus the fee into from their own wallet.
    /// </summary>
    Task<BreedEscrowInfo> CreateBreedEscrowAsync(
        string breedingId, string playerId, string parentAAssetId, string parentBAssetId,
        long feeSats, string oraclePubKeyHex, long refundAfterUnixSeconds, CancellationToken ct = default);

    /// <summary>True once both parents and the fee sit at the breed escrow address.</summary>
    Task<bool> IsBreedEscrowFundedAsync(string breedingId, CancellationToken ct = default);

    /// <summary>
    /// Executes the breeding through the covenant: the server assembles the
    /// mint (parents retained to the player, child issued under the species
    /// with <paramref name="childData"/>'s metadata, fee to the treasury) and
    /// presents the oracle's BIP340 signature over the child's metadata root.
    /// Returns the child's asset id. The covenant makes any other shape
    /// unsignable.
    /// </summary>
    Task<HeroMintResult> ExecuteBreedCovenantAsync(
        string breedingId, HeroMintData childData, byte[] oracleSignature64, CancellationToken ct = default);

    /// <summary>The public breed-escrow parameters for trustless client rebuild + refund. Null when unknown/invoice-mode.</summary>
    Task<Covenants.BreedEscrowParams?> GetBreedEscrowParamsAsync(string breedingId, CancellationToken ct = default);

    // ── Covenant merge / fusion escrows ────────────────────────────────

    /// <summary>
    /// Builds the merge escrow and returns the address the player deposits
    /// BOTH input heroes (base + sacrifice) plus the fee into from their own
    /// wallet. Rung 1 is treasury-executed; rung 2 makes the mint covenant-enforced.
    /// </summary>
    Task<string> CreateMergeEscrowAsync(
        string mergeId, string playerId, string baseAssetId, string sacrificeAssetId,
        long feeSats, string oraclePubKeyHex, long refundAfterUnixSeconds, CancellationToken ct = default);

    /// <summary>True once both input heroes and the fee sit at the merge escrow address.</summary>
    Task<bool> IsMergeEscrowFundedAsync(string mergeId, CancellationToken ct = default);

    /// <summary>
    /// Executes the merge: the two input heroes are BURNED (declared in the asset
    /// packet with no output — a true sink) and the fused hero is issued under the
    /// species with <paramref name="fusedData"/>'s metadata, gated on the oracle's
    /// BIP340 signature over the fused-genome metadata root. Returns the fused asset id.
    /// </summary>
    Task<HeroMintResult> ExecuteMergeAsync(
        string mergeId, HeroMintData fusedData, byte[] oracleSignature64, CancellationToken ct = default);

    /// <summary>The public merge-escrow parameters for trustless client rebuild + refund. Null when unknown.</summary>
    Task<Covenants.MergeEscrowParams?> GetMergeEscrowParamsAsync(string mergeId, CancellationToken ct = default);

    // ── Covenant item offers (resting, buyer-fulfilled) ────────────────

    /// <summary>
    /// Builds the resting-offer covenant for one unit of a game item's asset
    /// (seller pinned as payee, ask enforced, timelocked reclaim to the seller)
    /// and returns the address the seller deposits the item + carrier dust into
    /// from their own wallet. ANYONE may then fulfil it by paying the seller the
    /// ask in the same transaction — the emulator refuses underpayment.
    /// </summary>
    Task<OfferInfo> CreateOfferAsync(
        string offerId, string sellerPlayerId, string itemId, long askSats,
        long refundAfterUnixSeconds, CancellationToken ct = default);

    /// <summary>
    /// Builds a resting-offer covenant for a specific HERO asset (a unique
    /// supply-1 asset, rather than a fungible item). The offer covenant is
    /// asset-agnostic, so this shares the fulfil/reclaim machinery with item
    /// offers — only the asset id (the hero's own mint asset) differs.
    /// </summary>
    Task<OfferInfo> CreateHeroOfferAsync(
        string offerId, string sellerPlayerId, string heroAssetId, long askSats,
        long refundAfterUnixSeconds, CancellationToken ct = default);

    /// <summary>True once the offered asset (plus carrier dust) sits at the offer address.</summary>
    Task<bool> IsOfferFundedAsync(string offerId, CancellationToken ct = default);

    /// <summary>
    /// The public offer parameters for trustless client rebuild — everything a
    /// BUYER needs to reconstruct the offer covenant (via
    /// <see cref="Covenants.OfferContracts.Build"/>), verify the address matches
    /// the listing, and fulfil it, or the SELLER needs to reclaim after expiry.
    /// Null when the offer is unknown.
    /// </summary>
    Task<Covenants.OfferParams?> GetOfferParamsAsync(string offerId, CancellationToken ct = default);

    /// <summary>
    /// Settles the escrow through the covenant: presents the oracle's BIP340
    /// signature over the winning branch's settle message, reveals the seed,
    /// and sweeps both stakes atomically to the winner via the emulator.
    /// </summary>
    Task<string> SettleWagerEscrowAsync(
        string matchId, bool challengerWon, byte[] serverSeed, byte[] oracleSignature64,
        CancellationToken ct = default);

    // ── On-chain reads (never DB trust) ────────────────────────────────

    /// <summary>True if the player's registered address currently holds the hero asset.</summary>
    Task<bool> VerifyHeroOwnershipAsync(string playerId, string assetId, CancellationToken ct = default);

    /// <summary>Units of the item's asset currently held at the player's registered address.</summary>
    Task<ulong> GetItemAssetBalanceAsync(string playerId, string itemId, CancellationToken ct = default);
}
