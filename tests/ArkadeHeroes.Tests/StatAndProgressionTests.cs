using ArkadeHeroes.Core.Equipment;
using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Heroes;
using ArkadeHeroes.Core.Progression;

namespace ArkadeHeroes.Tests;

public class StatAndProgressionTests
{
    private static Genome GenomeWith(byte statGenes, byte growthGenes)
    {
        var bytes = new byte[32];
        for (var i = 0; i < 5; i++) bytes[i] = statGenes;
        for (var i = 8; i < 13; i++) bytes[i] = growthGenes;
        return new Genome(bytes);
    }

    [Fact]
    public void HigherGenesGiveHigherStats()
    {
        var weak = StatBlock.ComputeFor(GenomeWith(0, 0), level: 1);
        var strong = StatBlock.ComputeFor(GenomeWith(255, 0), level: 1);
        Assert.True(strong.Attack > weak.Attack);
        Assert.True(strong.MaxHp > weak.MaxHp);
        Assert.True(strong.Speed > weak.Speed);
    }

    [Fact]
    public void GrowthGenesDominateAtHighLevel()
    {
        // Same visible genes, opposite growth genes: equal at level 1, far apart at 40.
        var slow = GenomeWith(128, 0);
        var fast = GenomeWith(128, 255);

        var slowL1 = StatBlock.ComputeFor(slow, 1);
        var fastL1 = StatBlock.ComputeFor(fast, 1);
        Assert.Equal(slowL1.Attack, fastL1.Attack);

        var slowL40 = StatBlock.ComputeFor(slow, 40);
        var fastL40 = StatBlock.ComputeFor(fast, 40);
        Assert.True(fastL40.Attack >= slowL40.Attack + 100,
            $"Expected growth genes to add ≥100 attack by level 40 (got {slowL40.Attack} vs {fastL40.Attack}).");
    }

    [Fact]
    public void EquipmentModifiesStats()
    {
        var genome = GenomeWith(128, 128);
        var bare = StatBlock.ComputeFor(genome, 5);
        var armed = StatBlock.ComputeFor(genome, 5, [ItemCatalog.Find("steel-saber")!]);
        Assert.Equal(bare.Attack + 10, armed.Attack);
    }

    [Fact]
    public void XpCurveIsMonotonic()
    {
        for (var level = 1; level < Leveling.MaxLevel; level++)
            Assert.True(Leveling.XpToNext(level + 1) > Leveling.XpToNext(level));
    }

    [Fact]
    public void ApplyLevelsUpAcrossMultipleThresholds()
    {
        var bigAward = Leveling.XpToNext(1) + Leveling.XpToNext(2) + 10;
        var (level, xp, gained) = Leveling.Apply(1, 0, bigAward);
        Assert.Equal(3, level);
        Assert.Equal(10, xp);
        Assert.Equal(2, gained);
    }

    [Fact]
    public void LevelCapsAtMax()
    {
        var (level, xp, _) = Leveling.Apply(Leveling.MaxLevel, 0, 1_000_000);
        Assert.Equal(Leveling.MaxLevel, level);
        Assert.Equal(0, xp);
    }
}
