using ArkadeHeroes.Chain;
using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Server;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ArkadeHeroes.Tests;

/// <summary>
/// The marketplace fee: a treasury cut on secondary trades, the counterweight to the daily + season
/// faucets. It is enforced by the offer's own COVENANT and taken from the SALE — the buyer pays the
/// listed ask and the fulfil leaf splits it, seller <c>ask − fee</c> and treasury the rest.
///
/// That shape is the point. Nothing is billed at listing, so an offer that never sells costs its seller
/// nothing, there is no fee payment that can fail and strand a deposited hero, and the server cannot
/// skip or misdirect a cut the covenant pins. These tests pin the fee explicitly rather than leaning on
/// the shipped default, so they keep meaning whatever that default is set to.
/// </summary>
public class MarketplaceListingFeeTests
{
    const long Fee = 500;
    const long Ask = 3_000;

    static WebApplicationFactory<Program> FactoryWithFee(long fee) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.Configure<GameOptions>(o => o.OfferListingFeeSats = fee)));

    [Fact]
    public async Task Listing_CostsNothingUpFront_AndGoesLiveOnTheDepositAlone()
    {
        using var factory = FactoryWithFee(Fee);
        var chain = (InMemoryChainService)factory.Services.GetRequiredService<IChainService>();
        var (seller, _) = await factory.RegisterAsync("MF-Seller");
        await seller.BuyItemAsync("rusty-blade");

        var balanceBefore = (await seller.Players.MeAsync()).BalanceSats;
        var treasuryBefore = await chain.TreasuryBalanceAsync();

        var offer = await seller.Offers.CreateItemAsync(new CreateOfferRequest("rusty-blade", Ask));
        Assert.Equal(Fee, offer.ListingFeeSats);

        // Depositing is all it takes — no invoice, no second step.
        await seller.Dev.FundOfferAsync(new { OfferId = offer.OfferId });
        Assert.Contains(await seller.Offers.ListAsync(), o => o.OfferId == offer.OfferId && o.Status == "active");

        // And listing moved no money at all: the seller is untouched and the treasury has earned nothing
        // yet, because nothing has SOLD. This is what makes an unsold listing free.
        Assert.Equal(balanceBefore, (await seller.Players.MeAsync()).BalanceSats);
        Assert.Equal(treasuryBefore, await chain.TreasuryBalanceAsync());
    }

    /// <summary>
    /// The other half of "listing is free": taking an unsold item back must ALSO cost nothing. The
    /// covenant gets this right by construction — the fee lives in the fulfil leaf, and reclaim spends a
    /// different leaf that moves no sats — but /sell promises the seller "a hero that never sells is
    /// never charged", and nothing pinned the reclaim end of that promise. A fee quietly taken here
    /// would turn an unsold listing into a loss, which is the opposite of what the page says.
    /// </summary>
    [Fact]
    public async Task Reclaiming_AnUnsoldOffer_ChargesNothing_AndReturnsTheItem()
    {
        using var factory = FactoryWithFee(Fee);
        var chain = (InMemoryChainService)factory.Services.GetRequiredService<IChainService>();
        var (seller, sellerDto) = await factory.RegisterAsync("MF-Reclaim");
        await seller.BuyItemAsync("rusty-blade");

        var offer = await seller.Offers.CreateItemAsync(new CreateOfferRequest("rusty-blade", Ask));
        await seller.Dev.FundOfferAsync(new { OfferId = offer.OfferId });

        // Baselines AFTER the deposit: the item has left the seller and nothing has been charged yet.
        var balanceBefore = (await seller.Players.MeAsync()).BalanceSats;
        var treasuryBefore = await chain.TreasuryBalanceAsync();
        Assert.Equal(0UL, await chain.GetItemAssetBalanceAsync(sellerDto.PlayerId, "rusty-blade"));

        await seller.Dev.ReclaimOfferAsync(new { OfferId = offer.OfferId });

        Assert.Equal(treasuryBefore, await chain.TreasuryBalanceAsync());                       // the house took nothing
        Assert.Equal(balanceBefore, (await seller.Players.MeAsync()).BalanceSats);              // and the seller paid nothing
        Assert.Equal(1UL, await chain.GetItemAssetBalanceAsync(sellerDto.PlayerId, "rusty-blade"));  // item is back
        Assert.DoesNotContain(await seller.Offers.ListAsync(), o => o.OfferId == offer.OfferId && o.Status == "active");
    }

    [Fact]
    public async Task Sale_PaysTheSellerTheAskMinusTheFee_AndTheTreasuryTheRest()
    {
        using var factory = FactoryWithFee(Fee);
        var chain = (InMemoryChainService)factory.Services.GetRequiredService<IChainService>();
        var (seller, _) = await factory.RegisterAsync("MF-Paid");
        var (buyer, _) = await factory.RegisterAsync("MF-Buyer");
        await seller.BuyItemAsync("rusty-blade");

        var offer = await seller.Offers.CreateItemAsync(new CreateOfferRequest("rusty-blade", Ask));
        await seller.Dev.FundOfferAsync(new { OfferId = offer.OfferId });

        var sellerBefore = (await seller.Players.MeAsync()).BalanceSats;
        var buyerBefore = (await buyer.Players.MeAsync()).BalanceSats;
        var treasuryBefore = await chain.TreasuryBalanceAsync();

        await buyer.Dev.FulfillOfferAsync(new { OfferId = offer.OfferId });

        // The buyer pays the sticker ask; the covenant splits it. The seller absorbs the fee.
        Assert.Equal(buyerBefore - Ask, (await buyer.Players.MeAsync()).BalanceSats);
        Assert.Equal(sellerBefore + Ask - Fee, (await seller.Players.MeAsync()).BalanceSats);
        Assert.Equal(treasuryBefore + Fee, await chain.TreasuryBalanceAsync());
    }

    [Fact]
    public async Task AnAskAtOrBelowTheFee_IsRefusedWithAnActionableMessage()
    {
        // The seller's payout would be zero or negative, and PayTo cannot pin a non-positive amount, so
        // the covenant could not be built at all. Refusing at listing beats failing at fulfil.
        using var factory = FactoryWithFee(Fee);
        var (seller, _) = await factory.RegisterAsync("MF-TooCheap");
        await seller.BuyItemAsync("rusty-blade");

        var refused = await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => seller.Offers.CreateItemAsync(new CreateOfferRequest("rusty-blade", Fee)));
        Assert.Contains("marketplace fee", refused.Message);
    }

    [Fact]
    public async Task FeeDisabled_SellerKeepsTheWholeAsk()
    {
        using var factory = FactoryWithFee(0);
        var chain = (InMemoryChainService)factory.Services.GetRequiredService<IChainService>();
        var (seller, _) = await factory.RegisterAsync("MF-Free");
        var (buyer, _) = await factory.RegisterAsync("MF-FreeBuyer");
        await seller.BuyItemAsync("rusty-blade");

        var offer = await seller.Offers.CreateItemAsync(new CreateOfferRequest("rusty-blade", Ask));
        Assert.Equal(0, offer.ListingFeeSats);
        await seller.Dev.FundOfferAsync(new { OfferId = offer.OfferId });

        var sellerBefore = (await seller.Players.MeAsync()).BalanceSats;
        var treasuryBefore = await chain.TreasuryBalanceAsync();
        await buyer.Dev.FulfillOfferAsync(new { OfferId = offer.OfferId });

        Assert.Equal(sellerBefore + Ask, (await seller.Players.MeAsync()).BalanceSats);
        Assert.Equal(treasuryBefore, await chain.TreasuryBalanceAsync());
    }

    [Fact]
    public async Task Config_PublishesTheFee_ForPreListingDisplay()
    {
        // The sell page previews the fee from GET /api/chain/info so a seller sees what a sale will pay.
        using var factory = FactoryWithFee(Fee);
        var client = new ArkadeHeroesClient(factory.CreateClient());
        var info = await client.Chain.InfoAsync();
        Assert.Equal(Fee, info.Config?.OfferListingFeeSats);
    }
}
