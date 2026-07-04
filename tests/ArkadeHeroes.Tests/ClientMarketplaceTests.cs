using System.Net.Http.Json;
using ArkadeHeroes.Client;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Mvc.Testing;

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
        var observer = _factory.CreateClient();

        // Seller drives: register → starter → buy an item → list it for sale.
        await using var seller = NewClient(FreshHome());
        await seller.ExecuteAsync(["register", "CliSeller"]);
        await seller.ExecuteAsync(["starter"]);
        await seller.ExecuteAsync(["buy", "rusty-blade"]);
        await seller.ExecuteAsync(["sell", "rusty-blade", "3000"]);

        // The server now indexes one active offer for the item at the ask.
        var offers = await observer.GetFromJsonAsync<List<OfferDto>>("/api/offers");
        var offer = Assert.Single(offers!,
            o => o.ItemName == "Rusty Blade" && o.AskSats == 3000 && o.Status == "active");

        // Buyer drives: register → buy the offer (id discovered from the server).
        await using var buyer = NewClient(FreshHome());
        await buyer.ExecuteAsync(["register", "CliBuyer"]);
        await buyer.ExecuteAsync(["buyoffer", offer.OfferId]);

        // The sale went through the client: the offer closed (no longer listed).
        var after = await observer.GetFromJsonAsync<List<OfferDto>>("/api/offers");
        Assert.DoesNotContain(after!, o => o.OfferId == offer.OfferId);
    }

    [Fact]
    public async Task Sell_Then_CancelOffer_ReturnsTheItem()
    {
        var observer = _factory.CreateClient();

        await using var seller = NewClient(FreshHome());
        await seller.ExecuteAsync(["register", "CliCancel"]);
        await seller.ExecuteAsync(["starter"]);
        await seller.ExecuteAsync(["buy", "steel-saber"]);
        await seller.ExecuteAsync(["sell", "steel-saber", "5000"]);

        var offer = Assert.Single(
            (await observer.GetFromJsonAsync<List<OfferDto>>("/api/offers"))!,
            o => o.ItemName == "Steel Saber");

        await seller.ExecuteAsync(["canceloffer", offer.OfferId]);

        // Cancelled → no longer listed, and the item is back (the seller can re-list it).
        Assert.DoesNotContain(
            (await observer.GetFromJsonAsync<List<OfferDto>>("/api/offers"))!,
            o => o.OfferId == offer.OfferId);
        await seller.ExecuteAsync(["sell", "steel-saber", "4000"]); // must not throw — the unit is free again
        Assert.Contains(
            (await observer.GetFromJsonAsync<List<OfferDto>>("/api/offers"))!,
            o => o.ItemName == "Steel Saber" && o.AskSats == 4000);
    }
}
