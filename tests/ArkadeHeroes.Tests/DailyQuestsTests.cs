using ArkadeHeroes.Shared;

namespace ArkadeHeroes.Tests;

/// <summary>The daily quest catalog: deterministic per-day selection, and completion derived purely
/// from the in-window receipt log (the daily analogue of LeaderboardBuilder — no server trust).</summary>
public class DailyQuestsTests
{
    static ProgressionReceiptDto R(string type, string? winner, string a = "hero-a", string b = "hero-b") =>
        new(type, "id-" + Guid.NewGuid().ToString("N"), a, b, winner, "", "n", "", 0, 0, 1, 1, 0, "", "");

    [Fact]
    public void ForDay_ReturnsCount_StableWithinDay_RotatesAcrossDays()
    {
        var d1 = DailyQuests.ForDay(1, 3);
        Assert.Equal(3, d1.Count);
        Assert.Equal(3, d1.Select(q => q.Id).Distinct().Count());     // no dupes within a day
        Assert.Equal(d1.Select(q => q.Id), DailyQuests.ForDay(1, 3).Select(q => q.Id)); // stable
        Assert.NotEqual(d1.Select(q => q.Id), DailyQuests.ForDay(2, 3).Select(q => q.Id)); // rotates
    }

    [Fact]
    public void IsComplete_WinnersOnly_TrueForWinner_FalseForLoser()
    {
        var duel = DailyQuests.Catalog.First(q => q.Id == "duel-win");
        var mine = new HashSet<string> { "hero-a" };
        Assert.True(DailyQuests.IsComplete(duel, new[] { R("match", "hero-a") }, mine));
        Assert.False(DailyQuests.IsComplete(duel, new[] { R("match", "hero-b") }, mine)); // I lost
    }

    [Fact]
    public void IsComplete_WrongTypeOrEmpty_False()
    {
        var breed = DailyQuests.Catalog.First(q => q.Id == "breed");
        var mine = new HashSet<string> { "hero-a" };
        Assert.False(DailyQuests.IsComplete(breed, new[] { R("match", "hero-a") }, mine));
        Assert.False(DailyQuests.IsComplete(breed, Array.Empty<ProgressionReceiptDto>(), mine));
    }

    [Fact]
    public void IsComplete_Breed_NotWinnersOnly_TrueOnParticipation()
    {
        var breed = DailyQuests.Catalog.First(q => q.Id == "breed");
        Assert.True(DailyQuests.IsComplete(breed, new[] { R("breeding", null, "hero-a") },
            new HashSet<string> { "hero-a" }));
    }

    /// <summary>A merge BURNS both inputs before the receipt is written (GameService removes them from the
    /// roster, then issues the receipt naming them as HeroA/HeroB). The fused hero — the only id still in the
    /// player's hands — is the ResultHeroId, so participation has to be readable there or the quest, and the
    /// season-pass points behind it, can never be earned by anyone.</summary>
    [Fact]
    public void IsComplete_Merge_TrueForTheFusedHero_TheOnlyInputThatSurvives()
    {
        var merge = DailyQuests.Catalog.First(q => q.Id == "merge");
        var afterTheBurn = new HashSet<string> { "hero-fused" };

        Assert.True(DailyQuests.IsComplete(
            merge, new[] { R("merge", "hero-fused", "hero-base", "hero-sacrifice") }, afterTheBurn));
    }

    [Fact]
    public void IsComplete_Merge_FalseForABystander()
    {
        var merge = DailyQuests.Catalog.First(q => q.Id == "merge");
        Assert.False(DailyQuests.IsComplete(
            merge, new[] { R("merge", "hero-fused", "hero-base", "hero-sacrifice") },
            new HashSet<string> { "someone-elses-hero" }));
    }

    [Fact]
    public void IsComplete_DeathMatch_AcceptsAbsorbReceipt()
    {
        var dm = DailyQuests.Catalog.First(q => q.Id == "deathmatch");
        Assert.True(DailyQuests.IsComplete(dm, new[] { R("absorb", "hero-a") },
            new HashSet<string> { "hero-a" }));
    }
}
