using ArkadeHeroes.Core;
using ArkadeHeroes.Core.Progression;

namespace ArkadeHeroes.Tests;

/// <summary>Duel.razor tells the player a heavy loss "costs a level", and names the level-3-beaten-by-1
/// case. Both are arithmetic on Leveling, so both can be checked rather than trusted.</summary>
public class DuelCopyStillTrueTests
{
    [Fact]
    public void AHeavyLossStillCostsALevel_AndTheQuotedCaseIsTheRealOne()
    {
        var config = GameConfig.Default;

        // The page's own example: a level-3 hero at the bottom of its level, beaten by a level 1.
        var transfer = Leveling.PayableTransfer(winnerLevel: 1, loserLevel: 3, loserXp: 0, config);
        var (level, _, _) = Leveling.Apply(3, 0, -transfer, config);

        Assert.Equal(2, level);
    }

    [Fact]
    public void TheTransferComesOffTheBANKEDTotal_WhichIsWhatMakesADelevelPossible()
    {
        var config = GameConfig.Default;

        // If it were bounded by progress-within-level, a loss could only ever reach 0 XP at the same level
        // and no delevel could happen at all — which is precisely what the page's claim rests on.
        Assert.True(Leveling.TotalXp(3, 0, config) > 0,
            "A hero at the bottom of level 3 has banked XP to lose; without it the copy is wrong.");
        Assert.True(Leveling.PayableTransfer(1, 3, 0, config) > 0,
            "A level-1 winner can still take XP off a level-3 loser.");
    }
}
