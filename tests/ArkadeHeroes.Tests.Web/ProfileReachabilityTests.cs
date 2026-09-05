using ArkadeHeroes.Shared;
using ArkadeHeroes.Web.Pages;
using Bunit;

namespace ArkadeHeroes.Tests.Web;

/// <summary>
/// <c>PlayerProfileDto</c> exists because "a name on the leaderboard led nowhere" — its own words. The
/// endpoint, the SDK method and the DTO all shipped; no page ever rendered it and nothing ever linked to
/// it, so the gap it was written to close stayed open. These pin both halves: the page renders, and the
/// two places a player would look actually point at it.
/// </summary>
public class ProfileReachabilityTests
{
    private const string Them = "player-2";

    private static PlayerProfileDto Trophies() => new(
        PlayerId: Them,
        Name: "Kestrel",
        SeasonPass: new SeasonPassProgress(
            Points: 15, Tier: 1, PointsIntoTier: 5, PointsToNextTier: 5,
            Title: null, NextTitle: "Contender", MaxTier: 10),
        Achievements: new PlayerAchievementsDto(
            HeroesOwned: 4, HeroesBred: 3, Legendaries: 1, Fancies: 1, TournamentsWon: 2,
            Badges: ["First Blood"], FancySetsOwned: ["Emberline"],
            TraitAlbum: new Dictionary<string, int> { ["Aura"] = 2 },
            FancyEditions: [new FancyEditionDto("h-9", "Onyx Reaver Nyx", "Emberline", 3)]),
        Notable: [Fixtures.Hero("h-9", "Onyx Reaver Nyx", ownerId: Them)]);

    [Fact]
    public void TheTrophyCaseRendersWhatAPlayerWouldShowOff()
    {
        using var ctx = new PageTestContext();
        ctx.Api.Get($"/api/players/{Them}/profile", Trophies());

        var cut = ctx.Render<Profile>(p => p.Add(c => c.PlayerId, Them));

        cut.WaitForAssertion(() => Assert.Contains("Kestrel", cut.Markup));
        Assert.Contains("Contender", cut.Markup);          // what the next tier buys
        Assert.Contains("Emberline", cut.Markup);          // the fancy set, with its edition
        Assert.Contains("#3", cut.Markup);
        Assert.Contains("Onyx Reaver Nyx", cut.Markup);    // a notable hero
    }

    [Fact]
    public void AFailedReadSaysSo_RatherThanShowingAnEmptyCase()
    {
        // The page exists to say what someone HAS, so "we could not read it" must not render as "nothing".
        using var ctx = new PageTestContext();
        ctx.Api.GetFails($"/api/players/{Them}/profile");

        var cut = ctx.Render<Profile>(p => p.Add(c => c.PlayerId, Them));

        cut.WaitForAssertion(() => Assert.Contains("Couldn't load this player", cut.Markup));
        Assert.DoesNotContain("No heroes to show", cut.Markup);
    }

    [Fact]
    public void TheLeaderboardPointsAtBothTheHeroAndItsOwner()
    {
        using var ctx = new PageTestContext();
        ctx.Api.Get("/api/leaderboard", new[]
        {
            new LeaderboardEntryDto(1, "h-9", "Onyx Reaver Nyx", 4, 7, 9, Them),
        });

        var cut = ctx.Render<Ranks>();

        cut.WaitForAssertion(() => Assert.Contains("Onyx Reaver Nyx", cut.Markup));
        Assert.Contains($"players/{Them}", cut.Markup);
        Assert.Contains("heroes/h-9", cut.Markup);
    }
}
