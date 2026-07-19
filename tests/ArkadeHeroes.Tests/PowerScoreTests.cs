using ArkadeHeroes.Core.Equipment;
using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Heroes;
using ArkadeHeroes.Core.Progression;

namespace ArkadeHeroes.Tests;

/// <summary>
/// F18 power score: one scalar for a hero's realized strength (level + growth genes + equipped gear
/// + skill kit + crit EV + capped affinity), for fairer matchmaking than LevelGap. A pure function of
/// hero state — it never enters combat or the XP transfer. The heuristic weights are sim-tunable; the
/// facts pinned here are the monotonic ones the ordering relies on.
/// </summary>
public class PowerScoreTests
{
    private static Hero HeroAt(int level, params string[] equipped)
    {
        var h = new Hero { Id = "h", OwnerId = "p", Name = "H", Genome = Genome.NewGen0(new byte[] { 1, 2, 3, 4 }), Level = level };
        foreach (var id in equipped) h.Equipment.Equip(ItemCatalog.Find(id)!);
        return h;
    }

    [Fact]
    public void HigherLevelScoresStrictlyHigher()
        => Assert.True(PowerScore.Compute(HeroAt(10)) > PowerScore.Compute(HeroAt(1)));

    [Fact]
    public void EquippedGearScoresStrictlyHigher()
        => Assert.True(PowerScore.Compute(HeroAt(5, "arkforged-edge")) > PowerScore.Compute(HeroAt(5)));

    [Fact]
    public void MoreGearScoresStrictlyHigher()
    {
        // Gear moves realized power monotonically — the axis LevelGap matchmaking is blind to.
        var oneItem = PowerScore.Compute(HeroAt(5, "arkforged-edge"));
        var threeItems = PowerScore.Compute(HeroAt(5, "arkforged-edge", "covenant-plate", "vtxo-charm"));
        Assert.True(threeItems > oneItem);
    }

    [Fact]
    public void PowerGapPercentIsSymmetricAndZeroForEqual()
    {
        Assert.Equal(0, Matchmaking.PowerGapPercent(250, 250));
        Assert.Equal(Matchmaking.PowerGapPercent(200, 300), Matchmaking.PowerGapPercent(300, 200));
    }

    [Fact]
    public void PowerFavorBands()
    {
        Assert.Equal("favored", Matchmaking.PowerFavor(300, 200));   // 1.5×
        Assert.Equal("underdog", Matchmaking.PowerFavor(200, 300));  // 0.67×
        Assert.Equal("even", Matchmaking.PowerFavor(105, 100));      // within ±15%
    }
}
