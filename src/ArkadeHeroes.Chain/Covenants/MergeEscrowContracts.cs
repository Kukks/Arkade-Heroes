using NArk.Abstractions;
using NArk.Core.Assets;
using NBitcoin;
using NBitcoin.Scripting;

namespace ArkadeHeroes.Chain.Covenants;

/// <summary>
/// Everything needed to rebuild a merge (fusion) escrow contract from scratch:
/// the two INPUT hero asset ids (base + sacrifice), the species control asset,
/// the fee (and its treasury destination), the merge oracle key, and the refund
/// expiry. Public parameters — the escrow address commits to them — persisted
/// per merge (KV <c>merge-escrow:{mergeId}</c>) and served at
/// <c>/api/merges/{id}/escrow</c> so a PLAYER can rebuild the contract and
/// reclaim an abandoned deposit without trusting the server.
///
/// Note: the covenant gate is <see cref="ArkadeCovenants.BreedAuthorized"/> —
/// byte-identical to breeding's (both inputs present, one hero issued under the
/// species, fee to the treasury, oracle sig over the metadata root). Merge
/// differs only in EXECUTION (the packet BURNS the inputs — declared with no
/// output — instead of retaining them to the player), which the covenant — like
/// breed's parent retention — leaves to the tx-builder.
/// </summary>
public sealed record MergeEscrowParams(
    string PlayerAddress,
    string BaseId,
    string SacrificeId,
    string SpeciesId,
    string TreasuryFeeAddress,
    long FeeSats,
    long EscrowSats,
    string OraclePkHex,
    string MergeId,
    long RefundAfterUnixSeconds);

/// <summary>
/// The canonical construction of a merge escrow contract — shared by the server
/// (escrow creation, funding checks, covenant mint assembly) and the client
/// (refund reclaim), so both derive a byte-identical taptree and address from
/// the same <see cref="MergeEscrowParams"/>.
/// </summary>
public static class MergeEscrowContracts
{
    /// <summary>
    /// The merge escrow: one contract with a <c>merge</c> leaf (the full
    /// <see cref="ArkadeCovenants.BreedAuthorized"/> gate — both inputs present,
    /// fused hero controlled by the species, fee paid to the treasury, oracle
    /// sig over the fused hero's metadata root) and a timelocked <c>refund</c>
    /// leaf paying the escrow value back to the player after expiry (the inputs
    /// ride the refund output home; task 17/18 machinery reused). The player
    /// deposits base + sacrifice plus the fee here; the server assembles the
    /// covenant mint, BURNING both inputs.
    /// </summary>
    public static ArkadeArtifactContract Build(
        MergeEscrowParams parameters, OutputDescriptor operatorKey, string emulatorSignerKeyHex)
    {
        var playerScript = ArkAddress.Parse(parameters.PlayerAddress).ScriptPubKey;
        var treasuryScript = ArkAddress.Parse(parameters.TreasuryFeeAddress).ScriptPubKey;
        var oraclePk = Convert.FromHexString(parameters.OraclePkHex);
        var species = AssetId.FromString(parameters.SpeciesId);
        var baseAsset = AssetId.FromString(parameters.BaseId);
        var sacrificeAsset = AssetId.FromString(parameters.SacrificeId);

        var refundLockTime = new LockTime((uint)parameters.RefundAfterUnixSeconds);
        return new ArkadeArtifactContract(
            "merge-escrow", operatorKey, emulatorSignerKeyHex,
            [
                new("merge", ArkadeCovenants.BreedAuthorized(
                    species, baseAsset, sacrificeAsset, oraclePk, treasuryScript, parameters.FeeSats)),
                new("refund", ArkadeCovenants.RefundTo(playerScript, parameters.EscrowSats), refundLockTime),
            ]);
    }
}
