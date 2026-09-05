using ArkadeHeroes.Shared;
using ArkadeHeroes.Web.Pages;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using ArkadeHeroes.Web.Wallet;

namespace ArkadeHeroes.Tests.Web;

/// <summary>Hero transfer has had a server endpoint and an SDK method all along, and the public player
/// lookup exists expressly to serve it. Nothing in the browser called it — a hero could be sold, not given.</summary>
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
    public void ChangingHero_ClosesAnOpenGiftForm()
    {
        const string other = "hero-other";
        using var ctx = HeroPage();
        ctx.Api.Get("/api/players/player-2", Fixtures.Player(id: "player-2", name: "Brenna"));
        ctx.Api.GetFails($"/api/heroes/{other}/tombstone", System.Net.HttpStatusCode.NotFound);
        ctx.Api.Get($"/api/heroes/{other}", Fixtures.Hero(other, "Emberwake"));
        ctx.Api.Get($"/api/receipts/hero/{other}", Array.Empty<ProgressionReceiptDto>());
        ctx.Api.Get($"/api/heroes/{other}/timeline", new HeroTimelineDto(other, [], Complete: true, null));

        var cut = ctx.Render<HeroDetail>(p => p.Add(x => x.Id, HeroId));
        cut.WaitForAssertion(() => Assert.Contains("Ashfang", cut.Markup));
        Click(cut, "Gift");
        cut.Find("input[placeholder='recipient player id']").Input("player-2");
        Click(cut, "Check");
        cut.WaitForAssertion(() => Assert.Contains("Send to Brenna", cut.Markup));

        cut.Render(p => p.Add(x => x.Id, other));

        cut.WaitForAssertion(() => Assert.Contains("Emberwake", cut.Markup));
        Assert.DoesNotContain("Send to Brenna", cut.Markup);
    }

    [Fact]
    public void EditingTheIdAfterACheck_DisarmsSend()
    {
        using var ctx = HeroPage();
        ctx.Api.Get("/api/players/player-2", Fixtures.Player(id: "player-2", name: "Brenna"));
        var cut = Render(ctx);

        Click(cut, "Gift");
        cut.Find("input[placeholder='recipient player id']").Input("player-2");
        Click(cut, "Check");
        cut.WaitForAssertion(() => Assert.Contains("Send to Brenna", cut.Markup));

        cut.Find("input[placeholder='recipient player id']").Input("player-3");

        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Contains("Send to", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AHandoverAlreadyOnChain_IsFinishedWithoutSendingAgain()
    {
        // The recovery path: send landed, confirm timed out. Proven by the ABSENCE of a wallet here — any
        // attempt to send would throw before returning.
        using var ctx = HeroPage();
        ctx.Api.Post($"/api/heroes/{HeroId}/transfer", new TransferResponse(Fixtures.Hero(HeroId, "Ashfang", ownerId: "player-2")));

        var hero = await ctx.Services.GetRequiredService<GameSession>().GiftHeroAsync(HeroId, "player-2");

        Assert.Equal("player-2", hero.OwnerId);
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
