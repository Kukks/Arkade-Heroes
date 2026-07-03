namespace ArkadeHeroes.Chain;

public record ChainInfo(
    string Mode,
    string Network,
    string TreasuryAddress,
    string? SpeciesAssetId);

public record PlayerWallet(string PlayerId, string ArkadeAddress);

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

/// <summary>
/// The game's view of Arkade. <c>InMemoryChainService</c> simulates it for unit
/// tests and offline dev; <c>NArkChainService</c> talks to a real Arkade
/// operator (regtest denigiri stack) via the NArk SDK.
/// </summary>
public interface IChainService
{
    Task<ChainInfo> GetInfoAsync(CancellationToken ct = default);

    /// <summary>Creates (or returns) the player's wallet and Arkade address.</summary>
    Task<PlayerWallet> GetOrCreatePlayerWalletAsync(string playerId, CancellationToken ct = default);

    Task<long> GetBalanceSatsAsync(string playerId, CancellationToken ct = default);

    /// <summary>
    /// Mints a hero as an Arkade asset (amount 1, genome in genesis metadata,
    /// control = species asset) delivered to the player's address.
    /// </summary>
    Task<HeroMintResult> MintHeroAssetAsync(string playerId, HeroMintData data, CancellationToken ct = default);

    /// <summary>Pays a game fee (breeding, item purchase) from the player to the treasury. Returns a payment reference.</summary>
    Task<string> PayFeeAsync(string playerId, long amountSats, string memo, CancellationToken ct = default);

    /// <summary>True if the player's wallet currently holds the hero asset.</summary>
    Task<bool> VerifyHeroOwnershipAsync(string playerId, string assetId, CancellationToken ct = default);
}
