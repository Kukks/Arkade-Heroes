using System.Net.Http.Json;
using ArkadeHeroes.Client;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ArkadeHeroes.Tests;

/// <summary>
/// Extends the client integration coverage beyond marketplace to the CORE game
/// loop — breed, friendly fight, and hero transfer — driving the REAL
/// <see cref="GameClient"/> command dispatch against a fresh in-memory server per
/// test and asserting on observable server state. Catches client-side dispatch /
/// wiring / hero-reference bugs the server-only tests can't (the same class as
/// the fixed first-run crash).
/// </summary>
public class ClientGameLoopTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory = new();
    private readonly HttpClient _observer;
    private readonly List<string> _homeDirs = [];

    public ClientGameLoopTests() => _observer = _factory.CreateClient();

    public void Dispose()
    {
        _observer.Dispose();
        _factory.Dispose();
        foreach (var d in _homeDirs)
            try { if (Directory.Exists(d)) Directory.Delete(d, recursive: true); } catch { /* windows lock */ }
    }

    private string FreshHome()
    {
        var home = Path.Combine(Path.GetTempPath(), $"ah-loop-{Guid.NewGuid():N}");
        _homeDirs.Add(home);
        return home;
    }

    private GameClient NewClient(string home) => new("http://localhost", _factory.CreateClient(), home);

    private async Task<List<HeroDto>> HeroesAsync() =>
        (await _observer.GetFromJsonAsync<List<HeroDto>>("/api/heroes"))!;

    [Fact]
    public async Task Breed_ThroughTheClient_ProducesGen1Child_AndStoresReceipt()
    {
        var home = FreshHome();
        await using var alice = NewClient(home);
        await alice.ExecuteAsync(["register", "LoopAlice"]);
        await alice.ExecuteAsync(["starter"]);
        await alice.ExecuteAsync(["mine"]);        // populate the client's list for '1'/'2' refs
        await alice.ExecuteAsync(["breed", "1", "2"]);

        // Fresh server → the only generation-1 hero is alice's child.
        Assert.Contains(await HeroesAsync(), h => h.Generation == 1);

        // The client stored the signed breed receipt locally (portable progression).
        Assert.True(File.Exists(Path.Combine(home, "arkade-heroes-receipts.json")),
            "the breed receipt should be stored in the client's data dir");
    }

    [Fact]
    public async Task Merge_ThroughTheClient_FusesTwoHeroesIntoOne()
    {
        var home = FreshHome();
        await using var alice = NewClient(home);
        await alice.ExecuteAsync(["register", "LoopMergeA"]);
        await alice.ExecuteAsync(["starter"]);
        await alice.ExecuteAsync(["mine"]); // populate the client's list for the '1'/'2' refs
        var before = (await HeroesAsync()).Select(h => h.Id).ToHashSet();
        Assert.Equal(2, before.Count);

        await alice.ExecuteAsync(["merge", "1", "2"]);

        // Both starters are consumed; exactly one NEW fused hero remains (fresh server).
        var after = await HeroesAsync();
        var fused = Assert.Single(after);
        Assert.DoesNotContain(fused.Id, before);
        // The client stored the signed merge receipt locally.
        Assert.True(File.Exists(Path.Combine(home, "arkade-heroes-receipts.json")),
            "the merge receipt should be stored in the client's data dir");
    }

    [Fact]
    public async Task FriendlyFight_ThroughTheClient_ResolvesWithAReceipt()
    {
        var aliceHome = FreshHome();
        await using var alice = NewClient(aliceHome);
        await alice.ExecuteAsync(["register", "LoopFightA"]);
        await alice.ExecuteAsync(["starter"]);
        var aliceId = (await HeroesAsync())[0].OwnerId;

        await using var bob = NewClient(FreshHome());
        await bob.ExecuteAsync(["register", "LoopFightB"]);
        await bob.ExecuteAsync(["starter"]);

        var all = await HeroesAsync();
        var aliceHero = all.First(h => h.OwnerId == aliceId);
        var bobHero = all.First(h => h.OwnerId != aliceId);

        // 'heroes' lists ALL heroes into the client's ref list, so a cross-player
        // opponent id resolves; a friendly fight needs no stake or opponent action.
        await alice.ExecuteAsync(["heroes"]);
        await alice.ExecuteAsync(["fight", aliceHero.Id, bobHero.Id]);

        var match = Assert.Single(await _observer.GetFromJsonAsync<List<MatchDto>>("/api/matches") ?? []);
        Assert.Equal("resolved", match.Status);
        Assert.NotNull(match.Result);
        Assert.True(File.Exists(Path.Combine(aliceHome, "arkade-heroes-receipts.json")));
    }

    [Fact]
    public async Task Transfer_ThroughTheClient_MovesHeroToRecipient()
    {
        await using var alice = NewClient(FreshHome());
        await alice.ExecuteAsync(["register", "LoopXferA"]);
        await alice.ExecuteAsync(["starter"]);
        var aliceId = (await HeroesAsync())[0].OwnerId;

        await using var bob = NewClient(FreshHome());
        await bob.ExecuteAsync(["register", "LoopXferB"]);
        await bob.ExecuteAsync(["starter"]);
        var bobId = (await HeroesAsync()).First(h => h.OwnerId != aliceId).OwnerId;

        // Alice transfers one of her heroes to Bob (InMemory: dev asset move + verify).
        await alice.ExecuteAsync(["mine"]);
        var aliceHero = (await HeroesAsync()).First(h => h.OwnerId == aliceId);
        await alice.ExecuteAsync(["transfer", aliceHero.Id, bobId]);

        // The server now shows the hero owned by Bob.
        var moved = (await HeroesAsync()).Single(h => h.Id == aliceHero.Id);
        Assert.Equal(bobId, moved.OwnerId);
    }

    [Fact]
    public async Task WageredDuel_ThroughTheClient_ResolvesTheStakedMatch()
    {
        await using var alice = NewClient(FreshHome());
        await alice.ExecuteAsync(["register", "LoopDuelA"]);
        await alice.ExecuteAsync(["starter"]);
        var aliceId = (await HeroesAsync())[0].OwnerId;

        await using var bob = NewClient(FreshHome());
        await bob.ExecuteAsync(["register", "LoopDuelB"]);
        await bob.ExecuteAsync(["starter"]);

        var all = await HeroesAsync();
        var aliceHero = all.First(h => h.OwnerId == aliceId);
        var bobHero = all.First(h => h.OwnerId != aliceId);

        // The marquee flow end-to-end through the client: challenge auto-stakes
        // (invoice mode → dev pay-invoice), bob accepts (auto-stakes his side),
        // alice resolves the duel — the whole staked lifecycle over the real
        // command dispatch, not just the server API.
        await alice.ExecuteAsync(["heroes"]);
        await alice.ExecuteAsync(["challenge", aliceHero.Id, bobHero.Id, "1000"]);

        var opened = Assert.Single(await _observer.GetFromJsonAsync<List<MatchDto>>("/api/matches") ?? []);
        await bob.ExecuteAsync(["accept", opened.MatchId]);
        await alice.ExecuteAsync(["duel", opened.MatchId]);

        var resolved = Assert.Single(await _observer.GetFromJsonAsync<List<MatchDto>>("/api/matches") ?? []);
        Assert.Equal("resolved", resolved.Status);
        Assert.Equal(1000, resolved.WagerSats);
        Assert.NotNull(resolved.Result);
    }

    [Fact]
    public async Task HeroSale_ThroughTheClient_TransfersOwnershipToTheBuyer()
    {
        await using var seller = NewClient(FreshHome());
        await seller.ExecuteAsync(["register", "LoopSellHeroA"]);
        await seller.ExecuteAsync(["starter"]);
        await seller.ExecuteAsync(["mine"]); // populate the client's list for the hero ref
        var sellerId = (await HeroesAsync())[0].OwnerId;
        var hero = (await HeroesAsync()).First(h => h.OwnerId == sellerId);

        await seller.ExecuteAsync(["sellhero", hero.Id, "15000"]);

        var offer = Assert.Single(await _observer.GetFromJsonAsync<List<OfferDto>>("/api/offers") ?? []);
        Assert.Equal("hero", offer.Kind);
        Assert.Equal(hero.Name, offer.ItemName);

        await using var buyer = NewClient(FreshHome());
        await buyer.ExecuteAsync(["register", "LoopSellHeroB"]);
        await buyer.ExecuteAsync(["buyhero", offer.OfferId]);

        // Ownership moved off the seller (to the buyer, who ran buyhero + claimed).
        var moved = (await HeroesAsync()).Single(h => h.Id == hero.Id);
        Assert.NotEqual(sellerId, moved.OwnerId);
        Assert.DoesNotContain(await _observer.GetFromJsonAsync<List<OfferDto>>("/api/offers") ?? [],
            o => o.OfferId == offer.OfferId);
    }
}
