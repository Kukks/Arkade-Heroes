using NArk.Abstractions;
using NBitcoin;
using NBitcoin.Scripting;

namespace ArkadeHeroes.Chain.Covenants;

/// <summary>
/// Everything needed to rebuild a match's per-party escrow contracts from
/// scratch: the seed commitment, both players' own addresses, the stake, the
/// oracle key, and the refund expiry. These are PUBLIC parameters — the escrow
/// addresses commit to them — persisted by the server per match (KV
/// <c>escrow:{matchId}</c>) and served at <c>/api/matches/{id}/escrow</c> so a
/// PLAYER can independently reconstruct the contracts and reclaim an abandoned
/// stake without trusting the server's covenant claims.
/// </summary>
public sealed record WagerEscrowParams(
    string CommitmentHex, string ChallengerAddress, string DefenderAddress, long StakeSats,
    string OraclePkHex, string MatchId, long RefundAfterUnixSeconds);

/// <summary>
/// The canonical construction of the wager-escrow contracts — shared by the
/// game server (escrow creation, funding checks, settlement) and the client
/// (refund reclaim), so both sides derive byte-identical taptrees and
/// addresses from the same <see cref="WagerEscrowParams"/>.
/// </summary>
public static class WagerEscrowContracts
{
    /// <summary>
    /// Per-party escrow contracts (coinflip's shape): both carry BOTH settle
    /// branches (the winner branch spends both VTXOs atomically), and each
    /// carries a timelocked refund leaf paying ONLY its own party — so a party
    /// can always reclaim their own stake after expiry, and never the other's.
    /// </summary>
    public static (ArkadeArtifactContract Challenger, ArkadeArtifactContract Defender) Build(
        WagerEscrowParams parameters, OutputDescriptor operatorKey, string emulatorSignerKeyHex)
    {
        var commitment = Convert.FromHexString(parameters.CommitmentHex);
        var oraclePk = Convert.FromHexString(parameters.OraclePkHex);
        var challengerScript = ArkAddress.Parse(parameters.ChallengerAddress).ScriptPubKey;
        var defenderScript = ArkAddress.Parse(parameters.DefenderAddress).ScriptPubKey;
        var pot = parameters.StakeSats * 2;

        // Each settle branch pins ITS OWN message, so the oracle's signature
        // authorizes exactly one (match, winner) pair — no cross-branch replay.
        ArkadeContractFunction[] settleBranches =
        [
            new("settleToChallenger",
                ArkadeCovenants.SettleAuthorized(
                    ArkadeCovenants.SettleMessage(parameters.MatchId, challengerWon: true), oraclePk,
                    commitment, challengerScript, pot, parameters.StakeSats)),
            new("settleToDefender",
                ArkadeCovenants.SettleAuthorized(
                    ArkadeCovenants.SettleMessage(parameters.MatchId, challengerWon: false), oraclePk,
                    commitment, defenderScript, pot, parameters.StakeSats)),
        ];

        var refundLockTime = new LockTime((uint)parameters.RefundAfterUnixSeconds);
        var challengerContract = new ArkadeArtifactContract(
            "wager-escrow-challenger", operatorKey, emulatorSignerKeyHex,
            [
                .. settleBranches,
                new("refund", ArkadeCovenants.RefundTo(challengerScript, parameters.StakeSats), refundLockTime),
            ]);
        var defenderContract = new ArkadeArtifactContract(
            "wager-escrow-defender", operatorKey, emulatorSignerKeyHex,
            [
                .. settleBranches,
                new("refund", ArkadeCovenants.RefundTo(defenderScript, parameters.StakeSats), refundLockTime),
            ]);
        return (challengerContract, defenderContract);
    }
}
