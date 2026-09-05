using ArkadeHeroes.Shared;
using ArkadeHeroes.Web.Pages;
using Bunit;

namespace ArkadeHeroes.Tests.Web;

/// <summary>
/// The item half of the marketplace shipped on the server and in the SDK and was unreachable from the
/// browser: nothing called <c>Offers.CreateItemAsync</c>, and Market gated Buy on <c>Kind == "hero"</c>, so
/// item offers rendered as tiles a player could look at and not buy.
/// </summary>
public class ItemMarketplaceTests
{
    private static ItemDto Blade() => new(
        Id: "rusty-blade", Name: "Rusty Blade", Slot: "Weapon",
        MaxHp: 0, Attack: 3, Magic: 0, Defense: 0, Speed: 0, CritPercent: 0,
        PriceSats: 500);

    private static OfferDto ItemOffer(string sellerId) => new(
        OfferId: "offer-1", SellerId: sellerId, ItemId: "rusty-blade", ItemName: "Rusty Blade",
        AskSats: 400, OfferAddress: "tark1qofferaddress", ItemAssetId: "asset-1",
        OfferValueSats: 0, RefundAfterUnixSeconds: 0, Status: "active", Kind: "item");

    private static PageTestContext Shop(long unitsHeld)
    {
        var ctx = new PageTestContext();
        ctx.SignIn(balanceSats: 100_000);
        ctx.Api.Get("/api/items", new[] { Blade() });
        ctx.Api.Get("/api/items/mine",
            unitsHeld > 0 ? new Dictionary<string, long> { ["rusty-blade"] = unitsHeld } : new());
        return ctx;
    }

    private static PageTestContext Stall(string sellerId)
    {
        var ctx = new PageTestContext();
        ctx.SignIn(balanceSats: 100_000);
        ctx.Api.Get("/api/offers", new[] { ItemOffer(sellerId) });
        ctx.Api.Get("/api/offers/sold?take=4", Array.Empty<OfferDto>());
        return ctx;
    }

    private static bool HasBuyButton(IRenderedComponent<Market> cut) =>
        cut.FindAll("button.buy-btn").Count > 0;

    [Fact]
    public void ARestingItemOffer_CanBeBought()
    {
        using var ctx = Stall(sellerId: "someone-else");
        var cut = ctx.Render<Market>();

        cut.WaitForAssertion(() => Assert.Contains("Rusty Blade", cut.Markup));
        Assert.True(HasBuyButton(cut), "an item rests under the same offer covenant a hero does");
    }

    [Fact]
    public void YourOwnItemOffer_IsStillNotBuyableByYou()
    {
        // The gate was widened from "heroes only" to "either kind" — it must not have lost the rest of
        // its conditions along the way.
        using var ctx = Stall(sellerId: "player-1");
        var cut = ctx.Render<Market>();

        cut.WaitForAssertion(() => Assert.Contains("Rusty Blade", cut.Markup));
        Assert.False(HasBuyButton(cut), "buying your own listing would just pay yourself the fee");
    }

    [Fact]
    public void ASpareUnit_CanBeListedForSale()
    {
        using var ctx = Shop(unitsHeld: 2);
        var cut = ctx.Render<Gear>();

        cut.WaitForAssertion(() => Assert.Contains("owned ×2", cut.Markup));
        Assert.Contains("Sell a spare", cut.Markup);
    }

    [Fact]
    public void OwningNone_OffersNothingToSell()
    {
        using var ctx = Shop(unitsHeld: 0);
        var cut = ctx.Render<Gear>();

        cut.WaitForAssertion(() => Assert.Contains("Rusty Blade", cut.Markup));
        Assert.DoesNotContain("Sell a spare", cut.Markup);
    }

    [Fact]
    public void AFailedListing_RereadsWhatYouActuallyStillHold()
    {
        // The deposit lands before the offer is polled, so a throw does not mean the unit is still yours.
        // Leaving the count alone would show an escrowed unit as equippable — the stale-count lie #244 fixed.
        using var ctx = Shop(unitsHeld: 2);
        var cut = ctx.Render<Gear>();
        cut.WaitForAssertion(() => Assert.Contains("Sell a spare", cut.Markup));

        cut.FindAll("button").First(b => b.TextContent.Contains("Sell a spare", StringComparison.Ordinal)).Click();
        ctx.Api.Requested.Clear();
        cut.FindAll("button").First(b => b.TextContent.Contains("List it", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() =>
            Assert.Contains(ctx.Api.Requested, r => r.EndsWith("/api/items/mine", StringComparison.Ordinal)));
    }

    [Fact]
    public void TheAskStartsAtTheCatalogPrice()
    {
        // The one number a seller and a buyer both already know; a blank field invites a mis-typed ask.
        using var ctx = Shop(unitsHeld: 1);
        var cut = ctx.Render<Gear>();

        cut.WaitForAssertion(() => Assert.Contains("Sell a spare", cut.Markup));
        cut.FindAll("button").First(b => b.TextContent.Contains("Sell a spare", StringComparison.Ordinal)).Click();

        Assert.Equal("500", cut.Find("input[type=number]").GetAttribute("value"));
    }
}
