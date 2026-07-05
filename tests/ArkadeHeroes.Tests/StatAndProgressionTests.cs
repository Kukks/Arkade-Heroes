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

    [Fact]
    public void ApplyDelevelsOnNegativeDelta()
    {
        // Climb to level 3, then take a loss large enough to give back a level.
        var (level, xp, _) = Leveling.Apply(1, 0, Leveling.XpToNext(1) + Leveling.XpToNext(2) + 5);
        Assert.Equal(3, level);

        var (dropped, _, changed) = Leveling.Apply(level, xp, -(Leveling.XpToNext(2) + xp + 1));
        Assert.True(dropped < 3, $"expected a delevel from 3, stayed at {dropped}");
        Assert.True(changed < 0);
    }

    [Fact]
    public void DelevelFloorsAtLevelOneAndZeroXp()
    {
        // A crushing negative delta can only fall to the floor, never below it.
        var (level, xp, changed) = Leveling.Apply(5, 10, -1_000_000);
        Assert.Equal(1, level);
        Assert.Equal(0, xp);
        Assert.Equal(-4, changed);
    }

    [Fact]
    public void XpTransferIsDifferenceBased_ConservedAndClampedAtZero()
    {
        // A peer fight moves the base amount; conserved because the loser's delta
        // is the exact negation of this single value at the call site.
        Assert.Equal(Leveling.BaseTransfer, Leveling.XpTransfer(3, 3));
        // Beating a far weaker hero transfers nothing — no farming down the ladder.
        Assert.Equal(0, Leveling.XpTransfer(20, 1));
        // An upset over a higher hero strips off (and awards) more than the base.
        Assert.True(Leveling.XpTransfer(2, 6) > Leveling.BaseTransfer);
    }

    [Fact]
    public void MatchFeeScalesWithLevel()
    {
        Assert.Equal(Leveling.MatchFeePerLevel, Leveling.MatchFee(1));
        Assert.Equal(Leveling.MatchFeePerLevel * 10, Leveling.MatchFee(10));
        Assert.True(Leveling.MatchFee(5) > Leveling.MatchFee(4));
    }
}
