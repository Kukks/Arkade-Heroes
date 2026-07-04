using NArk.Abstractions;
using NArk.Core.Assets;
using NBitcoin;
using NBitcoin.Scripting;

namespace ArkadeHeroes.Chain.Covenants;

/// <summary>
/// Everything needed to rebuild a breeding's escrow contract from scratch: the
/// two parent asset ids, the species control asset, the fee (and its treasury
/// destination), the breeding oracle key, and the refund expiry. Public
/// parameters — the escrow address commits to them — persisted per breeding
/// (KV <c>breed-escrow:{breedingId}</c>) and served at
/// <c>/api/breedings/{id}/escrow</c> so a PLAYER can rebuild the contract and
/// reclaim an abandoned deposit without trusting the server.
/// </summary>
public sealed record BreedEscrowParams(
    string PlayerAddress,
    string ParentAId,
    string ParentBId,
    string SpeciesId,
    string TreasuryFeeAddress,
    long FeeSats,
    string OraclePkHex,
    string BreedingId,
    long RefundAfterUnixSeconds);

/// <summary>
/// The canonical construction of a breeding escrow contract — shared by the
/// game server (escrow creation, funding checks, covenant mint assembly) and
/// the client (refund reclaim), so both derive a byte-identical taptree and
/// address from the same <see cref="BreedEscrowParams"/>.
/// </summary>
public static class BreedEscrowContracts
{
    /// <summary>
    /// The breed escrow: one contract with a <c>breed</c> leaf (the full
    /// <see cref="ArkadeCovenants.BreedAuthorized"/> gate — parents retained,
    /// child controlled by the species, fee paid to the treasury, oracle sig
    /// over the child's metadata root) and a timelocked <c>refund</c> leaf
    /// paying the escrow value back to the player after expiry (the parents
    /// ride the output; task 17/18 machinery reused). The player deposits both
    /// parents plus the fee here; the server assembles the covenant mint.
    /// </summary>
    public static ArkadeArtifactContract Build(
        BreedEscrowParams parameters, OutputDescriptor operatorKey, string emulatorSignerKeyHex, long escrowSats)
    {
        var playerScript = ArkAddress.Parse(parameters.PlayerAddress).ScriptPubKey;
        var treasuryScript = ArkAddress.Parse(parameters.TreasuryFeeAddress).ScriptPubKey;
        var oraclePk = Convert.FromHexString(parameters.OraclePkHex);
        var species = AssetId.FromString(parameters.SpeciesId);
        var parentA = AssetId.FromString(parameters.ParentAId);
        var parentB = AssetId.FromString(parameters.ParentBId);

        var refundLockTime = new LockTime((uint)parameters.RefundAfterUnixSeconds);
        return new ArkadeArtifactContract(
            "breed-escrow", operatorKey, emulatorSignerKeyHex,
            [
                new("breed", ArkadeCovenants.BreedAuthorized(
                    species, parentA, parentB, oraclePk, treasuryScript, parameters.FeeSats)),
                new("refund", ArkadeCovenants.RefundTo(playerScript, escrowSats), refundLockTime),
            ]);
    }

    /// <summary>
    /// The child hero's asset metadata in a FIXED order — the exact list the
    /// covenant mint puts in the child group AND the list whose Merkle root the
    /// breeding oracle signs. Order is deterministic (not a Dictionary) so the
    /// signed root and the on-chain group are byte-identical. Keys mirror
    /// <c>HeroMintData.ToMetadata()</c> so <c>FairnessAudit</c> recompute holds.
    /// </summary>
    public static List<AssetMetadata> ChildMetadata(
        string genomeHex, int generation, string parentAId, string parentBId,
        string serverSeedHex, string playerNonce) =>
    [
        AssetMetadata.Create("game", "arkade-heroes"),
        AssetMetadata.Create("genome", genomeHex),
        AssetMetadata.Create("generation", generation.ToString()),
        AssetMetadata.Create("parentA", parentAId),
        AssetMetadata.Create("parentB", parentBId),
        AssetMetadata.Create("serverSeed", serverSeedHex),
        AssetMetadata.Create("nonce", playerNonce),
    ];
}
