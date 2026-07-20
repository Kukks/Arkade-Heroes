using ArkadeHeroes.Core;
using ArkadeHeroes.Core.Progression;

namespace ArkadeHeroes.Tests;

/// <summary>Day windows, streak transitions, and reward math are pure functions — deterministic
/// and testable without a clock (the caller injects `now`, exactly like Season).</summary>
public class DailyTests
{
    [Fact]
    public void DayIndex_AtEpoch_IsZero() =>
        Assert.Equal(0, Daily.DayIndex(Daily.Epoch));

    [Fact]
    public void DayIndex_SameDay_UnchangedAcrossHours() =>
        Assert.Equal(0, Daily.DayIndex(Daily.Epoch.AddHours(23)));

    [Fact]
    public void DayIndex_AtUtcMidnight_RollsOver() =>
        Assert.Equal(1, Daily.DayIndex(Daily.Epoch.AddDays(1)));

    [Fact]
    public void DayIndex_BeforeEpoch_ClampsToZero() =>
        Assert.Equal(0, Daily.DayIndex(Daily.Epoch.AddDays(-3)));

    [Fact]
    public void ForDay_IsHalfOpenWindow()
    {
        var w = Daily.ForDay(Daily.Epoch.AddDays(5).AddHours(6));
        Assert.Equal(5, w.DayIndex);
        Assert.Equal(Daily.Epoch.AddDays(5), w.Start);
        Assert.Equal(Daily.Epoch.AddDays(6), w.End);
    }

    [Theory]
    [InlineData(null, 5, 3, 1)]   // first-ever claim → streak 1
    [InlineData(4, 5, 3, 4)]      // consecutive day → +1
    [InlineData(3, 5, 3, 1)]      // gap → reset to 1
    [InlineData(5, 5, 3, 3)]      // same day (defensive) → unchanged
    public void Streak_Next(int? last, int today, int current, int expected) =>
        Assert.Equal(expected, DailyStreak.Next(last, today, current));

    [Fact]
    public void Reward_BaseOnly_NoQuests_NoStreakBonus()
    {
        var r = DailyReward.Compute(GameConfig.Default, completedQuests: 0, streak: 1);
        Assert.Equal(GameConfig.Default.DailyBaseSats, r.Base);
        Assert.Equal(0, r.QuestBonus);
        Assert.Equal(0, r.StreakBonusPct);
        Assert.Equal(GameConfig.Default.DailyBaseSats, r.Total);
    }

    [Fact]
    public void Reward_QuestsAndStreak_Compound()
    {
        var cfg = GameConfig.Default;   // 50 base, 150/quest, +10%/day cap 100%
        var r = DailyReward.Compute(cfg, completedQuests: 3, streak: 11);
        Assert.Equal(450, r.QuestBonus);
        Assert.Equal(100, r.StreakBonusPct);          // (11-1)*10 = 100, capped
        Assert.Equal((50 + 450) * 2, r.Total);        // gross × 2
    }

    [Fact]
    public void Reward_StreakBonus_CapsAt100Pct()
    {
        var r = DailyReward.Compute(GameConfig.Default, completedQuests: 0, streak: 50);
        Assert.Equal(100, r.StreakBonusPct);          // not 490
    }
}
