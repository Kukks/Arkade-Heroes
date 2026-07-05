namespace ArkadeHeroes.Chain.Covenants;

/// <summary>
/// Public rebuildable params for ONE party's death-match escrow: their staked
/// hero, the match commitment, both parties' addresses (the settle spends both
/// per-party escrows), the oracle key, and the refund expiry. Persisted per
/// (deathMatchId, role) and served at <c>/api/deathmatch/{id}/escrow/{role}</c>
/// so a player can rebuild the contract and reclaim an abandoned stake without
/// trusting the server. Gear is covenant-staked in rung 2 (<see cref="GearAssetIds"/>).
///
/// The <c>Build</c> method (rung 2) mirrors <see cref="WagerEscrowContracts"/> —
/// per-party contracts with two oracle-signed settle branches
/// (settleToChallenger/settleToDefender, no sats PayTo — the "pot" is heroes) and
/// a timelocked refund leaf paying only this party.
/// </summary>
public sealed record DeathMatchEscrowParams(
    string PlayerAddress,
    string HeroAssetId,
    IReadOnlyList<string> GearAssetIds,
    string CommitmentHex,
    string ChallengerAddress,
    string DefenderAddress,
    string OraclePkHex,
    string DeathMatchId,
    string Role,
    long RefundAfterUnixSeconds);
