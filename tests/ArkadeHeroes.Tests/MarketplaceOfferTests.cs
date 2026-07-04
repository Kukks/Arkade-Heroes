using System.Net;
using System.Net.Http.Json;
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

    private static async Task<CreateOfferResponse> ListAsync(HttpClient seller, string itemId, long ask)
        => (await (await seller.PostAsJsonAsync("/api/offers", new CreateOfferRequest(itemId, ask)))
            .Content.ReadFromJsonAsync<CreateOfferResponse>())!;

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
        var beforeFund = await buyer.GetFromJsonAsync<List<OfferDto>>("/api/offers");
        Assert.DoesNotContain(beforeFund!, o => o.OfferId == offer.OfferId);

        // Seller deposits the item — the offer becomes an active, discoverable listing.
        (await seller.PostAsJsonAsync("/api/dev/fund-offer", new { OfferId = offer.OfferId })).EnsureSuccessStatusCode();
        var listed = await buyer.GetFromJsonAsync<List<OfferDto>>("/api/offers");
        var mine = Assert.Single(listed!, o => o.OfferId == offer.OfferId);
        Assert.Equal("active", mine.Status);
        Assert.Equal("Rusty Blade", mine.ItemName);
        Assert.Equal(ask, mine.AskSats);

        var sellerBefore = (await seller.GetFromJsonAsync<PlayerDto>("/api/players/me"))!.BalanceSats;

        // Buyer fulfils: pays the seller, takes the item.
        (await buyer.PostAsJsonAsync("/api/dev/fulfill-offer", new { OfferId = offer.OfferId })).EnsureSuccessStatusCode();

        var sellerAfter = (await seller.GetFromJsonAsync<PlayerDto>("/api/players/me"))!.BalanceSats;
        Assert.Equal(sellerBefore + ask, sellerAfter);

        // Offer closed — no longer discoverable.
        var afterSale = await buyer.GetFromJsonAsync<List<OfferDto>>("/api/offers");
        Assert.DoesNotContain(afterSale!, o => o.OfferId == offer.OfferId);

        // The buyer now holds the item — provable by equipping it to a hero.
        var buyerHeroes = await buyer.ClaimStartersAsync();
        (await buyer.PostAsJsonAsync($"/api/heroes/{buyerHeroes[0].Id}/equip",
            new EquipRequest("rusty-blade"))).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Offer_ParamsAreRebuildable_404ForUnknown()
    {
        var (seller, _) = await _factory.RegisterAsync("M-Params");
        await seller.BuyItemAsync("steel-saber");
        var offer = await ListAsync(seller, "steel-saber", 5_000);

        var parameters = (await seller.GetFromJsonAsync<Chain.Covenants.OfferParams>(
            $"/api/offers/{offer.OfferId}/params"))!;
        Assert.Equal(offer.OfferId, parameters.OfferId);
        Assert.Equal(offer.ItemAssetId, parameters.ItemAssetId);
        Assert.Equal(5_000, parameters.AskSats);

        var unknown = await seller.GetAsync("/api/offers/does-not-exist/params");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task Offer_CannotListItemNotHeld()
    {
        var (seller, _) = await _factory.RegisterAsync("M-NoItem");
        var response = await seller.PostAsJsonAsync("/api/offers", new CreateOfferRequest("rusty-blade", 1_000));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Offer_EquippedUnitCannotBeListed()
    {
        var (seller, _) = await _factory.RegisterAsync("M-Equipped");
        var heroes = await seller.ClaimStartersAsync();
        await seller.BuyItemAsync("rusty-blade");
        (await seller.PostAsJsonAsync($"/api/heroes/{heroes[0].Id}/equip",
            new EquipRequest("rusty-blade"))).EnsureSuccessStatusCode();

        // The only unit is equipped — none free to sell.
        var response = await seller.PostAsJsonAsync("/api/offers", new CreateOfferRequest("rusty-blade", 1_000));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Offer_SingleUnitCannotBeListedTwice()
    {
        var (seller, _) = await _factory.RegisterAsync("M-Double");
        await seller.BuyItemAsync("rusty-blade");
        (await seller.PostAsJsonAsync("/api/offers", new CreateOfferRequest("rusty-blade", 1_000)))
            .EnsureSuccessStatusCode();

        // The single unit is already reserved by the first (pending) offer.
        var second = await seller.PostAsJsonAsync("/api/offers", new CreateOfferRequest("rusty-blade", 2_000));
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
    }

    [Fact]
    public async Task Offer_TwoUnits_ListOneKeepOne_ThenListSecond()
    {
        var (seller, _) = await _factory.RegisterAsync("M-TwoUnits");
        await seller.BuyItemAsync("swift-anklet");
        await seller.BuyItemAsync("swift-anklet"); // now holds 2

        var first = await ListAsync(seller, "swift-anklet", 3_000);
        (await seller.PostAsJsonAsync("/api/dev/fund-offer", new { OfferId = first.OfferId })).EnsureSuccessStatusCode();

        // One unit is deposited; the second is still free to list.
        (await seller.PostAsJsonAsync("/api/offers", new CreateOfferRequest("swift-anklet", 4_000)))
            .EnsureSuccessStatusCode();

        // But not a third — both units are now committed.
        var third = await seller.PostAsJsonAsync("/api/offers", new CreateOfferRequest("swift-anklet", 5_000));
        Assert.Equal(HttpStatusCode.BadRequest, third.StatusCode);
    }

    [Fact]
    public async Task Offer_BuyerWithoutFundsIsRefused()
    {
        var (seller, _) = await _factory.RegisterAsync("M-RichSeller");
        var (buyer, _) = await _factory.RegisterAsync("M-BrokeBuyer");
        await seller.BuyItemAsync("arkforged-edge");

        // Ask more than the buyer's simulated faucet balance.
        var offer = await ListAsync(seller, "arkforged-edge", Chain.InMemoryChainService.FaucetSats + 1);
        (await seller.PostAsJsonAsync("/api/dev/fund-offer", new { OfferId = offer.OfferId })).EnsureSuccessStatusCode();

        var fulfil = await buyer.PostAsJsonAsync("/api/dev/fulfill-offer", new { OfferId = offer.OfferId });
        Assert.Equal(HttpStatusCode.BadRequest, fulfil.StatusCode);

        // Still resting, still discoverable — the failed buy changed nothing.
        var listed = await buyer.GetFromJsonAsync<List<OfferDto>>("/api/offers");
        Assert.Contains(listed!, o => o.OfferId == offer.OfferId);
    }

    [Fact]
    public async Task Offer_CancelReturnsItemToSeller()
    {
        var (seller, _) = await _factory.RegisterAsync("M-Cancel");
        var heroes = await seller.ClaimStartersAsync();
        await seller.BuyItemAsync("rusty-blade");
        var offer = await ListAsync(seller, "rusty-blade", 3_000);
        (await seller.PostAsJsonAsync("/api/dev/fund-offer", new { OfferId = offer.OfferId })).EnsureSuccessStatusCode();

        // While listed, the unit is committed — equipping it is refused.
        var equipWhileListed = await seller.PostAsJsonAsync($"/api/heroes/{heroes[0].Id}/equip",
            new EquipRequest("rusty-blade"));
        Assert.Equal(HttpStatusCode.BadRequest, equipWhileListed.StatusCode);

        // Cancel — the item returns, and now equipping works.
        (await seller.PostAsJsonAsync("/api/dev/reclaim-offer", new { OfferId = offer.OfferId })).EnsureSuccessStatusCode();
        (await seller.PostAsJsonAsync($"/api/heroes/{heroes[0].Id}/equip",
            new EquipRequest("rusty-blade"))).EnsureSuccessStatusCode();
    }
}
