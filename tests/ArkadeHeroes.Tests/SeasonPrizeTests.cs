using ArkadeHeroes.Core.Progression;

namespace ArkadeHeroes.Tests;

/// <summary>Pot math + season-number windows are pure functions — deterministic, testable without a clock.</summary>
public class SeasonPrizeTests
{
    [Fact] public void Split_TopThree_60_30_10() =>
        Assert.Equal(new long[] { 600, 300, 100 }, SeasonPrize.Split(1000, 3, SeasonPrize.Weights));

    [Fact] public void Split_FewerWinners_OnlyPresentRanks() =>
        Assert.Equal(new long[] { 600, 300 }, SeasonPrize.Split(1000, 2, SeasonPrize.Weights));

    [Fact] public void Split_SoloWinner_GetsFirstWeightOnly() =>
        Assert.Equal(new long[] { 600 }, SeasonPrize.Split(1000, 1, SeasonPrize.Weights));

    [Fact] public void Split_NoWinners_Empty() =>
        Assert.Empty(SeasonPrize.Split(1000, 0, SeasonPrize.Weights));

    [Fact]
    public void Split_Floors_DustStaysBehind()
    {
        var s = SeasonPrize.Split(999, 3, SeasonPrize.Weights);   // 599.4 / 299.7 / 99.9 → floored
        Assert.Equal(new long[] { 599, 299, 99 }, s);
        Assert.True(s.Sum() <= 999);
    }

    [Theory]
    [InlineData(4, 5, new int[0])]        // current 5 → due up to 4; lastSettled 4 → nothing
    [InlineData(3, 5, new[] { 4 })]       // season 4 ended, unsettled
    [InlineData(0, 3, new[] { 1, 2 })]    // seasons 1,2 ended, none settled
    [InlineData(4, 4, new int[0])]        // current season not ended
    public void DueSeasons_Range(int lastSettled, int current, int[] expected) =>
        Assert.Equal(expected, SeasonPrize.DueSeasons(lastSettled, current).ToArray());

    [Fact]
    public void Season_ForNumber_MatchesEpochWindows()
    {
        Assert.Equal(Season.Epoch, Season.ForNumber(1, 14).Start);
        var s16 = Season.ForNumber(16, 14);
        Assert.Equal(Season.Epoch.AddDays(15 * 14), s16.Start);
        Assert.Equal(s16.Start.AddDays(14), s16.End);
        Assert.Equal(16, s16.Number);
    }
}
