using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Heroes;
using ArkadeHeroes.Server;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ArkadeHeroes.Tests;

/// <summary>
/// The /founders board surfaces generation-0 heroes only — the starter-issued originals, ranked by level.
/// A pure filter over the store (Generation == 0), the same trustless basis as /rarest and /fancies.
/// </summary>
public class FoundersBoardTests
{
    [Fact]
    public async Task Founders_ReturnsGenerationZeroOnly()
    {
        using var factory = new WebApplicationFactory<Program>();
        var store = factory.Services.GetRequiredService<GameStore>();
        var client = new ArkadeHeroesClient(factory.CreateClient());

        store.Heroes["gen0"] = new Hero { Id = "gen0", OwnerId = "p", Name = "Origin", Genome = new Genome(new byte[32]), Generation = 0, Level = 1 };
        store.Heroes["gen2"] = new Hero { Id = "gen2", OwnerId = "p", Name = "Bred", Genome = new Genome(new byte[32]), Generation = 2, Level = 1 };

        var board = await client.Leaderboard.FoundersAsync();

        Assert.Contains(board, h => h.Id == "gen0");        // the original mint is a founder
        Assert.DoesNotContain(board, h => h.Id == "gen2");  // a bred (gen-2) hero is not
    }

    // Every gen-0 genome has zeroed trait bytes, so rarity can't order the board — it's level, then a stable
    // id tiebreak. Without the tiebreak, equal-level founders would reshuffle between calls (dictionary order).
    [Fact]
    public async Task Founders_RankByLevel_WithAStableTiebreakAmongEquals()
    {
        using var factory = new WebApplicationFactory<Program>();
        var store = factory.Services.GetRequiredService<GameStore>();
        var client = new ArkadeHeroesClient(factory.CreateClient());

        store.Heroes["f-high"] = new Hero { Id = "f-high", OwnerId = "p", Name = "High", Genome = new Genome(new byte[32]), Generation = 0, Level = 9 };
        store.Heroes["f-b"] = new Hero { Id = "f-b", OwnerId = "p", Name = "B", Genome = new Genome(new byte[32]), Generation = 0, Level = 3 };
        store.Heroes["f-a"] = new Hero { Id = "f-a", OwnerId = "p", Name = "A", Genome = new Genome(new byte[32]), Generation = 0, Level = 3 };

        var ids = (await client.Leaderboard.FoundersAsync()).Select(h => h.Id).ToList();
        Assert.Equal(new[] { "f-high", "f-a", "f-b" }, ids);   // level desc, then id ascending among the level-3 pair

        // Stable across calls — the tiebreak is not dictionary luck.
        var again = (await client.Leaderboard.FoundersAsync()).Select(h => h.Id).ToList();
        Assert.Equal(ids, again);
    }
}
