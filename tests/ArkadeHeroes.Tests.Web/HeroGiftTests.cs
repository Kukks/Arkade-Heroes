using ArkadeHeroes.Shared;
using ArkadeHeroes.Web.Pages;
using Bunit;

namespace ArkadeHeroes.Tests.Web;

/// <summary>
/// Hero transfer has had a server endpoint and an SDK method all along, and the public player lookup exists
/// expressly to serve it (<c>Program.cs:283</c>). Nothing in the browser called it — so a hero could be sold
/// but not given.
/// </summary>
public class HeroGiftTests
{
    private const string HeroId = "hero-mine";

    private static PageTestContext HeroPage(HeroDto? hero = null)
    {
        var ctx = new PageTestContext();
        ctx.SignIn();
        ctx.Api.GetFails($"/api/heroes/{HeroId}/tombstone", System.Net.HttpStatusCode.NotFound);
        ctx.Api.Get($"/api/heroes/{HeroId}", hero ?? Fixtures.Hero(HeroId, "Ashfang"));
        ctx.Api.Get($"/api/receipts/hero/{HeroId}", Array.Empty<ProgressionReceiptDto>());
        ctx.Api.Get("/api/chain/info", Fixtures.ChainInfo());
        ctx.Api.Get($"/api/heroes/{HeroId}/timeline", new HeroTimelineDto(HeroId, [], Complete: true, null));
        ctx.Api.Get("/api/bids", Array.Empty<BidDto>());
        ctx.Api.Get("/api/items/mine", new Dictionary<string, long>());
        ctx.Api.Get("/api/items", Array.Empty<ItemDto>());
        return ctx;
    }

    private static IRenderedComponent<HeroDetail> Render(PageTestContext ctx)
    {
        var cut = ctx.Render<HeroDetail>(p => p.Add(x => x.Id, HeroId));
        cut.WaitForAssertion(() => Assert.Contains("Ashfang", cut.Markup));
        return cut;
    }

    private static void Click(IRenderedComponent<HeroDetail> cut, string label) =>
        cut.FindAll("button").First(b => b.TextContent.Contains(label, StringComparison.Ordinal)).Click();

    [Fact]
    public void YourOwnHero_OffersAWayToGiveItAway()
    {
        using var ctx = HeroPage();
        Assert.Contains("Gift", Render(ctx).Markup);
    }

    [Fact]
    public void SomeoneElsesHero_DoesNot()
    {
        using var ctx = HeroPage(Fixtures.Hero(HeroId, "Ashfang", ownerId: "someone-else"));
        Assert.DoesNotContain("Gift", Render(ctx).Markup);
    }

    [Fact]
    public void TheRecipientMustBeResolvedBeforeAnythingCanBeSent()
    {
        // The safety property: an unchecked id is not sendable — Send does not exist until the id resolves.
        using var ctx = HeroPage();
        ctx.Api.Get("/api/players/player-2", Fixtures.Player(id: "player-2", name: "Brenna"));
        var cut = Render(ctx);

        Click(cut, "Gift");
        cut.Find("input[placeholder='recipient player id']").Input("player-2");

        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Contains("Send to", StringComparison.Ordinal));
    }

    [Fact]
    public void ResolvingShowsTheNameBeforeTheHeroMoves()
    {
        using var ctx = HeroPage();
        ctx.Api.Get("/api/players/player-2", Fixtures.Player(id: "player-2", name: "Brenna"));
        var cut = Render(ctx);

        Click(cut, "Gift");
        cut.Find("input[placeholder='recipient player id']").Input("player-2");
        Click(cut, "Check");

        cut.WaitForAssertion(() => Assert.Contains("Send to Brenna", cut.Markup));
    }

    [Fact]
    public void AnUnknownRecipient_NeverBecomesSendable()
    {
        using var ctx = HeroPage();
        ctx.Api.GetFails("/api/players/nobody", System.Net.HttpStatusCode.NotFound);
        var cut = Render(ctx);

        Click(cut, "Gift");
        cut.Find("input[placeholder='recipient player id']").Input("nobody");
        Click(cut, "Check");

        cut.WaitForAssertion(() =>
            Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Contains("Send to", StringComparison.Ordinal)));
    }

    [Fact]
    public void TheGearWarningIsShown_BecauseTheLoadoutDoesNotTravel()
    {
        using var ctx = HeroPage();
        var cut = Render(ctx);

        Click(cut, "Gift");
        Assert.Contains("Equipped gear stays with you", cut.Markup);
    }
}
