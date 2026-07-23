using ArkadeHeroes.Core.Heroes;

namespace ArkadeHeroes.Core.Combat;

/// <summary>
/// 3v3 team-synergy bonus, gated behind <see cref="CombatConfig.SquadSynergy"/> (default OFF). A squad's
/// heroes reinforce each other by ELEMENTAL DIVERSITY: a lineup spanning more of the element ring can't be
/// hard-countered by a single type, so each of its members fights a little better. Pure + deterministic in
/// the lineup, so both the server's <see cref="SquadBattle.Resolve"/> and the client's
/// <c>FairnessAudit.VerifySquad</c> compute the same bonus and the squad match still verifies identically.
/// </summary>
public static class SquadSynergy
{
    /// <summary>The most a fully diverse squad adds to each member's damage — a nudge, not a trump, matching
    /// the affinity cap (<c>AffinityBonuses.Default.Cap</c> = 0.05). A mono-element squad gets nothing.</summary>
    public const double MaxBonus = 0.05;

    /// <summary>The damage MULTIPLIER (1.0 = none) each hero in <paramref name="lineup"/> earns from the
    /// lineup's elemental diversity: 0 extra for all-one-element, scaling linearly to <see cref="MaxBonus"/>
    /// when every slot is a distinct element. Symmetric — two equally diverse squads cancel out, so it rewards
    /// building a varied COMP against the opponent, never universal power creep.</summary>
    public static double Multiplier(IReadOnlyList<Hero> lineup)
    {
        if (lineup.Count <= 1) return 1.0;
        var distinct = lineup.Select(h => h.Genome.Element).Distinct().Count();
        var steps = distinct - 1;               // 0 for a mono squad, up to lineup.Count-1 when all distinct
        var maxSteps = lineup.Count - 1;
        return 1.0 + MaxBonus * steps / maxSteps;   // MaxBonus is double → real division, not integer
    }
}
