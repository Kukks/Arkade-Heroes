using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ArkadeHeroes.Tests;

/// <summary>Item units are fungible assets: buying delivers one, equipping allocates it, one unit backs one hero.</summary>
public class ItemAssetTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ItemAssetTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private async Task<(HttpClient Client, List<HeroDto> Heroes)> PlayerWithStartersAsync(string name)
    {
        var client = _factory.CreateClient();
        var register = await client.PostAsJsonAsync("/api/players", new RegisterPlayerRequest(name));
        var player = (await register.Content.ReadFromJsonAsync<PlayerDto>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", player.Token);
        var starter = await client.PostAsync("/api/heroes/starter", null);
        var heroes = (await starter.Content.ReadFromJsonAsync<StarterResponse>())!.Heroes.ToList();
        return (client, heroes);
    }

    [Fact]
    public async Task EquipWithoutHoldingIsRejected()
    {
        var (client, heroes) = await PlayerWithStartersAsync("I-NoUnit");
        var response = await client.PostAsJsonAsync($"/api/heroes/{heroes[0].Id}/equip",
            new EquipRequest("steel-saber"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Contains("buy", error!.Error);
    }

    [Fact]
    public async Task OneUnitBacksOnlyOneHero()
    {
        var (client, heroes) = await PlayerWithStartersAsync("I-OneUnit");

        (await client.PostAsync("/api/items/lucky-feather/buy", null)).EnsureSuccessStatusCode();

        // First hero equips fine.
        (await client.PostAsJsonAsync($"/api/heroes/{heroes[0].Id}/equip",
            new EquipRequest("lucky-feather"))).EnsureSuccessStatusCode();

        // Second hero can't use the same unit.
        var second = await client.PostAsJsonAsync($"/api/heroes/{heroes[1].Id}/equip",
            new EquipRequest("lucky-feather"));
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);

        // Re-equipping the same hero's same slot is a no-op, not a double allocation.
        (await client.PostAsJsonAsync($"/api/heroes/{heroes[0].Id}/equip",
            new EquipRequest("lucky-feather"))).EnsureSuccessStatusCode();

        // Unequip frees the unit for the second hero.
        (await client.PostAsJsonAsync($"/api/heroes/{heroes[0].Id}/unequip",
            new UnequipRequest("Trinket"))).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync($"/api/heroes/{heroes[1].Id}/equip",
            new EquipRequest("lucky-feather"))).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task BuyingMoreUnitsAllowsMoreHeroes()
    {
        var (client, heroes) = await PlayerWithStartersAsync("I-TwoUnits");

        (await client.PostAsync("/api/items/swift-anklet/buy", null)).EnsureSuccessStatusCode();
        var secondBuy = (await (await client.PostAsync("/api/items/swift-anklet/buy", null))
            .Content.ReadFromJsonAsync<BuyItemResponse>())!;
        Assert.Equal(2UL, secondBuy.UnitsHeld);

        (await client.PostAsJsonAsync($"/api/heroes/{heroes[0].Id}/equip",
            new EquipRequest("swift-anklet"))).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync($"/api/heroes/{heroes[1].Id}/equip",
            new EquipRequest("swift-anklet"))).EnsureSuccessStatusCode();
    }
}
