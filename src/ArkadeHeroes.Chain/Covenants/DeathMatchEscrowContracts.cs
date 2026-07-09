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
/// <summary>One staked gear position: an item ASSET id (chain-resolved) and how many units this side stakes (1 per equipped slot; the same fungible item on both sides aggregates at settle). <paramref name="ItemId"/> is display-only provenance (the catalog item the asset resolves from — 1:1); the covenant uses only AssetId + Amount.</summary>
public sealed record GearStake(string AssetId, int Amount, string? ItemId = null);

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
    IReadOnlyList<GearStake>? DefenderGear = null,
    bool Absorb = false,
    string SpeciesId = "");

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

        // A reclaim branch (covenant-v2, trustless): after expiry, MY hero + MY gear → MY
        // output 0 (script-pinned). No oracle, no server. The counterparty's carriers can't be
        // pulled in: NumAssetGroupsIs(N) forbids any extra asset group (their hero / exclusive
        // gear), and per-my-asset AssetInputSumIs bounds a SHARED fungible-gear group to my own
        // staked units. Timelocked (RefundAfterUnixSeconds) so the winner settles first — reclaim
        // only ever reaches an ABANDONED escrow. Empty witness. Ends in OP_1.
        byte[] ReclaimBranch(bool isChallenger)
        {
            var myHero = isChallenger ? challengerHero : defenderHero;
            var myScript = isChallenger ? challengerScript : defenderScript;
            var myGear = isChallenger ? challengerGear : defenderGear;
            // Distinct gear assets (ordinal — address-critical), aggregated amount per asset.
            var myGearByAsset = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (var g in myGear) myGearByAsset[g.AssetId] = myGearByAsset.GetValueOrDefault(g.AssetId) + g.Amount;

            var s = new List<byte>();
            s.AddRange(ArkadeCovenants.AssetAtOutput(0, myHero, myScript));
            foreach (var (gearId, total) in myGearByAsset)
                s.AddRange(ArkadeCovenants.AssetAtOutput(
                    0, global::NArk.Core.Assets.AssetId.FromString(gearId), myScript, total));
            // Exactly my distinct asset groups (hero + each distinct gear asset).
            s.AddRange(ArkadeCovenants.NumAssetGroupsIs(1 + myGearByAsset.Count));
            // Bound each of my assets' input sum to my staked amount (hero = 1).
            s.AddRange(ArkadeCovenants.AssetInputSumIs(myHero, 1));
            foreach (var (gearId, total) in myGearByAsset)
                s.AddRange(ArkadeCovenants.AssetInputSumIs(
                    global::NArk.Core.Assets.AssetId.FromString(gearId), total));
            s.Add(0x51); // OP_1
            return [.. s];
        }

        var refundLockTime = new LockTime((uint)p.RefundAfterUnixSeconds);
        // Absorb mode adds two settleMint leaves (burn both + mint the absorbed hero under species);
        // the classic death-match stays at 4 leaves, byte-identical (its address does not shift).
        var species = p.Absorb ? global::NArk.Core.Assets.AssetId.FromString(p.SpeciesId) : default;
        return new ArkadeArtifactContract(
            "deathmatch-joint", operatorKey, emulatorSignerKeyHex,
            [
                new("settleToChallenger", SettleBranch(challengerWon: true)),
                new("settleToDefender", SettleBranch(challengerWon: false)),
                .. (p.Absorb
                    ? new ArkadeContractFunction[]
                    {
                        new("settleMintChallenger", SettleMintLeaf(
                            challengerHero, defenderHero, challengerScript, species, oraclePk, commitment,
                            p.DeathMatchId, challengerWon: true, mergedGear)),
                        new("settleMintDefender", SettleMintLeaf(
                            defenderHero, challengerHero, defenderScript, species, oraclePk, commitment,
                            p.DeathMatchId, challengerWon: false, mergedGear)),
                    }
                    : []),
                new("reclaimChallenger", ReclaimBranch(isChallenger: true), refundLockTime),
                new("reclaimDefender", ReclaimBranch(isChallenger: false), refundLockTime),
            ]);
    }

    /// <summary>
    /// The <c>settleMint</c> leaf for an ABSORB death-match: oracle-authorize THIS (match, winner)
    /// absorb-mint (a DISTINCT message from the keep settle) + reveal the seed, then STRUCTURALLY
    /// burn BOTH staked heroes and mint the absorbed hero UNDER THE SPECIES to the winner (+ all
    /// staked gear at output 0). The oracle signs the absorb-mint message AND the minted metadata
    /// root; the burn/mint/route are covenant-enforced, not packet-trusted. Witness =
    /// <see cref="ArkadeCovenants.DeathMatchAbsorbMintWitness"/>. Ends in OP_1. Shared by the
    /// structural probe and (T3) the 6-leaf <see cref="BuildJoint"/>.
    /// </summary>
    public static byte[] SettleMintLeaf(
        global::NArk.Core.Assets.AssetId winnerHero, global::NArk.Core.Assets.AssetId loserHero,
        Script winnerScript, global::NArk.Core.Assets.AssetId species,
        byte[] oraclePk, byte[] commitment, string deathMatchId, bool challengerWon,
        IEnumerable<KeyValuePair<string, int>> mergedGear)
    {
        var s = new List<byte>();
        s.AddRange(ArkadeCovenants.CheckSigFromStackGate(
            ArkadeCovenants.DeathMatchAbsorbMintMessage(deathMatchId, challengerWon), oraclePk));
        s.AddRange(ArkadeCovenants.Sha256Gate(commitment));
        s.AddRange(ArkadeCovenants.MintUnderSpeciesAuthorized(species, winnerHero, loserHero, oraclePk));
        s.Add(0x69); // OP_VERIFY — consume the oracle-root verdict
        s.AddRange(ArkadeCovenants.AssetBurned(winnerHero, SettleOutputSweep)); // old winner hero destroyed
        s.AddRange(ArkadeCovenants.AssetBurned(loserHero, SettleOutputSweep));  // loser hero destroyed
        s.AddRange(ArkadeCovenants.MintToPlayer(winnerScript));                 // absorbed hero → winner (output 0)
        foreach (var (gearId, total) in mergedGear)
            s.AddRange(ArkadeCovenants.AssetAtOutput(
                0, global::NArk.Core.Assets.AssetId.FromString(gearId), winnerScript, total));
        s.Add(0x51); // OP_1 — leave EXACTLY one truthy item
        return [.. s];
    }
}
