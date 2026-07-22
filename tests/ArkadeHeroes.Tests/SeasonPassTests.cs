using ArkadeHeroes.Shared;

namespace ArkadeHeroes.Tests;

/// <summary>
/// The season pass: a season-long goal scored from the same deeds the daily quests recognise, counted
/// across the window instead of asked once a day. Pays TITLES, never sats — the daily faucet is only
/// solvent because its rewards are gated behind fee-paying actions, and a season-long sats reward would be
/// a second, ungated faucet.
/// </summary>
public class SeasonPassTests
{
    private const string Mine = "hero-mine";
    private static readonly HashSet<string> MyHeroes = [Mine];

    private static ProgressionReceiptDto Receipt(string type, string? winner = null, string heroA = Mine) =>
        new(type, Guid.NewGuid().ToString("N"), heroA, "hero-other", winner,
            "seed", "nonce", "commit", 0, 0, 1, 1, 0, "", "");

    [Fact]
    public void NoDeeds_IsTierZeroWithNoTitle()
    {
        var p = SeasonPass.Progress([], MyHeroes);
        Assert.Equal(0, p.Points);
        Assert.Equal(0, p.Tier);
        Assert.Null(p.Title);
        Assert.Equal("Contender", p.NextTitle);   // the board still shows what's next
    }

    [Fact]
    public void WinnersOnlyDeedsPayMoreThanUnconditionalOnes()
    {
        // A duel WON is harder than a breed, so it's worth more — the pass shouldn't reward pure churn.
        var won = SeasonPass.Progress([Receipt("match", winner: Mine)], MyHeroes);
        var bred = SeasonPass.Progress([Receipt("breeding")], MyHeroes);
        Assert.True(won.Points > bred.Points, $"a won duel ({won.Points}) should beat a breed ({bred.Points})");
    }

    [Fact]
    public void ALostDuelScoresNothing_ButTheBreedStillCounts()
    {
        // WinnersOnly: the player's hero must be the result. A loss earns no pass credit.
        Assert.Equal(0, SeasonPass.Progress([Receipt("match", winner: "hero-other")], MyHeroes).Points);
        Assert.True(SeasonPass.Progress([Receipt("breeding")], MyHeroes).Points > 0);
    }

    [Fact]
    public void AnAbsorbCountsAsADeathMatchWin()
    {
        // A death-match won via trait absorb issues an "absorb" receipt, not "deathmatch" — the pass shares
        // the daily's matcher precisely so this aliasing can't drift apart.
        Assert.True(SeasonPass.Progress([Receipt("absorb", winner: Mine)], MyHeroes).Points > 0);
    }

    [Fact]
    public void TiersAndTitlesUnlockAsPointsAccrue()
    {
        // 5 won duels x 3 points = 15 → tier 1 (10/tier), still short of Contender at tier 3.
        var five = SeasonPass.Progress(Enumerable.Range(0, 5).Select(_ => Receipt("match", winner: Mine)), MyHeroes);
        Assert.Equal(15, five.Points);
        Assert.Equal(1, five.Tier);
        Assert.Null(five.Title);

        // 10 won duels = 30 points → tier 3 → Contender.
        var ten = SeasonPass.Progress(Enumerable.Range(0, 10).Select(_ => Receipt("match", winner: Mine)), MyHeroes);
        Assert.Equal(3, ten.Tier);
        Assert.Equal("Contender", ten.Title);
        Assert.Equal("Season Veteran", ten.NextTitle);
    }

    [Fact]
    public void ThePassCapsOut_AndReadsAsFinishedRatherThanReset()
    {
        // Far past the cap: points clamp, the tier stops, and the bar shows full instead of wrapping to 0.
        var maxed = SeasonPass.Progress(Enumerable.Range(0, 500).Select(_ => Receipt("match", winner: Mine)), MyHeroes);
        Assert.Equal(SeasonPass.MaxTier * SeasonPass.PointsPerTier, maxed.Points);
        Assert.Equal(SeasonPass.MaxTier, maxed.Tier);
        Assert.Equal("Season Sovereign", maxed.Title);
        Assert.Null(maxed.NextTitle);
        Assert.Equal(SeasonPass.PointsPerTier, maxed.PointsIntoTier);
        Assert.Equal(0, maxed.PointsToNextTier);
    }

    [Fact]
    public void SomeoneElsesDeedsDoNotScore()
    {
        var theirs = Receipt("breeding", heroA: "not-mine");
        Assert.Equal(0, SeasonPass.Progress([theirs], MyHeroes).Points);
    }
}
