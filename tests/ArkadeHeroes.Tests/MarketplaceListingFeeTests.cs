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
/// deposited. The default is 0 (disabled), so every other offer test is unaffected; these use a
/// fee-enabled factory to exercise the gate, and a plain factory to prove the disabled path is a no-op.
/// </summary>
public class MarketplaceListingFeeTests
{
    const long Fee = 500;

    static WebApplicationFactory<Program> FeeFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.Configure<GameOptions>(o => o.OfferListingFeeSats = Fee)));

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
    public async Task Listing_FeeDisabled_ActiveOnFund_NoInvoice()
    {
        // The shared default (0) path: no fee invoice, the offer is live as soon as the asset lands.
        using var factory = new WebApplicationFactory<Program>();
        var (seller, _) = await factory.RegisterAsync("LF-Free");
        await seller.BuyItemAsync("rusty-blade");

        var offer = await seller.Offers.CreateItemAsync(new CreateOfferRequest("rusty-blade", 3_000));
        Assert.Equal(0, offer.ListingFeeSats);
        Assert.Null(offer.ListingFee);

        await seller.Dev.FundOfferAsync(new { OfferId = offer.OfferId });
        Assert.Contains(await seller.Offers.ListAsync(), o => o.OfferId == offer.OfferId && o.Status == "active");
    }
}
