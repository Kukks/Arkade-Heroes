using ArkadeHeroes.Core.Progression;
using ArkadeHeroes.Server;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ArkadeHeroes.Tests;

/// <summary>
/// The Fancy discovery race: the FIRST hero ever to express a named Fancy set claims it for its owner,
/// permanently. Later finds can never displace the discoverer — they only add to the tally, so a hero can
/// still be "the 7th Sovereign". Pure bookkeeping: it never gates or changes a game outcome.
/// </summary>
public class FancyDiscoveryTests
{
    [Fact]
    public void FirstFinderKeepsTheClaim_LaterFindsOnlyBumpTheTally()
    {
        var store = new GameStore();

        store.RecordFancyFind("Sovereign", "hero-1", "Alpha", "player-1", 100);
        store.RecordFancyFind("Sovereign", "hero-2", "Beta", "player-2", 200);
        store.RecordFancyFind("Sovereign", "hero-3", "Gamma", "player-3", 300);

        var claim = store.FancyDiscoveries["Sovereign"];
        Assert.Equal("hero-1", claim.HeroId);      // the discoverer is immutable once claimed
        Assert.Equal("player-1", claim.OwnerId);
        Assert.Equal(100, claim.UnixSeconds);
        Assert.Equal(3, store.FancyFindCount["Sovereign"]);   // but every find counts toward the tally
    }

    // The record returns the stamped fact so the caller can persist it — and returns null when nothing was
    // stamped (a hero already recorded), so a repeat is never written twice.
    [Fact]
    public void RecordFancyFind_ReturnsTheStampedFact_AndNullWhenAlreadyStamped()
    {
        var store = new GameStore();

        var first = store.RecordFancyFind("Emberlord", "hero-1", "Alpha", "player-1", 100);
        Assert.NotNull(first);
        Assert.Equal(1, first!.Edition);
        Assert.Equal("Emberlord", first.Title);
        Assert.Equal("player-1", first.OwnerId);

        var second = store.RecordFancyFind("Emberlord", "hero-2", "Beta", "player-2", 200);
        Assert.Equal(2, second!.Edition);   // a later find still returns, with the next edition

        Assert.Null(store.RecordFancyFind("Emberlord", "hero-1", "Alpha", "player-1", 999));   // re-stamp: nothing to persist
    }

    // Rehydrating persisted finds must restore the discoverer, each hero's exact edition, AND the per-set
    // count — so the next LIVE find takes the next number instead of colliding on a second "#1".
    [Fact]
    public void LoadFancyFind_RebuildsDiscovererEditionAndCount_SoTheNextFindDoesNotCollide()
    {
        var store = new GameStore();
        store.LoadFancyFind(new FancyFind("Sovereign", "hero-1", "Alpha", "player-1", 100, 1));
        store.LoadFancyFind(new FancyFind("Sovereign", "hero-2", "Beta", "player-2", 200, 2));
        store.LoadFancyFind(new FancyFind("Sovereign", "hero-3", "Gamma", "player-3", 300, 3));

        Assert.Equal("player-1", store.FancyDiscoveries["Sovereign"].OwnerId);   // discoverer restored from edition #1
        Assert.Equal(2, store.FancyEditionByHero["hero-2"].Edition);             // each hero's edition restored
        Assert.Equal(3, store.FancyFindCount["Sovereign"]);                      // count restored

        // The next live find continues the sequence — #4, not a duplicate #1.
        Assert.Equal(4, store.RecordFancyFind("Sovereign", "hero-4", "Delta", "player-4", 400)!.Edition);
    }

    [Fact]
    public void EditionsAreStampedInDiscoveryOrder_AndNeverRenumbered()
    {
        var store = new GameStore();

        store.RecordFancyFind("Sovereign", "hero-1", "Alpha", "player-1", 100);
        store.RecordFancyFind("Sovereign", "hero-2", "Beta", "player-2", 200);
        store.RecordFancyFind("Sovereign", "hero-3", "Gamma", "player-3", 300);

        Assert.Equal(1, store.FancyEditionByHero["hero-1"].Edition);   // the discoverer is #1
        Assert.Equal(2, store.FancyEditionByHero["hero-2"].Edition);
        Assert.Equal(3, store.FancyEditionByHero["hero-3"].Edition);
        Assert.Equal("Sovereign", store.FancyEditionByHero["hero-2"].Title);

        // A repeat find for a hero already stamped must not mint it a second edition — nor inflate the
        // tally, or later heroes would be numbered past the number of heroes that actually exist.
        store.RecordFancyFind("Sovereign", "hero-2", "Beta", "player-2", 400);
        Assert.Equal(2, store.FancyEditionByHero["hero-2"].Edition);
        Assert.Equal(3, store.FancyFindCount["Sovereign"]);
    }

    [Fact]
    public void EditionsAreIndependentPerSet()
    {
        var store = new GameStore();
        store.RecordFancyFind("Oracle", "hero-o1", "O1", "p", 10);
        store.RecordFancyFind("Duelist", "hero-d1", "D1", "p", 20);

        // Each set has its own #1 — a Duelist isn't numbered behind the Oracles.
        Assert.Equal(1, store.FancyEditionByHero["hero-o1"].Edition);
        Assert.Equal(1, store.FancyEditionByHero["hero-d1"].Edition);
    }

    [Fact]
    public void SetsAreClaimedIndependently()
    {
        var store = new GameStore();
        store.RecordFancyFind("Oracle", "hero-o", "Oracle-ish", "player-a", 10);
        store.RecordFancyFind("Duelist", "hero-d", "Duelist-ish", "player-b", 20);

        Assert.Equal("player-a", store.FancyDiscoveries["Oracle"].OwnerId);
        Assert.Equal("player-b", store.FancyDiscoveries["Duelist"].OwnerId);
        Assert.DoesNotContain("Sovereign", store.FancyDiscoveries.Keys);   // untouched sets stay unclaimed
    }

    [Fact]
    public async Task Board_ListsEveryCatalogTitle_IncludingUndiscoveredOnes()
    {
        // Fresh store: the race is the point, so an unclaimed set must still appear (as "up for grabs")
        // rather than being omitted — otherwise players can't see what's left to find.
        using var factory = new WebApplicationFactory<Program>();
        var (alice, _) = await factory.RegisterAsync("Fancy-Board");

        var board = await alice.Leaderboard.FancyDiscoveriesAsync();

        Assert.Equal(FancySets.AllTitles.Count, board.Count);
        Assert.Equal(FancySets.AllTitles.OrderBy(t => t), board.Select(b => b.Title).OrderBy(t => t));
        foreach (var row in board.Where(b => b.HeroId is null))
        {
            Assert.Null(row.OwnerId);          // an unclaimed set carries no discoverer…
            Assert.Equal(0, row.FoundCount);   // …and nothing has ever expressed it
        }
    }
}
