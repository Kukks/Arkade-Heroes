using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Heroes;
using ArkadeHeroes.Core.Skills;

namespace ArkadeHeroes.Core.Progression;

/// <summary>
/// One scalar for how strong a hero actually is in a fight — folding realized stats (level + growth
/// genes + equipped gear, via <see cref="StatBlock.ComputeFor"/>), the unlocked skill kit, crit EV,
/// and capped elemental affinity. Same-level heroes vary wildly (traits, gear, rarity), which plain
/// <see cref="Matchmaking.LevelGap"/> can't see — the power score can, so matchmaking suggests fairer
/// fights (and death-match, where gear is STAKED, reads a truthful favorability).
///
/// It is a pure function of CURRENT hero state — not a history rating — so there is no Elo to tank or
/// farm, and it only orders SUGGESTIONS (anyone can still challenge anyone). Cosmetic rarity carries
/// ZERO weight: traits touch combat only through <see cref="Traits.AffinityModifier"/>. The score
/// NEVER enters combat resolution or the XP transfer — keeping the conserved-XP invariant untouched.
/// </summary>
public static class PowerScore
{
    // BattleEngine damage is ~ Power·scale / (Defense + 25), so 25 sets the survivability curve.
    private const double ArmorConstant = 25;

    /// <summary>The hero's realized power. Heuristic weights (off-stat /4, /50 survival scale, sqrt) are
    /// tunable against a win-rate sim; the score's use — ordering suggestions — is robust to the exact values.</summary>
    public static int Compute(Hero hero, GameConfig? config = null)
    {
        var c = config ?? GameConfig.Default;
        var stats = StatBlock.ComputeFor(hero.Genome, hero.Level, hero.Equipment.ResolveItems());
        var skills = SkillCatalog.SkillsFor(hero.Genome, hero.Level, c.Combat);

        // Kit quality: the best expected single-hit power, normalized to Strike (40).
        var bestHit = skills.Max(s => s.Power * s.Accuracy / 100.0);

        // Offense: dominant offensive stat plus a quarter of the off-stat, scaled by kit quality, crit
        // EV (a crit multiplies damage 1.5×, so its expected value is 1 + 0.5·crit%), and affinity.
        var primary = Math.Max(stats.Attack, stats.Magic);
        var secondary = Math.Min(stats.Attack, stats.Magic);
        var offense = (primary + secondary / 4.0)
            * (bestHit / 40.0)
            * (1 + 0.5 * stats.CritPercent / 100.0)
            * Traits.AffinityModifier(hero.Genome, c);

        // Survivability: effective HP through armor and dodge (DodgePercent is capped at 25 in StatBlock).
        var survival = stats.MaxHp
            * ((stats.Defense + ArmorConstant) / 50.0)
            * (100.0 / (100 - stats.DodgePercent));

        return (int)Math.Round(Math.Sqrt(offense * survival));
    }
}
