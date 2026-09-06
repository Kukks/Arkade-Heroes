using ArkadeHeroes.Shared;
using ArkadeHeroes.Web.Pages;
using Bunit;

namespace ArkadeHeroes.Tests.Web;

/// <summary>Trials reads the GENOME, not the level, and a bought hero is capped below the ghost it meets
/// at wave 1 — measured, a recruit clears nothing 97.5% of the time against a bred hero's 48.6%. The whole
/// starting cohort loses immediately, and the page said only that the score reads the hero's strength.</summary>
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
