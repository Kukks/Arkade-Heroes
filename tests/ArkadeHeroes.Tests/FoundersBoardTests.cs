using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Heroes;
using ArkadeHeroes.Server;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ArkadeHeroes.Tests;

/// <summary>
/// The /founders board surfaces generation-0 heroes only — the original mints, the scarcest lineage.
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
}
