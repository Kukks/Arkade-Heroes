using System.Net;
using System.Net.Http.Json;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ArkadeHeroes.Tests;

/// <summary>
/// Item units are fungible assets bought non-custodially: invoice → the
/// player's wallet pays → claim delivers the unit; equipping allocates a held
/// unit and one unit backs at most one hero.
/// </summary>
public class ItemAssetTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ItemAssetTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task ClaimWithoutPaymentIsRejected()
    {
        var (client, _) = await _factory.RegisterAsync("I-Unpaid");
        var invoiceResponse = await client.PostAsync("/api/items/steel-saber/buy", null);
        invoiceResponse.EnsureSuccessStatusCode();
        var invoice = (await invoiceResponse.Content.ReadFromJsonAsync<ItemInvoiceResponse>())!.Invoice;

        var claim = await client.PostAsJsonAsync("/api/items/claim", new ClaimItemRequest(invoice.InvoiceId));
        Assert.Equal(HttpStatusCode.BadRequest, claim.StatusCode);
    }

    [Fact]
    public async Task EquipWithoutHoldingIsRejected()
    {
        var (client, _) = await _factory.RegisterAsync("I-NoUnit");
        var heroes = await client.ClaimStartersAsync();
        var response = await client.PostAsJsonAsync($"/api/heroes/{heroes[0].Id}/equip",
            new EquipRequest("steel-saber"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Contains("buy", error!.Error);
    }

    [Fact]
    public async Task OneUnitBacksOnlyOneHero()
    {
        var (client, _) = await _factory.RegisterAsync("I-OneUnit");
        var heroes = await client.ClaimStartersAsync();

        await client.BuyItemAsync("lucky-feather");

        (await client.PostAsJsonAsync($"/api/heroes/{heroes[0].Id}/equip",
            new EquipRequest("lucky-feather"))).EnsureSuccessStatusCode();

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
        var (client, _) = await _factory.RegisterAsync("I-TwoUnits");
        var heroes = await client.ClaimStartersAsync();

        await client.BuyItemAsync("swift-anklet");
        var second = await client.BuyItemAsync("swift-anklet");
        Assert.Equal(2UL, second.UnitsHeld);

        (await client.PostAsJsonAsync($"/api/heroes/{heroes[0].Id}/equip",
            new EquipRequest("swift-anklet"))).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync($"/api/heroes/{heroes[1].Id}/equip",
            new EquipRequest("swift-anklet"))).EnsureSuccessStatusCode();
    }
}
