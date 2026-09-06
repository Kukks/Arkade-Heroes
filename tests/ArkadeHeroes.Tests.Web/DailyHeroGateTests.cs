using ArkadeHeroes.Shared;
using ArkadeHeroes.Web.Components;
using Bunit;

namespace ArkadeHeroes.Tests.Web;

/// <summary>The daily claim is refused to a wallet holding no heroes (GameService.cs:3220), and the card
/// offered the button anyway — so the very first thing a new player pressed came back a refusal.</summary>
public class DailyHeroGateTests
{
    private static PageTestContext Card(bool hasHero)
    {
        var ctx = new PageTestContext();
        ctx.SignIn();
        ctx.Api.Get("/api/chain/info",
            Fixtures.ChainInfo() with { Config = Fixtures.Config() with { DailyRewardEnabled = true } });
        ctx.Api.Get("/api/daily", new DailyStatusDto(
            DayIndex: 248, DayEndsUnix: 1_800_000_000, ClaimedToday: false, Streak: 0,
            BaseSats: 50, Quests: [], ClaimableNowSats: 50, ProjectedSats: 50, HasHero: hasHero));
        ctx.Api.Get("/api/players/season-pass", new SeasonPassProgress(0, 0, 0, 0, null, null, 0));
        return ctx;
    }

    [Fact]
    public void WithNoHero_TheClaimButtonIsNotOffered_AndTheCardSaysWhy()
    {
        using var ctx = Card(hasHero: false);
        var cut = ctx.Render<DailyCard>();

        cut.WaitForAssertion(() => Assert.Contains("daily reward goes to players", cut.Markup));
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Contains("Claim "));
    }

    [Fact]
    public void WithAHero_TheButtonIsBackAndQuotesTheAmount()
    {
        using var ctx = Card(hasHero: true);
        var cut = ctx.Render<DailyCard>();

        cut.WaitForAssertion(() =>
            Assert.Contains(cut.FindAll("button"), b => b.TextContent.Contains("Claim 50 sat")));
        Assert.DoesNotContain("daily reward goes to players", cut.Markup);
    }
}
