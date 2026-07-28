using ArkadeHeroes.Chain;
using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Server;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ArkadeHeroes.Tests;

/// <summary>
/// The marketplace listing fee: a flat treasury charge the seller pays to list an offer — treasury
/// capture on secondary trades (the counterweight to the daily + season faucets). The fee GATES the
/// listing: an offer stays pending (not buyable) until the fee invoice clears, even once the asset is
/// deposited. Both factories pin the fee explicitly rather than leaning on the shipped default, so these
/// keep testing the gate and the no-op path whatever that default is set to.
/// </summary>
public class MarketplaceListingFeeTests
{
    const long Fee = 500;

    static WebApplicationFactory<Program> FeeFactory() => FactoryWithFee(Fee);

    static WebApplicationFactory<Program> FactoryWithFee(long fee) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.Configure<GameOptions>(o => o.OfferListingFeeSats = fee)));

    [Fact]
    public async Task Listing_StaysPendingUntilFeePaid_ThenTreasuryCaptured()
    {
        using var factory = FeeFactory();
        var chain = (InMemoryChainService)factory.Services.GetRequiredService<IChainService>();
        var (seller, _) = await factory.RegisterAsync("LF-Seller");
        await seller.BuyItemAsync("rusty-blade");

        var treasuryBefore = await chain.TreasuryBalanceAsync();

        // Listing bills a fee invoice for the flat amount.
        var offer = await seller.Offers.CreateItemAsync(new CreateOfferRequest("rusty-blade", 3_000));
        Assert.Equal(Fee, offer.ListingFeeSats);
        Assert.NotNull(offer.ListingFee);
        Assert.Equal(Fee, offer.ListingFee!.AmountSats);

        // Even after the asset is deposited, the offer is NOT live — the fee hasn't cleared.
        await seller.Dev.FundOfferAsync(new { OfferId = offer.OfferId });
        Assert.DoesNotContain(await seller.Offers.ListAsync(), o => o.OfferId == offer.OfferId);

        // Seller pays the listing fee → the offer goes active and the treasury is credited.
        await seller.Dev.PayInvoiceAsync(new { offer.ListingFee!.InvoiceId });
        Assert.Contains(await seller.Offers.ListAsync(), o => o.OfferId == offer.OfferId && o.Status == "active");
        Assert.Equal(treasuryBefore + Fee, await chain.TreasuryBalanceAsync());
    }

    [Fact]
    public async Task Listing_FeePending_DoesNotReserveTheAlreadyDepositedUnit()
    {
        // A funded-but-fee-unpaid offer stays `pending`, yet its unit has ALREADY left the seller's
        // wallet. The free-unit check counts a pending offer as "awaiting deposit" — true only while
        // the item is still held — so reading `pending` alone double-counts this unit and wrongly
        // refuses a second listing the seller genuinely holds free.
        using var factory = FeeFactory();
        var (seller, _) = await factory.RegisterAsync("LF-TwoUnits");
        await seller.BuyItemAsync("swift-anklet");
        await seller.BuyItemAsync("swift-anklet"); // now holds 2

        var first = await seller.Offers.CreateItemAsync(new CreateOfferRequest("swift-anklet", 3_000));
        await seller.Dev.FundOfferAsync(new { OfferId = first.OfferId });

        // The fee is deliberately left unpaid: `first` is still pending, but its unit is gone.
        Assert.DoesNotContain(await seller.Offers.ListAsync(), o => o.OfferId == first.OfferId);

        // The second unit is free, so listing it must be allowed.
        await seller.Offers.CreateItemAsync(new CreateOfferRequest("swift-anklet", 4_000));

        // But not a third — both units are committed now.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => seller.Offers.CreateItemAsync(new CreateOfferRequest("swift-anklet", 5_000)));
    }

    [Fact]
    public async Task Listing_FeeDisabled_ActiveOnFund_NoInvoice()
    {
        // The fee-disabled path: no fee invoice, the offer is live as soon as the asset lands.
        using var factory = FactoryWithFee(0);
        var (seller, _) = await factory.RegisterAsync("LF-Free");
        await seller.BuyItemAsync("rusty-blade");

        var offer = await seller.Offers.CreateItemAsync(new CreateOfferRequest("rusty-blade", 3_000));
        Assert.Equal(0, offer.ListingFeeSats);
        Assert.Null(offer.ListingFee);

        await seller.Dev.FundOfferAsync(new { OfferId = offer.OfferId });
        Assert.Contains(await seller.Offers.ListAsync(), o => o.OfferId == offer.OfferId && o.Status == "active");
    }

    [Fact]
    public async Task Config_PublishesListingFee_ForPreListingDisplay()
    {
        // The sell page previews the fee from GET /api/chain/info before the seller lists.
        using var factory = FeeFactory();
        var client = new ArkadeHeroesClient(factory.CreateClient());
        var info = await client.Chain.InfoAsync();
        Assert.Equal(Fee, info.Config?.OfferListingFeeSats);
    }
}
