using AngleSharp.Dom;
using ArkadeHeroes.Shared;
using ArkadeHeroes.Web.Pages;
using Bunit;

namespace ArkadeHeroes.Tests.Web;

/// <summary>Accepting a bid is what prices the marketplace fee the SELLER absorbs, so the bid figure alone
/// was never what the row offered — the true number first appeared on the next button, after the decision.</summary>
public class BidProceedsDisclosureTests
{
    private const string HeroId = "hero-mine";

    private static PageTestContext WithBid(long bidSats, long listingFeeSats = 100)
    {
        var ctx = new PageTestContext();
        ctx.SignIn();
        ctx.Api.GetFails($"/api/heroes/{HeroId}/tombstone", System.Net.HttpStatusCode.NotFound);
        ctx.Api.Get($"/api/heroes/{HeroId}", Fixtures.Hero(HeroId, "Ashfang"));
        ctx.Api.Get($"/api/receipts/hero/{HeroId}", Array.Empty<ProgressionReceiptDto>());
        ctx.Api.Get("/api/chain/info",
            Fixtures.ChainInfo() with { Config = Fixtures.Config() with { OfferListingFeeSats = listingFeeSats } });
        ctx.Api.Get($"/api/heroes/{HeroId}/timeline", new HeroTimelineDto(HeroId, [], Complete: true, null));
        ctx.Api.Get("/api/bids", new[]
        {
            new BidDto("bid-1", HeroId, "player-2", "player-1", bidSats, 0, "proposed", 0),
        });
        ctx.Api.Get("/api/items/mine", new Dictionary<string, long>());
        ctx.Api.Get("/api/items", Array.Empty<ItemDto>());
        return ctx;
    }

    private static IRenderedComponent<HeroDetail> Render(PageTestContext ctx)
    {
        var cut = ctx.Render<HeroDetail>(p => p.Add(x => x.Id, HeroId));
        cut.WaitForAssertion(() => Assert.Contains("Offers on Ashfang", cut.Markup));
        return cut;
    }

    private static IElement Accept(IRenderedComponent<HeroDetail> cut) =>
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Accept");

    [Fact]
    public void TheRowStatesWhatTheOwnerActuallyCollects()
    {
        using var ctx = WithBid(1_000);
        var cut = Render(ctx);

        Assert.Contains("you collect", cut.Markup);
        Assert.Contains("900", cut.Markup);
        Assert.False(Accept(cut).HasAttribute("disabled"));
    }

    [Fact]
    public void ABidUnderTheFeeSaysSoAndCannotBeAccepted()
    {
        // MarketplaceFeeFor THROWS at or below the fee, so this Accept could only ever fail. Sell.razor
        // already refuses the mirror case at listing time; the bid row let the owner press it and find out.
        using var ctx = WithBid(100);
        var cut = Render(ctx);

        Assert.Contains("under the 100 sat marketplace fee", cut.Markup);
        Assert.True(Accept(cut).HasAttribute("disabled"));
    }

    [Fact]
    public void NoFeeConfiguredQuotesNothingAndBlocksNothing()
    {
        // The same zero the page sees before chain info lands. It must not become "you collect 1,000" on
        // faith, nor disable an Accept that would have worked.
        using var ctx = WithBid(1_000, listingFeeSats: 0);
        var cut = Render(ctx);

        Assert.DoesNotContain("you collect", cut.Markup);
        Assert.DoesNotContain("marketplace fee", cut.Markup);
        Assert.False(Accept(cut).HasAttribute("disabled"));
    }
}
