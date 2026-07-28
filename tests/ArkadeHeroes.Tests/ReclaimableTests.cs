using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Server;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ArkadeHeroes.Tests;

/// <summary>
/// The recovery list: this player's covenant escrows that may still hold their assets with no path
/// forward. It exists so a stranded deposit can be SEEN at all — the console client has reclaim
/// commands (canceloffer / refund-breed / refund-merge), the browser has none, so a listing stuck on an
/// unpaid fee was previously invisible there. The list is discovery only: reclaiming is a covenant spend
/// from the player's own wallet against the public escrow params, so it never needs the server to agree.
/// </summary>
public class ReclaimableTests
{
    const long Fee = 500;

    static WebApplicationFactory<Program> FeeFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.Configure<GameOptions>(o => o.OfferListingFeeSats = Fee)));

    [Fact]
    public async Task DepositedListing_IsReclaimable()
    {
        // The seller's asset is escrowed in a resting offer. Whether it sells or not, that asset is
        // recoverable through the covenant's timelocked reclaim leaf, and the browser needs to SEE it —
        // it has no other way to find a listing it wants back.
        using var factory = FeeFactory();
        var (seller, _) = await factory.RegisterAsync("RC-Resting");
        await seller.BuyItemAsync("rusty-blade");

        var offer = await seller.Offers.CreateItemAsync(new CreateOfferRequest("rusty-blade", 3_000));
        await seller.Dev.FundOfferAsync(new { OfferId = offer.OfferId });

        var resting = Assert.Single(await seller.Players.ReclaimableAsync());
        Assert.Equal("offer", resting.Kind);
        Assert.Equal(offer.OfferId, resting.Id);
        Assert.Contains("resting on the market", resting.Summary);
    }

    [Fact]
    public async Task Listing_StillAwaitingItsDeposit_IsNotReclaimable()
    {
        // Nothing has left the wallet yet, so there is nothing to recover — listing it would be noise.
        using var factory = FeeFactory();
        var (seller, _) = await factory.RegisterAsync("RC-Undeposited");
        await seller.BuyItemAsync("rusty-blade");
        await seller.Offers.CreateItemAsync(new CreateOfferRequest("rusty-blade", 3_000));

        Assert.Empty(await seller.Players.ReclaimableAsync());
    }

    [Fact]
    public async Task Reclaimable_IsScopedToTheAskingPlayer()
    {
        using var factory = FeeFactory();
        var (seller, _) = await factory.RegisterAsync("RC-Mine");
        var (other, _) = await factory.RegisterAsync("RC-Theirs");
        await seller.BuyItemAsync("rusty-blade");
        var offer = await seller.Offers.CreateItemAsync(new CreateOfferRequest("rusty-blade", 3_000));
        await seller.Dev.FundOfferAsync(new { OfferId = offer.OfferId });

        Assert.Single(await seller.Players.ReclaimableAsync());
        Assert.Empty(await other.Players.ReclaimableAsync());
    }

    [Fact]
    public async Task UnrevealedCovenantBreed_IsReclaimable()
    {
        // A covenant breed that never reached reveal. It is listed on the SESSION being unfinished, not
        // on the escrow reading fully funded: a run that died between the two parent deposits leaves a
        // hero escrowed while IsBreedEscrowFundedAsync still reports false, and that is the case that
        // most needs surfacing. Reclaiming an empty escrow is harmless; hiding a full one is not.
        using var factory = new WebApplicationFactory<Program>();
        var (player, _) = await factory.RegisterAsync("RC-Breed");
        var heroes = await player.ClaimStartersAsync();

        var commit = await player.Breeding.CommitAsync(
            new BreedCommitRequest(heroes[0].Id, heroes[1].Id, "covenant"));

        var stuck = Assert.Single(await player.Players.ReclaimableAsync());
        Assert.Equal("breed", stuck.Kind);
        Assert.Equal(commit.BreedingId, stuck.Id);
    }
}
