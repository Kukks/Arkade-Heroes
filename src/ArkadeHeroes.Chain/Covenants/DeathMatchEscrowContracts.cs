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
/// <summary>One staked gear position: an item ASSET id (chain-resolved) and how many units this side stakes (1 per equipped slot; the same fungible item on both sides aggregates at settle).</summary>
public sealed record GearStake(string AssetId, int Amount);

public sealed record DeathMatchJointEscrowParams(
    string ChallengerAddress,
    string ChallengerHeroAssetId,
    string DefenderAddress,
    string DefenderHeroAssetId,
    string CommitmentHex,
    string OraclePkHex,
    string DeathMatchId,
    long EscrowSats,
    long RefundAfterUnixSeconds,
    IReadOnlyList<GearStake>? ChallengerGear = null,
    IReadOnlyList<GearStake>? DefenderGear = null);

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

        // Staked gear: per-side lists baked at open. The settle checks use the MERGED
        // per-asset totals (the same fungible item on both sides aggregates at the
        // winner's output); the refund checks use the per-side amounts. Ordinal ordering
        // everywhere — ADDRESS-CRITICAL (server + any client rebuild must agree).
        var challengerGear = p.ChallengerGear ?? [];
        var defenderGear = p.DefenderGear ?? [];
        var mergedGear = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var g in challengerGear) mergedGear[g.AssetId] = mergedGear.GetValueOrDefault(g.AssetId) + g.Amount;
        foreach (var g in defenderGear) mergedGear[g.AssetId] = mergedGear.GetValueOrDefault(g.AssetId) + g.Amount;

        // A settle branch: oracle-authorize THIS (match, winner) pair + reveal the
        // committed seed, THEN structurally bind the outcome — winner's hero AND all
        // staked gear at the winner's output, loser's hero burned. Witness (bottom→top):
        // [serverSeed, oracleSig] (both gates consume from the witness; every
        // structural check is fully baked). Ends in OP_1 (exactly one truthy item).
        byte[] SettleBranch(bool challengerWon)
        {
            var winnerHero = challengerWon ? challengerHero : defenderHero;
            var winnerScript = challengerWon ? challengerScript : defenderScript;
            var loserHero = challengerWon ? defenderHero : challengerHero;
            var s = new List<byte>();
            s.AddRange(ArkadeCovenants.CheckSigFromStackGate(
                ArkadeCovenants.DeathMatchSettleMessage(p.DeathMatchId, challengerWon), oraclePk));
            s.AddRange(ArkadeCovenants.Sha256Gate(commitment));
            s.AddRange(ArkadeCovenants.AssetAtOutput(0, winnerHero, winnerScript));
            s.AddRange(ArkadeCovenants.AssetBurned(loserHero, SettleOutputSweep));
            // ALL staked gear → the winner at output 0 (per-asset aggregated amounts).
            foreach (var (gearId, total) in mergedGear)
                s.AddRange(ArkadeCovenants.AssetAtOutput(
                    0, global::NArk.Core.Assets.AssetId.FromString(gearId), winnerScript, total));
            s.Add(0x51); // OP_1 — leave EXACTLY one truthy stack item
            return [.. s];
        }

        // Refund (timelocked): each side's hero + OWN gear routed home — challenger's →
        // output 0 paying the challenger, defender's → output 1 paying the defender
        // (distinct owners; each side's assets share ONE output). No oracle, no seed;
        // fully baked (empty witness). Script-pinned destinations mean anyone may
        // trigger it after expiry without being able to steal.
        var refundScript = new List<byte>();
        refundScript.AddRange(ArkadeCovenants.AssetAtOutput(0, challengerHero, challengerScript));
        foreach (var g in challengerGear.OrderBy(g => g.AssetId, StringComparer.Ordinal))
            refundScript.AddRange(ArkadeCovenants.AssetAtOutput(
                0, global::NArk.Core.Assets.AssetId.FromString(g.AssetId), challengerScript, g.Amount));
        refundScript.AddRange(ArkadeCovenants.AssetAtOutput(1, defenderHero, defenderScript));
        foreach (var g in defenderGear.OrderBy(g => g.AssetId, StringComparer.Ordinal))
            refundScript.AddRange(ArkadeCovenants.AssetAtOutput(
                1, global::NArk.Core.Assets.AssetId.FromString(g.AssetId), defenderScript, g.Amount));
        refundScript.Add(0x51); // OP_1
        byte[] refund = [.. refundScript];

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
