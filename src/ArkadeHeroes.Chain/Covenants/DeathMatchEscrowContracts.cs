using NArk.Abstractions;
using NBitcoin;
using NBitcoin.Scripting;

namespace ArkadeHeroes.Chain.Covenants;

/// <summary>
/// Public rebuildable params for ONE party's death-match escrow: their staked
/// hero, the match commitment, both parties' addresses (the settle spends both
/// per-party escrows), the oracle key, the hero-carrier escrow value, and the
/// refund expiry. Persisted per (deathMatchId, role) and served at
/// <c>/api/deathmatch/{id}/escrow/{role}</c> so a player can rebuild the contract
/// and reclaim an abandoned stake without trusting the server. Gear is
/// covenant-staked in a follow-up (<see cref="GearAssetIds"/>).
/// </summary>
public sealed record DeathMatchEscrowParams(
    string PlayerAddress,
    string HeroAssetId,
    IReadOnlyList<string> GearAssetIds,
    string CommitmentHex,
    string OraclePkHex,
    string DeathMatchId,
    string Role,
    long EscrowSats,
    long RefundAfterUnixSeconds);

/// <summary>
/// The canonical construction of one party's death-match escrow contract — shared
/// by the server (escrow creation, funding checks, settlement) and the client
/// (refund reclaim), so both derive a byte-identical taptree + address from the
/// same <see cref="DeathMatchEscrowParams"/>. Mirrors <see cref="WagerEscrowContracts"/>.
/// </summary>
public static class DeathMatchEscrowContracts
{
    /// <summary>
    /// One party's escrow: BOTH oracle-signed settle branches
    /// (settleToChallenger/settleToDefender — the winning branch is spent for BOTH
    /// escrows in one tx) and a timelocked refund leaf paying ONLY this party (the
    /// staked hero rides the refund output home). The settle branches carry NO sats
    /// PayTo — the "pot" is heroes, routed by the asset packet — just the oracle-sig
    /// gate over the branch message + the committed-seed reveal.
    /// </summary>
    public static ArkadeArtifactContract Build(
        DeathMatchEscrowParams parameters, OutputDescriptor operatorKey, string emulatorSignerKeyHex)
    {
        var commitment = Convert.FromHexString(parameters.CommitmentHex);
        var oraclePk = Convert.FromHexString(parameters.OraclePkHex);
        var playerScript = ArkAddress.Parse(parameters.PlayerAddress).ScriptPubKey;

        // Each settle branch pins ITS OWN message, so the oracle's signature
        // authorizes exactly one (death-match, winner) pair — no cross-branch replay.
        ArkadeContractFunction[] settleBranches =
        [
            new("settleToChallenger",
                ArkadeCovenants.SettleAuthorizedNoPot(
                    ArkadeCovenants.DeathMatchSettleMessage(parameters.DeathMatchId, challengerWon: true), oraclePk, commitment)),
            new("settleToDefender",
                ArkadeCovenants.SettleAuthorizedNoPot(
                    ArkadeCovenants.DeathMatchSettleMessage(parameters.DeathMatchId, challengerWon: false), oraclePk, commitment)),
        ];

        var refundLockTime = new LockTime((uint)parameters.RefundAfterUnixSeconds);
        return new ArkadeArtifactContract(
            $"deathmatch-escrow-{parameters.Role}", operatorKey, emulatorSignerKeyHex,
            [
                .. settleBranches,
                new("refund", ArkadeCovenants.RefundTo(playerScript, parameters.EscrowSats), refundLockTime),
            ]);
    }
}
