using ArkadeHeroes.Core.Progression;

namespace ArkadeHeroes.Tests;

/// <summary>Season windows are a pure function of the clock — deterministic, boundary-exact, clamped.</summary>
public class SeasonTests
{
    [Fact]
    public void Current_AtEpoch_IsSeasonOne()
    {
        var s = Season.Current(Season.Epoch, 14);
        Assert.Equal(1, s.Number);
        Assert.Equal(Season.Epoch, s.Start);
        Assert.Equal(Season.Epoch.AddDays(14), s.End);
    }

    [Fact]
    public void Current_MidSeason_KeepsTheSameWindow()
    {
        var s = Season.Current(Season.Epoch.AddDays(10), 14);   // still in season 1 = [0, 14)
        Assert.Equal(1, s.Number);
        Assert.Equal(Season.Epoch, s.Start);
    }

    [Fact]
    public void Current_AtBoundary_RollsToTheNextSeason()
    {
        var s = Season.Current(Season.Epoch.AddDays(14), 14);   // exactly the start of season 2 (End is exclusive)
        Assert.Equal(2, s.Number);
        Assert.Equal(Season.Epoch.AddDays(14), s.Start);
        Assert.Equal(Season.Epoch.AddDays(28), s.End);
    }

    [Fact]
    public void Current_ManySeasonsLater_NumbersCorrectly()
    {
        var s = Season.Current(Season.Epoch.AddDays(14 * 5 + 3), 14);   // 3 days into season 6
        Assert.Equal(6, s.Number);
    }

    [Fact]
    public void Current_BeforeEpoch_ClampsToSeasonOne()
    {
        var s = Season.Current(Season.Epoch.AddDays(-5), 14);
        Assert.Equal(1, s.Number);
        Assert.Equal(Season.Epoch, s.Start);
    }

    [Fact]
    public void Current_ClampsNonPositiveLength()
    {
        var s = Season.Current(Season.Epoch.AddDays(3), 0);   // length clamped to 1 day → day 3 = season 4
        Assert.Equal(4, s.Number);
    }
}
