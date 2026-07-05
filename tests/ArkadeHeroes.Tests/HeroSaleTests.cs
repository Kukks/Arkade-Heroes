using System.Net;
using System.Net.Http.Json;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ArkadeHeroes.Tests;

/// <summary>
/// Hero sales over the InMemory offer simulation: a player lists one of their
/// HEROES (a unique asset) for sale via the same offer covenant items use; a
/// buyer fulfils it and claims game-side ownership — the hero record moves to the
/// buyer with its equipment stripped (loadouts stay in the seller's wallet, as on
/// transfer), and the seller is paid the ask. Mirrors the lifecycle the covenant
/// enforces on regtest.
/// </summary>
public class HeroSaleTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public HeroSaleTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private static async Task<CreateOfferResponse> ListHeroAsync(HttpClient seller, string heroId, long ask)
        => (await (await seller.PostAsJsonAsync("/api/offers/hero", new CreateHeroOfferRequest(heroId, ask)))
            .Content.ReadFromJsonAsync<CreateOfferResponse>())!;

    [Fact]
    public async Task HeroSale_ListFundFulfilClaim_MovesOwnershipAndStripsEquipment()
    {
        var (seller, _) = await _factory.RegisterAsync("H-Seller");
        var (buyer, buyerPlayer) = await _factory.RegisterAsync("H-Buyer");
        var heroes = await seller.ClaimStartersAsync();
        var hero = heroes[0];

        // Equip the hero, to prove the loadout is stripped when it changes owners.
        await seller.BuyItemAsync("rusty-blade");
        (await seller.PostAsJsonAsync($"/api/heroes/{hero.Id}/equip", new EquipRequest("rusty-blade"))).EnsureSuccessStatusCode();

        const long ask = 20_000;
        var offer = await ListHeroAsync(seller, hero.Id, ask);
        (await seller.PostAsJsonAsync("/api/dev/fund-offer", new { OfferId = offer.OfferId })).EnsureSuccessStatusCode();

        // Discoverable as a HERO offer, named for the hero.
        var listed = await buyer.GetFromJsonAsync<List<OfferDto>>("/api/offers");
        var mine = Assert.Single(listed!, o => o.OfferId == offer.OfferId);
        Assert.Equal("hero", mine.Kind);
        Assert.Equal(hero.Id, mine.HeroId);
        Assert.Equal(hero.Name, mine.ItemName);

        var sellerBefore = (await seller.GetFromJsonAsync<PlayerDto>("/api/players/me"))!.BalanceSats;

        // Buyer fulfils (own wallet) then claims game-side ownership.
        (await buyer.PostAsJsonAsync("/api/dev/fulfill-offer", new { OfferId = offer.OfferId })).EnsureSuccessStatusCode();
        var claim = (await (await buyer.PostAsync($"/api/offers/{offer.OfferId}/claim-hero", null))
            .Content.ReadFromJsonAsync<TransferResponse>())!;

        // The hero is the buyer's now, with no equipment; the seller was paid.
        Assert.Equal(buyerPlayer.PlayerId, claim.Hero.OwnerId);
        Assert.Empty(claim.Hero.Equipment);
        var sellerAfter = (await seller.GetFromJsonAsync<PlayerDto>("/api/players/me"))!.BalanceSats;
        Assert.Equal(sellerBefore + ask, sellerAfter);

        // The buyer controls the hero; the seller no longer does.
        Assert.Contains((await buyer.GetFromJsonAsync<List<HeroDto>>("/api/heroes/mine"))!, h => h.Id == hero.Id);
        Assert.DoesNotContain((await seller.GetFromJsonAsync<List<HeroDto>>("/api/heroes/mine"))!, h => h.Id == hero.Id);
    }

    [Fact]
    public async Task CannotListAHeroYouDoNotOwn()
    {
        var (seller, _) = await _factory.RegisterAsync("H-NotMine");
        var (other, _) = await _factory.RegisterAsync("H-Owner");
        var othersHeroes = await other.ClaimStartersAsync();

        var response = await seller.PostAsJsonAsync("/api/offers/hero",
            new CreateHeroOfferRequest(othersHeroes[0].Id, 1_000));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CannotListTheSameHeroTwice()
    {
        var (seller, _) = await _factory.RegisterAsync("H-Double");
        var heroes = await seller.ClaimStartersAsync();

        (await seller.PostAsJsonAsync("/api/offers/hero", new CreateHeroOfferRequest(heroes[0].Id, 1_000)))
            .EnsureSuccessStatusCode();
        var second = await seller.PostAsJsonAsync("/api/offers/hero", new CreateHeroOfferRequest(heroes[0].Id, 2_000));
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
    }

    [Fact]
    public async Task ClaimBeforeFulfilment_IsRefused()
    {
        var (seller, _) = await _factory.RegisterAsync("H-EarlyS");
        var (buyer, _) = await _factory.RegisterAsync("H-EarlyB");
        var heroes = await seller.ClaimStartersAsync();

        var offer = await ListHeroAsync(seller, heroes[0].Id, 5_000);
        (await seller.PostAsJsonAsync("/api/dev/fund-offer", new { OfferId = offer.OfferId })).EnsureSuccessStatusCode();

        // The buyer never fulfilled, so the chain doesn't show them holding it.
        var claim = await buyer.PostAsync($"/api/offers/{offer.OfferId}/claim-hero", null);
        Assert.Equal(HttpStatusCode.BadRequest, claim.StatusCode);
    }
}
