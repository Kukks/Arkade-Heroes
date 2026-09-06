using ArkadeHeroes.Shared;
using ArkadeHeroes.Web.Pages;
using Bunit;

namespace ArkadeHeroes.Tests.Web;

/// <summary>A bought hero is capped below the ghost it meets at wave 1, so the whole starting cohort loses
/// there — measured zero-wave 97.5% for a recruit against 48.6% for a bred hero, a gap worth about twenty
/// levels. The page said only that the score reads the hero's strength, which left that unexplained.</summary>
public class TrialsRecruitGuidanceTests
{
    private static PageTestContext Roster(params HeroDto[] heroes)
    {
        var ctx = new PageTestContext();
        ctx.SignIn(balanceSats: 100_000);
        ctx.Api.Get("/api/heroes/mine", heroes);
        return ctx;
    }

    private static HeroDto Recruit(string id, string name) => Fixtures.Hero(id, name);
    private static HeroDto Bred(string id, string name) => Fixtures.Hero(id, name) with { Generation = 2 };

    private static IRenderedComponent<Trials> Open(PageTestContext ctx, string heroId)
    {
        var cut = ctx.Render<Trials>();
        cut.WaitForAssertion(() => Assert.Contains("Enter the Trials", cut.Markup));
        cut.Find("select").Change(heroId);
        return cut;
    }

    [Fact]
    public void ARecruitIsToldWhyItWillLose_AndWhatMovesTheNeedle()
    {
        using var ctx = Roster(Recruit("h-1", "Ashen Vigil"));

        var cut = Open(ctx, "h-1");

        Assert.Contains("weakest heroes in the game", cut.Markup);
        Assert.Contains("href=\"breed\"", cut.Markup);
    }

    [Fact]
    public void ABredHeroIsNotNagged()
    {
        using var ctx = Roster(Bred("h-2", "Emberwake"));

        var cut = Open(ctx, "h-2");

        Assert.DoesNotContain("weakest heroes in the game", cut.Markup);
    }

    [Fact]
    public void NothingIsClaimedBeforeAHeroIsPicked()
    {
        using var ctx = Roster(Recruit("h-1", "Ashen Vigil"));

        var cut = ctx.Render<Trials>();

        cut.WaitForAssertion(() => Assert.Contains("Enter the Trials", cut.Markup));
        Assert.DoesNotContain("weakest heroes in the game", cut.Markup);
    }
}
