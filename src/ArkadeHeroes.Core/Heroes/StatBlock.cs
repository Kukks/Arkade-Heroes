using ArkadeHeroes.Core.Equipment;
using ArkadeHeroes.Core.Genetics;

namespace ArkadeHeroes.Core.Heroes;

/// <summary>
/// Derived combat stats. Base values come from the visible stat genes; growth
/// genes (hidden potential) control per-level gains — so two heroes with equal
/// base stats can diverge sharply at high level, which is what makes breeding
/// for growth genes the long-game meta.
/// </summary>
public readonly record struct StatBlock(
    int MaxHp,
    int Attack,
    int Magic,
    int Defense,
    int Speed,
    int Luck,
    int CritPercent,
    int DodgePercent)
{
    public static StatBlock ComputeFor(Genome genome, int level, IEnumerable<Item>? equipment = null)
    {
        if (level < 1) throw new ArgumentOutOfRangeException(nameof(level));

        var strength = StatValue(genome.StrengthGene, genome.GrowthGene(Stat.Strength), level);
        var vitality = StatValue(genome.VitalityGene, genome.GrowthGene(Stat.Vitality), level);
        var agility = StatValue(genome.AgilityGene, genome.GrowthGene(Stat.Agility), level);
        var intellect = StatValue(genome.IntellectGene, genome.GrowthGene(Stat.Intellect), level);
        var luck = StatValue(genome.LuckGene, genome.GrowthGene(Stat.Luck), level);

        var mods = StatMods.Sum(equipment);

        var maxHp = 30 + vitality * 4 + level * 2 + mods.MaxHp;
        var attack = strength + mods.Attack;
        var magic = intellect + mods.Magic;
        var defense = 5 + vitality / 2 + agility / 4 + mods.Defense;
        var speed = agility + mods.Speed;
        var critPercent = Math.Min(40, 5 + luck / 4 + mods.CritPercent);
        var dodgePercent = Math.Min(25, speed / 5);

        return new StatBlock(maxHp, attack, magic, defense, speed, luck, critPercent, dodgePercent);
    }

    /// <summary>base 10..73 from the visible gene, +1..4 per level from the growth gene.</summary>
    private static int StatValue(byte gene, byte growthGene, int level)
        => 10 + gene / 4 + (1 + growthGene / 64) * (level - 1);
}
