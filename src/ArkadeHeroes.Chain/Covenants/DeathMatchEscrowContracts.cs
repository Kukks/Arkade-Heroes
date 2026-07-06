using NArk.Abstractions;
using NBitcoin;
using NBitcoin.Scripting;

namespace ArkadeHeroes.Chain.Covenants;

/// <summary>
/// Public rebuildable params for the JOINT death-match escrow (covenant-v2): ONE
/// contract, staked by BOTH players, whose settle branches STRUCTURALLY enforce
/// the asset consequences (winner's hero to the winner, loser's hero burned) via
/// output-asset introspection — the oracle attests ONLY the winning branch, never
/// the routing. Both hero asset ids, both stakers' addresses, the commitment, the
/// oracle key, the shared carrier value, and the refund expiry are baked at open.
/// Persisted under <c>deathmatch-escrow:{id}</c> and served so either player can
/// rebuild the contract and reclaim their OWN hero after expiry without the server.
/// </summary>
public sealed record DeathMatchJointEscrowParams(
    string ChallengerAddress,
    string ChallengerHeroAssetId,
    string DefenderAddress,
    string DefenderHeroAssetId,
    string CommitmentHex,
    string OraclePkHex,
    string DeathMatchId,
    long EscrowSats,
    long RefundAfterUnixSeconds);

/// <summary>
/// The canonical construction of the JOINT death-match escrow contract — shared by
/// the server (escrow creation, funding checks, settlement) and the client (refund
/// reclaim), so both derive a byte-identical taptree + address from the same
/// <see cref="DeathMatchJointEscrowParams"/>. Mirrors <see cref="WagerEscrowContracts"/>.
/// </summary>
public static class DeathMatchEscrowContracts
{
    /// <summary>
    /// How many outputs the settle covenant sweeps for the loser-hero absence check
    /// (<see cref="ArkadeCovenants.AssetBurned"/>). The settle tx has 2 real outputs
    /// (winner + the appended extension); this generously over-sweeps so a cheat
    /// cannot route the loser past the checked range. Safe: <c>0xef</c> returns
    /// "absent" for a non-existent output, so over-sweeping never false-rejects.
    /// Proven live by <c>CovenantStructuralBurnProbe</c> (Rung 1).
    /// </summary>
    public const int SettleOutputSweep = 4;

    /// <summary>
    /// The JOINT death-match escrow (covenant-v2): ONE contract with two structural
    /// settle branches + an introspection-bound refund. Both heroes are staked into
    /// this ONE address; the winning branch is UNSIGNABLE unless the winner's hero
    /// lands at the winner's output (0xef amount==1 + 0xd1 output-script) AND the
    /// loser's hero is burned (absent from every swept output). The oracle signs
    /// ONLY the branch message — the burn + return are covenant-enforced, not packet-
    /// trusted. The refund routes EACH hero back to ITS staker (script-pinned, so
    /// anyone may trigger it post-expiry without theft).
    /// </summary>
    public static ArkadeArtifactContract BuildJoint(
        DeathMatchJointEscrowParams p, OutputDescriptor operatorKey, string emulatorSignerKeyHex)
    {
        var commitment = Convert.FromHexString(p.CommitmentHex);
        var oraclePk = Convert.FromHexString(p.OraclePkHex);
        var challengerScript = ArkAddress.Parse(p.ChallengerAddress).ScriptPubKey;
        var defenderScript = ArkAddress.Parse(p.DefenderAddress).ScriptPubKey;
        var challengerHero = global::NArk.Core.Assets.AssetId.FromString(p.ChallengerHeroAssetId);
        var defenderHero = global::NArk.Core.Assets.AssetId.FromString(p.DefenderHeroAssetId);

        // A settle branch: oracle-authorize THIS (match, winner) pair + reveal the
        // committed seed, THEN structurally bind the outcome — winner's hero at the
        // winner's output, loser's hero burned. Witness (bottom→top):
        // [serverSeed, oracleSig] (both gates consume from the witness; the two
        // structural checks are fully baked). Ends in OP_1 (exactly one truthy item).
        byte[] SettleBranch(bool challengerWon)
        {
            var winnerHero = challengerWon ? challengerHero : defenderHero;
            var winnerScript = challengerWon ? challengerScript : defenderScript;
            var loserHero = challengerWon ? defenderHero : challengerHero;
            return
            [
                .. ArkadeCovenants.CheckSigFromStackGate(
                    ArkadeCovenants.DeathMatchSettleMessage(p.DeathMatchId, challengerWon), oraclePk),
                .. ArkadeCovenants.Sha256Gate(commitment),
                .. ArkadeCovenants.AssetAtOutput(0, winnerHero, winnerScript),
                .. ArkadeCovenants.AssetBurned(loserHero, SettleOutputSweep),
                0x51, // OP_1 — leave EXACTLY one truthy stack item
            ];
        }

        // Refund (timelocked): each hero routed home — challenger's → output 0
        // paying the challenger, defender's → output 1 paying the defender. No
        // oracle, no seed; fully baked (empty witness). Script-pinned destinations
        // mean anyone may trigger it after expiry without being able to steal.
        byte[] refund =
        [
            .. ArkadeCovenants.AssetAtOutput(0, challengerHero, challengerScript),
            .. ArkadeCovenants.AssetAtOutput(1, defenderHero, defenderScript),
            0x51, // OP_1
        ];

        var refundLockTime = new LockTime((uint)p.RefundAfterUnixSeconds);
        return new ArkadeArtifactContract(
            "deathmatch-joint", operatorKey, emulatorSignerKeyHex,
            [
                new("settleToChallenger", SettleBranch(challengerWon: true)),
                new("settleToDefender", SettleBranch(challengerWon: false)),
                new("refund", refund, refundLockTime),
            ]);
    }
}
