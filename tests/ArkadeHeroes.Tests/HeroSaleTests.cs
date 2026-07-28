using ArkadeHeroes.Client.Sdk;
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

    /// <summary>Lists a hero and clears the listing fee, so these lifecycle tests drive the real default
    /// path — a fee IS charged, and an offer stays pending until it clears. Tests that expect listing
    /// itself to be refused call the SDK directly instead.</summary>
    private static async Task<CreateOfferResponse> ListHeroAsync(ArkadeHeroesClient seller, string heroId, long ask)
    {
        var offer = await seller.Offers.CreateHeroAsync(new CreateHeroOfferRequest(heroId, ask));
        if (offer.ListingFee is { AmountSats: > 0 } fee)
            await seller.Dev.PayInvoiceAsync(new { fee.InvoiceId });
        return offer;
    }

    [Fact]
    public async Task HeroSale_ListFundFulfilClaim_MovesOwnershipAndStripsEquipment()
    {
        var (seller, _) = await _factory.RegisterAsync("H-Seller");
        var (buyer, buyerPlayer) = await _factory.RegisterAsync("H-Buyer");
        var heroes = await seller.ClaimStartersAsync();
        var hero = heroes[0];

        // Equip the hero, to prove the loadout is stripped when it changes owners.
        await seller.BuyItemAsync("rusty-blade");
        await seller.Heroes.EquipAsync(hero.Id, new EquipRequest("rusty-blade"));

        const long ask = 20_000;
        var offer = await ListHeroAsync(seller, hero.Id, ask);
        await seller.Dev.FundOfferAsync(new { OfferId = offer.OfferId });

        // Discoverable as a HERO offer, named for the hero.
        var listed = await buyer.Offers.ListAsync();
        var mine = Assert.Single(listed, o => o.OfferId == offer.OfferId);
        Assert.Equal("hero", mine.Kind);
        Assert.Equal(hero.Id, mine.HeroId);
        Assert.Equal(hero.Name, mine.ItemName);

        var sellerBefore = (await seller.Players.MeAsync()).BalanceSats;

        // Buyer fulfils (own wallet) then claims game-side ownership.
        await buyer.Dev.FulfillOfferAsync(new { OfferId = offer.OfferId });
        var claim = await buyer.Offers.ClaimHeroAsync(offer.OfferId);

        // The hero is the buyer's now, with no equipment; the seller was paid.
        Assert.Equal(buyerPlayer.PlayerId, claim.Hero.OwnerId);
        Assert.Empty(claim.Hero.Equipment);
        var sellerAfter = (await seller.Players.MeAsync()).BalanceSats;
        Assert.Equal(sellerBefore + ask, sellerAfter);

        // The buyer controls the hero; the seller no longer does.
        Assert.Contains(await buyer.Heroes.MineAsync(), h => h.Id == hero.Id);
        Assert.DoesNotContain(await seller.Heroes.MineAsync(), h => h.Id == hero.Id);
    }

    [Fact]
    public async Task CannotListAHeroYouDoNotOwn()
    {
        var (seller, _) = await _factory.RegisterAsync("H-NotMine");
        var (other, _) = await _factory.RegisterAsync("H-Owner");
        var othersHeroes = await other.ClaimStartersAsync();

        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => seller.Offers.CreateHeroAsync(new CreateHeroOfferRequest(othersHeroes[0].Id, 1_000)));
    }

    [Fact]
    public async Task CannotListTheSameHeroTwice()
    {
        var (seller, _) = await _factory.RegisterAsync("H-Double");
        var heroes = await seller.ClaimStartersAsync();

        await seller.Offers.CreateHeroAsync(new CreateHeroOfferRequest(heroes[0].Id, 1_000));
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => seller.Offers.CreateHeroAsync(new CreateHeroOfferRequest(heroes[0].Id, 2_000)));
    }

    [Fact]
    public async Task ClaimBeforeFulfilment_IsRefused()
    {
        var (seller, _) = await _factory.RegisterAsync("H-EarlyS");
        var (buyer, _) = await _factory.RegisterAsync("H-EarlyB");
        var heroes = await seller.ClaimStartersAsync();

        var offer = await ListHeroAsync(seller, heroes[0].Id, 5_000);
        await seller.Dev.FundOfferAsync(new { OfferId = offer.OfferId });

        // The buyer never fulfilled, so the chain doesn't show them holding it.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => buyer.Offers.ClaimHeroAsync(offer.OfferId));
    }
}
