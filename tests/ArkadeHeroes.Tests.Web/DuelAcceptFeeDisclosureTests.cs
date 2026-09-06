using ArkadeHeroes.Shared;
using ArkadeHeroes.Web.Pages;
using Bunit;

namespace ArkadeHeroes.Tests.Web;

/// <summary>Accepting a duel stakes the wager AND pays a match fee, and the row showed only the wager.</summary>
public class DuelAcceptFeeDisclosureTests
{
    private const string Mine = "h-mine";
    private const string Spare = "h-spare";
    private const string Theirs = "h-theirs";

    private static PageTestContext Challenged(bool withConfig = true)
    {
        var ctx = new PageTestContext();
        ctx.SignIn(balanceSats: 100_000);
        ctx.Api.Get("/api/heroes/mine", new[]
        {
            Fixtures.Hero(Mine, "Ashen Vigil", level: 7),
            Fixtures.Hero(Spare, "Azure Warden Rook", level: 3),
        });
        ctx.Api.Get($"/api/heroes/{Theirs}", Fixtures.Hero(Theirs, "Direbloom", ownerId: "player-2"));
        if (withConfig) ctx.Api.Get("/api/chain/info", Fixtures.ChainInfo());
        ctx.Api.Get("/api/matches", new[]
        {
            new MatchDto("m-1", Theirs, Mine, "open", "00", Result: null, WagerSats: 1_000),
        });
        return ctx;
    }

    private static string AcceptRow(IRenderedComponent<Duel> cut)
    {
        cut.WaitForAssertion(() =>
            Assert.Contains(cut.FindAll("div.match-row"), e => e.TextContent.Contains("challenges your")));
        return cut.FindAll("div.match-row").Single(e => e.TextContent.Contains("challenges your")).TextContent;
    }

    [Fact]
    public void TheRowQuotesTheFeeAndTheTotal_PricedFromTheDefendersOwnHero()
    {
        using var ctx = Challenged();
        var row = AcceptRow(ctx.Render<Duel>(ps => ps.Add(p => p.PollEvery, TimeSpan.FromHours(1)))).Replace(",", "");

        // 100 + 10*7 on the test config. The spare hero is level 3 (130), so a fee taken off the wrong
        // hero of mine would still read as a plausible number — hence asserting its absence too.
        Assert.Contains("170 sat match fee", row);
        Assert.Contains("1170 sats to accept", row);
        Assert.DoesNotContain("130", row);
    }

    [Fact]
    public void WithoutTheConfigTheRowQuotesNothingRatherThanZero()
    {
        using var ctx = Challenged(withConfig: false);
        var row = AcceptRow(ctx.Render<Duel>(ps => ps.Add(p => p.PollEvery, TimeSpan.FromHours(1))));

        Assert.DoesNotContain("match fee", row);
        Assert.Contains("1,000", row);
    }
}
