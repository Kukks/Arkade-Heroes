using AngleSharp.Dom;
using ArkadeHeroes.Web.Pages;
using Bunit;

namespace ArkadeHeroes.Tests.Web;

/// <summary>Past the XP cap the drop is the only reward left, and the page said it "still applies" — it
/// needs a FULL clear, which a bought recruit achieves 0.0% of the time at every level.</summary>
public class GauntletXpCapAdviceTests
{
    private static PageTestContext Roster(int level)
    {
        var ctx = new PageTestContext();
        ctx.SignIn(balanceSats: 100_000);
        ctx.Api.Get("/api/heroes/mine", new[] { Fixtures.Hero("h-1", "Ashen Vigil", level: level) });
        return ctx;
    }

    private static IRenderedComponent<Gauntlet> Pick(PageTestContext ctx)
    {
        var cut = ctx.Render<Gauntlet>();
        cut.WaitForAssertion(() => Assert.Contains("Ashen Vigil", cut.Markup));
        cut.Find("select").Change("h-1");
        return cut;
    }

    [Fact]
    public void ACappedHeroIsToldTheDropNeedsAFullClear_NotJustThatItApplies()
    {
        using var ctx = Roster(level: 10);

        var cut = Pick(ctx);

        // Scoped to the advisory: the page's intro already says "A full clear wins a piece of gear"
        // unconditionally, so a whole-markup match passes with the old wording restored.
        var advice = cut.FindAll(".status-line")
            .First(e => e.TextContent.Contains("no XP", StringComparison.Ordinal));
        Assert.Contains("full clear", advice.TextContent, StringComparison.Ordinal);
        Assert.Contains("breeding", advice.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void AHeroStillUnderTheCapIsNotToldAnyOfThis()
    {
        using var ctx = Roster(level: 3);

        var cut = Pick(ctx);

        Assert.DoesNotContain("no XP", cut.Markup);
    }
}
