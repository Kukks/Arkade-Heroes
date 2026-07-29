using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ArkadeHeroes.Tests;

/// <summary>
/// The item marketplace over the InMemory offer simulation: a seller lists a
/// spare item unit at a fixed ask, deposits it into the offer, and a buyer
/// fulfils it — the seller is paid and the item moves to the buyer. Mirrors the
/// lifecycle the covenant enforces on regtest (a resting offer VTXO ANYONE may
/// take only by paying the seller the ask), so the game logic is exercised
/// without a live chain. The dev endpoints stand in for the client wallets.
/// </summary>
public class MarketplaceOfferTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public MarketplaceOfferTests(WebApplicationFactory<Program> factory) => _factory = factory;

    /// <summary>Lists a spare unit. Nothing is paid at listing — the marketplace fee is enforced by the
    /// offer's covenant and taken from the sale — so listing is a single call again.</summary>
    private static Task<CreateOfferResponse> ListAsync(ArkadeHeroesClient seller, string itemId, long ask)
        => seller.Offers.CreateItemAsync(new CreateOfferRequest(itemId, ask));

    [Fact]
    public async Task Offer_ListFundFulfil_ItemMovesAndSellerPaid()
    {
        var (seller, _) = await _factory.RegisterAsync("M-Seller");
        var (buyer, _) = await _factory.RegisterAsync("M-Buyer");
        await seller.BuyItemAsync("rusty-blade");

        const long ask = 3_000;
        var offer = await ListAsync(seller, "rusty-blade", ask);
        Assert.False(string.IsNullOrEmpty(offer.OfferAddress));
        Assert.Equal(ask, offer.AskSats);

        // Not yet buyable: the item hasn't been deposited (pending, not listed).
        var beforeFund = await buyer.Offers.ListAsync();
        Assert.DoesNotContain(beforeFund, o => o.OfferId == offer.OfferId);

        // Seller deposits the item — the offer becomes an active, discoverable listing.
        await seller.Dev.FundOfferAsync(new { OfferId = offer.OfferId });
        var listed = await buyer.Offers.ListAsync();
        var mine = Assert.Single(listed, o => o.OfferId == offer.OfferId);
        Assert.Equal("active", mine.Status);
        Assert.Equal("Rusty Blade", mine.ItemName);
        Assert.Equal(ask, mine.AskSats);

        var sellerBefore = (await seller.Players.MeAsync()).BalanceSats;

        // Buyer fulfils: pays the seller, takes the item.
        await buyer.Dev.FulfillOfferAsync(new { OfferId = offer.OfferId });

        // The buyer paid the sticker ask; the covenant routed the marketplace fee to the treasury, so the
        // seller nets ask − fee. Read the fee off the offer rather than hardcoding the shipped default.
        var sellerAfter = (await seller.Players.MeAsync()).BalanceSats;
        Assert.Equal(sellerBefore + ask - offer.ListingFeeSats, sellerAfter);

        // Offer closed — no longer discoverable.
        var afterSale = await buyer.Offers.ListAsync();
        Assert.DoesNotContain(afterSale, o => o.OfferId == offer.OfferId);

        // The buyer now holds the item — provable by equipping it to a hero.
        var buyerHeroes = await buyer.ClaimStartersAsync();
        await buyer.Heroes.EquipAsync(buyerHeroes[0].Id, new EquipRequest("rusty-blade"));
    }

    /// <summary>
    /// An ITEM sale reaches the treasury structurally — the fulfil leaf pins the cut — but the server
    /// used to book nothing for it, because a closing offer looks the same whether it sold or the seller
    /// reclaimed it, and guessing would OVERSTATE income for a treasury holding real bitcoin. The two
    /// spends are in fact distinguishable: only a fulfil pays the treasury in the transaction that spends
    /// the offer. So a sale is now booked and a reclaim is still not — the under-count is closed without
    /// opening an over-count.
    /// </summary>
    [Fact]
    public async Task Offer_AnItemSaleIsBookedAsMarketplaceIncome_AReclaimIsNot()
    {
        var (seller, _) = await _factory.RegisterAsync("M-Book-Seller");
        var (buyer, _) = await _factory.RegisterAsync("M-Book-Buyer");
        await seller.BuyItemAsync("rusty-blade");
        await seller.BuyItemAsync("rusty-blade");

        static async Task<long> BookedAsync(ArkadeHeroesClient c) =>
            (await c.Economy.HealthAsync()).InflowByTag.GetValueOrDefault("listing");
        var before = await BookedAsync(seller);

        // A SALE: the covenant paid the treasury its cut, and the server can now prove that it did.
        var sold = await ListAsync(seller, "rusty-blade", 3_000);
        Assert.True(sold.ListingFeeSats > 0, "this test needs a fee-bearing listing to have anything to book");
        await seller.Dev.FundOfferAsync(new { OfferId = sold.OfferId });
        await buyer.Offers.ListAsync();     // reconcile — the deposit makes the listing active
        await buyer.Dev.FulfillOfferAsync(new { OfferId = sold.OfferId });
        await buyer.Offers.ListAsync();     // reconcile — where the offer closes
        var afterSale = await BookedAsync(seller);
        Assert.Equal(before + sold.ListingFeeSats, afterSale);

        // A RECLAIM closes an offer exactly as a sale does, and must still book nothing.
        var unsold = await ListAsync(seller, "rusty-blade", 3_000);
        await seller.Dev.FundOfferAsync(new { OfferId = unsold.OfferId });
        await buyer.Offers.ListAsync();
        await seller.Dev.ReclaimOfferAsync(new { OfferId = unsold.OfferId });
        await buyer.Offers.ListAsync();
        Assert.Equal(afterSale, await BookedAsync(seller));
    }

    [Fact]
    public async Task Offer_ParamsAreRebuildable_404ForUnknown()
    {
        var (seller, _) = await _factory.RegisterAsync("M-Params");
        await seller.BuyItemAsync("steel-saber");
        var offer = await ListAsync(seller, "steel-saber", 5_000);

        var parameters = await seller.Offers.ParamsAsync(offer.OfferId);
        Assert.Equal(offer.OfferId, parameters.OfferId);
        Assert.Equal(offer.ItemAssetId, parameters.ItemAssetId);
        Assert.Equal(5_000, parameters.AskSats);

        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => seller.Offers.ParamsAsync("does-not-exist"));
    }

    [Fact]
    public async Task Offer_CannotListItemNotHeld()
    {
        var (seller, _) = await _factory.RegisterAsync("M-NoItem");
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => seller.Offers.CreateItemAsync(new CreateOfferRequest("rusty-blade", 1_000)));
    }

    [Fact]
    public async Task Offer_EquippedUnitCannotBeListed()
    {
        var (seller, _) = await _factory.RegisterAsync("M-Equipped");
        var heroes = await seller.ClaimStartersAsync();
        await seller.BuyItemAsync("rusty-blade");
        await seller.Heroes.EquipAsync(heroes[0].Id, new EquipRequest("rusty-blade"));

        // The only unit is equipped — none free to sell.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => seller.Offers.CreateItemAsync(new CreateOfferRequest("rusty-blade", 1_000)));
    }

    [Fact]
    public async Task Offer_SingleUnitCannotBeListedTwice()
    {
        var (seller, _) = await _factory.RegisterAsync("M-Double");
        await seller.BuyItemAsync("rusty-blade");
        // Asks comfortably above the marketplace fee — an ask that doesn't clear the fee is refused for
        // its own reason, which would mask the double-listing rule under test here.
        await seller.Offers.CreateItemAsync(new CreateOfferRequest("rusty-blade", 3_000));

        // The single unit is already reserved by the first (pending) offer.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => seller.Offers.CreateItemAsync(new CreateOfferRequest("rusty-blade", 4_000)));
    }

    [Fact]
    public async Task Offer_TwoUnits_ListOneKeepOne_ThenListSecond()
    {
        var (seller, _) = await _factory.RegisterAsync("M-TwoUnits");
        await seller.BuyItemAsync("swift-anklet");
        await seller.BuyItemAsync("swift-anklet"); // now holds 2

        var first = await ListAsync(seller, "swift-anklet", 3_000);
        await seller.Dev.FundOfferAsync(new { OfferId = first.OfferId });

        // One unit is deposited; the second is still free to list.
        await seller.Offers.CreateItemAsync(new CreateOfferRequest("swift-anklet", 4_000));

        // But not a third — both units are now committed.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => seller.Offers.CreateItemAsync(new CreateOfferRequest("swift-anklet", 5_000)));
    }

    [Fact]
    public async Task Offer_BuyerWithoutFundsIsRefused()
    {
        var (seller, _) = await _factory.RegisterAsync("M-RichSeller");
        var (buyer, _) = await _factory.RegisterAsync("M-BrokeBuyer");
        await seller.BuyItemAsync("arkforged-edge");

        // Ask more than the buyer's simulated faucet balance.
        var offer = await ListAsync(seller, "arkforged-edge", Chain.InMemoryChainService.FaucetSats + 1);
        await seller.Dev.FundOfferAsync(new { OfferId = offer.OfferId });

        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => buyer.Dev.FulfillOfferAsync(new { OfferId = offer.OfferId }));

        // Still resting, still discoverable — the failed buy changed nothing.
        var listed = await buyer.Offers.ListAsync();
        Assert.Contains(listed, o => o.OfferId == offer.OfferId);
    }

    [Fact]
    public async Task Offer_CancelReturnsItemToSeller()
    {
        var (seller, _) = await _factory.RegisterAsync("M-Cancel");
        var heroes = await seller.ClaimStartersAsync();
        await seller.BuyItemAsync("rusty-blade");
        var offer = await ListAsync(seller, "rusty-blade", 3_000);
        await seller.Dev.FundOfferAsync(new { OfferId = offer.OfferId });

        // While listed, the unit is committed — equipping it is refused.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => seller.Heroes.EquipAsync(heroes[0].Id, new EquipRequest("rusty-blade")));

        // Cancel — the item returns, and now equipping works.
        await seller.Dev.ReclaimOfferAsync(new { OfferId = offer.OfferId });
        await seller.Heroes.EquipAsync(heroes[0].Id, new EquipRequest("rusty-blade"));
    }
}
