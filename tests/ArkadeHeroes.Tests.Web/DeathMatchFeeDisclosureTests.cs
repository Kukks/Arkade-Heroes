using ArkadeHeroes.Shared;
using ArkadeHeroes.Web.Pages;
using Bunit;

namespace ArkadeHeroes.Tests.Web;

/// <summary>The death-match burns a hero forever and charges the highest fee in the game. It was the last
/// paid action quoting nothing, so this pins that the player is told the number before committing.</summary>
public class DeathMatchFeeDisclosureTests
{
    private const string Mine = "hero-mine";
    private const string Theirs = "hero-theirs";

    private static PageTestContext Arena()
    {
        var ctx = new PageTestContext();
        ctx.SignIn(balanceSats: 100_000);
        ctx.Api.Get("/api/heroes/mine", new[] { Fixtures.Hero(Mine, "Ashen Vigil") });
        ctx.Api.Get("/api/deathmatch", Array.Empty<DeathMatchDto>());
        ctx.Api.Get($"/api/matchmaking/{Mine}", new[]
        {
            new OpponentSuggestionDto(
                Fixtures.Hero(Theirs, "Emberwake", ownerId: "player-2"), "player-2",
                LevelGap: 0, XpIfYouWin: 10, XpIfYouLose: 10),
        });
        ctx.Api.Get("/api/chain/info", Fixtures.ChainInfo());
        return ctx;
    }

    private static IRenderedComponent<DeathMatch> AtTheConfirm(PageTestContext ctx)
    {
        var cut = ctx.Render<DeathMatch>();
        // The roster picker gates everything: matchmaking only runs from @bind:after="OnHeroPicked".
        cut.WaitForAssertion(() => Assert.Contains("Ashen Vigil", cut.Markup));
        cut.Find("select").Change(Mine);
        cut.WaitForAssertion(() => Assert.Contains("Emberwake", cut.Markup));
        cut.FindAll("button").First(b => b.TextContent.Contains("Emberwake", StringComparison.Ordinal)).Click();
        cut.FindAll("button").First(b => b.TextContent.Contains("Open death-match", StringComparison.Ordinal)).Click();
        return cut;
    }

    [Fact]
    public void TheFeeIsOnScreenBeforeTheHeroIsStaked()
    {
        // Fixture config: base 100 + 10/level at level 3 = 130, doubled for a classic death-match.
        using var ctx = Arena();

        var cut = AtTheConfirm(ctx);

        cut.WaitForAssertion(() => Assert.Contains("PERMADEATH", cut.Markup));
        Assert.Contains("260", cut.Markup);
        Assert.Contains("win or lose", cut.Markup);
    }

    [Fact]
    public void AnUnreadableConfigQuotesNothingRatherThanZero()
    {
        // Pricing's own rule: a confident "0 sat" on an action that charges is worse than no number.
        using var ctx = new PageTestContext();
        ctx.SignIn(balanceSats: 100_000);
        ctx.Api.Get("/api/heroes/mine", new[] { Fixtures.Hero(Mine, "Ashen Vigil") });
        ctx.Api.Get("/api/deathmatch", Array.Empty<DeathMatchDto>());
        ctx.Api.Get($"/api/matchmaking/{Mine}", new[]
        {
            new OpponentSuggestionDto(
                Fixtures.Hero(Theirs, "Emberwake", ownerId: "player-2"), "player-2", 0, 10, 10),
        });
        ctx.Api.GetFails("/api/chain/info", System.Net.HttpStatusCode.ServiceUnavailable);

        var cut = AtTheConfirm(ctx);

        cut.WaitForAssertion(() => Assert.Contains("PERMADEATH", cut.Markup));
        Assert.DoesNotContain("entry fee", cut.Markup);
        Assert.DoesNotContain("0 sats", cut.Markup);
    }
}
