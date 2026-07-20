using ArkadeHeroes.Core.Fairness;
using ArkadeHeroes.Core.Heroes;

namespace ArkadeHeroes.Core.Combat;

/// <summary>One resolved duel of a squad match: slot i of the challenger lineup vs slot i of the defender lineup.</summary>
public readonly record struct SquadDuel(int Slot, BattleResult Result);

/// <summary>A resolved 3v3 squad match: who won the best-of-3, each side's duel-win count, and every duel (for replay).</summary>
public readonly record struct SquadResult(
    bool ChallengerWon, int ChallengerWins, int DefenderWins, IReadOnlyList<SquadDuel> Duels);

/// <summary>
/// The 3v3 squad resolver: a POSITIONAL best-of-3 relay — slot i of the challenger lineup fights slot i of
/// the defender lineup, each at full HP with its own per-slot sub-seed derived from the match seed. Pure +
/// deterministic in (lineups, seed, config), reusing <see cref="BattleEngine.Fight"/> unchanged — the server
/// scores with it and the client replays it identically (see FairnessAudit.VerifySquad). Three odd slots →
/// the side winning ≥2 duels wins; ties are impossible. This mirrors the Gauntlet's resolve pattern.
/// </summary>
public static class SquadBattle
{
    public const int LineupSize = 3;

    public static SquadResult Resolve(
        IReadOnlyList<Hero> challengers, IReadOnlyList<Hero> defenders,
        ReadOnlySpan<byte> matchSeed, GameConfig? config = null)
    {
        var cfg = config ?? GameConfig.Default;
        var seed = matchSeed.ToArray();
        var duels = new List<SquadDuel>(LineupSize);
        int challengerWins = 0, defenderWins = 0;
        for (var slot = 0; slot < LineupSize; slot++)
        {
            var fightSeed = CommitReveal.DeriveEntropy(seed, "squad-fight", slot.ToString());
            var result = BattleEngine.Fight(challengers[slot], defenders[slot], fightSeed, cfg);
            if (result.WinnerId == challengers[slot].Id) challengerWins++; else defenderWins++;
            duels.Add(new SquadDuel(slot, result));
        }
        return new SquadResult(challengerWins > defenderWins, challengerWins, defenderWins, duels);
    }
}
