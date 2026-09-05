using AngleSharp.Dom;
using ArkadeHeroes.Shared;
using ArkadeHeroes.Web.Pages;
using Bunit;

namespace ArkadeHeroes.Tests.Web;

/// <summary>
/// The gauntlet cooldown was enforced by the server and persisted across restarts, but never put on the
/// wire — so the only way to learn a hero was resting was to press Run and take the refusal. It is the
/// most-hit wall of a new player's first session. <c>BreedCooldownUntil</c> was always on
/// <see cref="HeroDto"/> and rendered; this pins its missing twin.
/// </summary>
public class GauntletCooldownTests
{
    private static PageTestContext Roster(TimeSpan? cooldownLeft)
    {
        var hero = Fixtures.Hero("h-1", "Ashen Vigil") with
        {
            GauntletCooldownUntil = cooldownLeft is { } left ? DateTimeOffset.UtcNow + left : null,
        };
        var ctx = new PageTestContext();
        ctx.SignIn(balanceSats: 100_000);
        ctx.Api.Get("/api/heroes/mine", new[] { hero });
        return ctx;
    }

    private static IElement RunButton(IRenderedComponent<Gauntlet> cut) =>
        cut.FindAll("button").First(b => b.TextContent.Contains("gauntlet", StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void ARestingHeroCannotBeSentBackIn()
    {
        using var ctx = Roster(TimeSpan.FromMinutes(3));
        var cut = ctx.Render<Gauntlet>();
        cut.WaitForAssertion(() => Assert.Contains("Ashen Vigil", cut.Markup));
        cut.Find("select").Change("h-1");

        Assert.Contains("resting", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.True(RunButton(cut).HasAttribute("disabled"));
    }

    [Fact]
    public void ACooldownThatHasAlreadyLapsedReadsAsReady()
    {
        // A stale roster read must not keep the button shut once the rest is over.
        using var ctx = Roster(TimeSpan.FromSeconds(-1));
        var cut = ctx.Render<Gauntlet>();
        cut.WaitForAssertion(() => Assert.Contains("Ashen Vigil", cut.Markup));
        cut.Find("select").Change("h-1");

        Assert.DoesNotContain("resting", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.False(RunButton(cut).HasAttribute("disabled"));
    }

    [Fact]
    public void AHeroThatHasNeverRunIsReady()
    {
        using var ctx = Roster(null);
        var cut = ctx.Render<Gauntlet>();
        cut.WaitForAssertion(() => Assert.Contains("Ashen Vigil", cut.Markup));
        cut.Find("select").Change("h-1");

        Assert.DoesNotContain("resting", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.False(RunButton(cut).HasAttribute("disabled"));
    }
}
