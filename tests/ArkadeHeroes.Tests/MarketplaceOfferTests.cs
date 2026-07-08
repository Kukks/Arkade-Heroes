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

        var sellerAfter = (await seller.Players.MeAsync()).BalanceSats;
        Assert.Equal(sellerBefore + ask, sellerAfter);

        // Offer closed — no longer discoverable.
        var afterSale = await buyer.Offers.ListAsync();
        Assert.DoesNotContain(afterSale, o => o.OfferId == offer.OfferId);

        // The buyer now holds the item — provable by equipping it to a hero.
        var buyerHeroes = await buyer.ClaimStartersAsync();
        await buyer.Heroes.EquipAsync(buyerHeroes[0].Id, new EquipRequest("rusty-blade"));
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
        await seller.Offers.CreateItemAsync(new CreateOfferRequest("rusty-blade", 1_000));

        // The single unit is already reserved by the first (pending) offer.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => seller.Offers.CreateItemAsync(new CreateOfferRequest("rusty-blade", 2_000)));
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
