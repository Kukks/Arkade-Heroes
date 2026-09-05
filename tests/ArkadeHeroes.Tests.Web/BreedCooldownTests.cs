using AngleSharp.Dom;
using ArkadeHeroes.Shared;
using ArkadeHeroes.Web.Pages;
using Bunit;

namespace ArkadeHeroes.Tests.Web;

/// <summary>
/// Breeding cooldown doubles with every breed and is gene-scaled on top, so on a well-used pair it is the
/// wall a player hits far more often than the fee — and the page read only sterility, so the only way to
/// find it was to press Breed and take the refusal. The same defect as the gauntlet cooldown, on a field
/// that was already on the wire.
/// </summary>
public class BreedCooldownTests
{
    private static PageTestContext Pair(TimeSpan? cooldownOnFirst)
    {
        var a = Fixtures.Hero("h-1", "Crimson Vanguard Vale") with
        {
            BreedCooldownUntil = cooldownOnFirst is { } left ? DateTimeOffset.UtcNow + left : null,
        };
        var b = Fixtures.Hero("h-2", "Azure Warden Rook");

        var ctx = new PageTestContext();
        ctx.SignIn(balanceSats: 100_000);
        ctx.Api.Get("/api/heroes/mine", new[] { a, b });
        return ctx;
    }

    private static IRenderedComponent<Breed> PickBoth(PageTestContext ctx)
    {
        var cut = ctx.Render<Breed>();
        cut.WaitForAssertion(() => Assert.Contains("Crimson Vanguard Vale", cut.Markup));
        // Re-queried between changes: picking parent A re-renders (the pairing fee appears), which
        // invalidates the handler id bUnit captured for parent B.
        cut.FindAll("select")[0].Change("h-1");
        cut.FindAll("select")[1].Change("h-2");
        return cut;
    }

    private static IElement BreedButton(IRenderedComponent<Breed> cut) =>
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Breed");

    [Fact]
    public void APairWithARestingParentCannotBreed()
    {
        using var ctx = Pair(TimeSpan.FromMinutes(40));
        var cut = PickBoth(ctx);

        Assert.Contains("recovering from its last breeding", cut.Markup);
        Assert.True(BreedButton(cut).HasAttribute("disabled"));
    }

    [Fact]
    public void ARestingHeroIsStillListed_NotFilteredOutOfTheRoster()
    {
        // Hiding it would drop _fertile below two and render "you need at least two fertile heroes --
        // recruit one", which is advice that costs sats to follow.
        using var ctx = Pair(TimeSpan.FromMinutes(40));
        var cut = ctx.Render<Breed>();
        cut.WaitForAssertion(() => Assert.Contains("Crimson Vanguard Vale", cut.Markup));

        Assert.DoesNotContain("at least two fertile heroes", cut.Markup);
        Assert.Contains("resting", cut.Markup);
    }

    [Fact]
    public void ACooldownThatHasAlreadyLapsedReadsAsReady()
    {
        using var ctx = Pair(TimeSpan.FromSeconds(-1));
        var cut = PickBoth(ctx);

        Assert.DoesNotContain("recovering from its last breeding", cut.Markup);
        Assert.False(BreedButton(cut).HasAttribute("disabled"));
    }

    [Fact]
    public void APairThatHasNeverBredIsReady()
    {
        using var ctx = Pair(null);
        var cut = PickBoth(ctx);

        Assert.DoesNotContain("recovering from its last breeding", cut.Markup);
        Assert.False(BreedButton(cut).HasAttribute("disabled"));
    }
}
