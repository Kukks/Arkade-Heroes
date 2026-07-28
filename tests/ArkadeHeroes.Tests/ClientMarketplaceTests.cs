using System.Net.Http.Json;
using ArkadeHeroes.Chain;
using ArkadeHeroes.Client;
using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Server;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ArkadeHeroes.Tests;

/// <summary>
/// Drives the REAL <see cref="GameClient"/> (the console client's command
/// dispatch) against an in-memory server, asserting on observable SERVER state —
/// so the user-facing marketplace commands (sell / offers / buyoffer) and the
/// first-run session handling are covered, not just the server API. The client
/// is pointed at the test server via an injected HttpClient and given an isolated
/// data dir, so no real wallet or filesystem home is touched.
/// </summary>
public class ClientMarketplaceTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly List<string> _homeDirs = [];

    public ClientMarketplaceTests(WebApplicationFactory<Program> factory) => _factory = factory;

    public void Dispose()
    {
        foreach (var d in _homeDirs)
            try { if (Directory.Exists(d)) Directory.Delete(d, recursive: true); } catch { /* windows lock */ }
    }

    private string FreshHome()
    {
        var home = Path.Combine(Path.GetTempPath(), $"ah-client-{Guid.NewGuid():N}");
        _homeDirs.Add(home);
        return home;
    }

    private GameClient NewClient(string home) => new("http://localhost", _factory.CreateClient(), home);

    [Fact]
    public async Task Register_CreatesFreshHomeDir_NoCrash()
    {
        // Regression for the first-run crash: a brand-new data dir must not throw
        // (register saves the session before any wallet op would create the dir).
        var home = FreshHome();
        Assert.False(Directory.Exists(home));

        await using var client = NewClient(home);
        await client.ExecuteAsync(["register", "CliFresh"]); // must not throw

        Assert.True(File.Exists(Path.Combine(home, "arkade-heroes-session.json")),
            "register must persist the session in the fresh home dir");
    }

    [Fact]
    public async Task Sell_Offers_BuyOffer_ThroughTheClient()
    {
        var observer = new ArkadeHeroesClient(_factory.CreateClient());

        // Seller drives: register → starter → buy an item → list it for sale.
        await using var seller = NewClient(FreshHome());
        await seller.ExecuteAsync(["register", "CliSeller"]);
        await seller.ExecuteAsync(["starter"]);
        await seller.ExecuteAsync(["buy", "rusty-blade"]);
        await seller.ExecuteAsync(["sell", "rusty-blade", "3000"]);

        // The server now indexes one active offer for the item at the ask.
        var offers = await observer.Offers.ListAsync();
        var offer = Assert.Single(offers,
            o => o.ItemName == "Rusty Blade" && o.AskSats == 3000 && o.Status == "active");

        // Buyer drives: register → buy the offer (id discovered from the server).
        await using var buyer = NewClient(FreshHome());
        await buyer.ExecuteAsync(["register", "CliBuyer"]);
        await buyer.ExecuteAsync(["buyoffer", offer.OfferId]);

        // The sale went through the client: the offer closed (no longer listed).
        var after = await observer.Offers.ListAsync();
        Assert.DoesNotContain(after, o => o.OfferId == offer.OfferId);
    }

    [Fact]
    public async Task Sell_Then_CancelOffer_ReturnsTheItem()
    {
        var observer = new ArkadeHeroesClient(_factory.CreateClient());

        await using var seller = NewClient(FreshHome());
        await seller.ExecuteAsync(["register", "CliCancel"]);
        await seller.ExecuteAsync(["starter"]);
        await seller.ExecuteAsync(["buy", "steel-saber"]);
        await seller.ExecuteAsync(["sell", "steel-saber", "5000"]);

        var offer = Assert.Single(
            await observer.Offers.ListAsync(),
            o => o.ItemName == "Steel Saber");

        await seller.ExecuteAsync(["canceloffer", offer.OfferId]);

        // Cancelled → no longer listed, and the item is back (the seller can re-list it).
        Assert.DoesNotContain(
            await observer.Offers.ListAsync(),
            o => o.OfferId == offer.OfferId);
        await seller.ExecuteAsync(["sell", "steel-saber", "4000"]); // must not throw — the unit is free again
        Assert.Contains(
            await observer.Offers.ListAsync(),
            o => o.ItemName == "Steel Saber" && o.AskSats == 4000);
    }

    [Fact]
    public async Task Sell_WhenTheListingFeeCannotBePaid_NeverEscrowsTheItem()
    {
        // Ordering invariant: the fee is paid BEFORE the asset is deposited. The deposit is an
        // irreversible send into the offer covenant — undoable only through the timelocked reclaim leaf —
        // whereas failing the fee costs nothing. Depositing first would strand the item in an offer that
        // can never go live. A fee above the simulated faucet balance makes payment impossible.
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.Configure<GameOptions>(
                o => o.OfferListingFeeSats = InMemoryChainService.FaucetSats + 1)));
        var chain = (InMemoryChainService)factory.Services.GetRequiredService<IChainService>();

        await using var seller = new GameClient("http://localhost", factory.CreateClient(), FreshHome());
        await seller.ExecuteAsync(["register", "CliBrokeLister"]);
        await seller.ExecuteAsync(["starter"]);
        await seller.ExecuteAsync(["buy", "rusty-blade"]);
        // The command cannot succeed — the point is the state it leaves behind, not how it reports failure.
        try { await seller.ExecuteAsync(["sell", "rusty-blade", "3000"]); } catch { /* expected */ }

        // The seller is the only registered player here, so any hero's owner is them.
        var http = factory.CreateClient();
        var heroes = await http.GetFromJsonAsync<List<HeroDto>>("/api/heroes");
        var sellerId = heroes!.First().OwnerId;

        // The unit is still in the seller's own wallet — nothing was escrowed behind an unpayable fee.
        Assert.Equal(1ul, await chain.GetItemAssetBalanceAsync(sellerId, "rusty-blade", default));
    }
}
