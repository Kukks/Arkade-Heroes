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
    /// Outputs the merge settle covenant sweeps for input-burn absence (fused→0, fee→1,
    /// + the appended extension). Over-sweeps safely: <c>0xef</c> returns "absent" for a
    /// non-existent output, so a generous sweep never false-rejects. Mirrors the
    /// death-match <c>SettleOutputSweep</c>.
    /// </summary>
    public const int MergeOutputSweep = 4;

    /// <summary>
    /// The merge escrow (covenant-v2): one contract with a structural <c>merge</c> leaf
    /// (<see cref="ArkadeCovenants.MergeAuthorized"/> — the shared breed gate PLUS base +
    /// sacrifice provably BURNED and the fused hero provably MINTED TO THE PLAYER, no packet
    /// trust) and a timelocked introspection-bound <c>refund</c> leaf that routes BOTH
    /// deposited heroes home to the player at output 0 (one output, two assets — two
    /// player-paying outputs would be coalesced; script-pinned so a reclaim cannot reroute;
    /// the fee returns as change). The player deposits base + sacrifice plus
    /// the fee here; the server assembles the covenant mint, and the covenant — not the
    /// packet — enforces that both inputs are destroyed and the fused hero reaches the player.
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
                new("merge", ArkadeCovenants.MergeAuthorized(
                    species, baseAsset, sacrificeAsset, oraclePk, treasuryScript, parameters.FeeSats,
                    playerScript, MergeOutputSweep)),
                new("refund",
                    [
                        // BOTH heroes home to the player at output 0 (one output, two assets —
                        // two player-paying outputs would be coalesced by the builder).
                        .. ArkadeCovenants.AssetAtOutput(0, baseAsset, playerScript),
                        .. ArkadeCovenants.AssetAtOutput(0, sacrificeAsset, playerScript),
                        0x51, // OP_1
                    ],
                    refundLockTime),
            ]);
    }
}
