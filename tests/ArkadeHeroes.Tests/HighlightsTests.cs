using ArkadeHeroes.Shared;

namespace ArkadeHeroes.Tests;

/// <summary>
/// The spectator feed: which resolved fights were worth watching. Pure over public match + hero data, so a
/// client holding the same inputs recomputes the same feed — the server's pick carries no trust of its own.
/// </summary>
public class HighlightsTests
{
    private static MatchDto Fight(
        string id, string winner, string loser, long wager = 0, int winnerHp = 50, int winnerMaxHp = 100) =>
        new(id, winner, loser, "resolved", "commit",
            new BattleResultDto(winner, loser, 10, [], winnerHp, winnerMaxHp), wager);

    private static Dictionary<string, HighlightHero> Roster(params (string Id, string Name, int Level, bool Prized)[] hs) =>
        hs.ToDictionary(h => h.Id, h => new HighlightHero(h.Name, h.Level, h.Prized));

    [Fact]
    public void AnUnremarkableFightIsNotAHighlight()
    {
        // Even levels, no wager, an ordinary finish — padding the feed with these would make it worthless
        // as a "come watch this" surface, so it's dropped rather than ranked last.
        var roster = Roster(("a", "Ava", 10, false), ("b", "Bo", 10, false));
        Assert.Empty(HighlightsBuilder.Build([Fight("m1", "a", "b")], roster));
    }

    [Fact]
    public void PunchingUpOutranksAPlainBigPot()
    {
        var roster = Roster(("a", "Ava", 3, false), ("b", "Bo", 15, false), ("c", "Cy", 10, false), ("d", "Di", 10, false));
        var feed = HighlightsBuilder.Build(
        [
            Fight("upset", "a", "b"),               // 12 levels down → 120
            Fight("rich", "c", "d", wager: 5_000),  // 5,000 sat → 10
        ], roster);

        Assert.Equal("upset", feed[0].MatchId);
        Assert.Contains("12 levels down", feed[0].Reason);
        Assert.Equal(2, feed.Count);
    }

    [Fact]
    public void HowItEndedCounts_FlawlessAndNearDeath()
    {
        var roster = Roster(("a", "Ava", 10, false), ("b", "Bo", 10, false));

        var flawless = HighlightsBuilder.Build([Fight("f", "a", "b", winnerHp: 100, winnerMaxHp: 100)], roster);
        Assert.Contains("flawless", flawless.Single().Reason);

        var fumes = HighlightsBuilder.Build([Fight("n", "a", "b", winnerHp: 4, winnerMaxHp: 100)], roster);
        Assert.Contains("fumes", fumes.Single().Reason);
    }

    [Fact]
    public void APrizedHeroMakesAnOtherwisePlainFightWatchable()
    {
        var roster = Roster(("a", "Ava", 10, true), ("b", "Bo", 10, false));
        Assert.Contains("prized", HighlightsBuilder.Build([Fight("m", "a", "b")], roster).Single().Reason);
    }

    [Fact]
    public void FightsWhoseHeroesAreGone_AreSkipped()
    {
        // A death-match burns the loser and a merge consumes its inputs, so a past fight can reference a
        // hero that no longer exists. Skip rather than throw.
        var roster = Roster(("a", "Ava", 10, false));
        Assert.Empty(HighlightsBuilder.Build([Fight("m", "a", "ghost", wager: 9_000)], roster));
    }

    [Fact]
    public void OrderIsDeterministicAndRespectsTake()
    {
        var roster = Roster(("a", "Ava", 5, false), ("b", "Bo", 9, false));
        // Identical notability → the tiebreak is the match id, so the feed can't shuffle between reads.
        var feed = HighlightsBuilder.Build([Fight("m2", "a", "b"), Fight("m1", "a", "b")], roster, take: 1);
        Assert.Equal("m1", Assert.Single(feed).MatchId);
    }
}
