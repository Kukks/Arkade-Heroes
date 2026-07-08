using ArkadeHeroes.Client.Sdk;
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
        var invoice = (await client.Items.BuyAsync("steel-saber")).Invoice;

        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => client.Items.ClaimAsync(new ClaimItemRequest(invoice.InvoiceId)));
    }

    [Fact]
    public async Task EquipWithoutHoldingIsRejected()
    {
        var (client, _) = await _factory.RegisterAsync("I-NoUnit");
        var heroes = await client.ClaimStartersAsync();
        var ex = await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => client.Heroes.EquipAsync(heroes[0].Id, new EquipRequest("steel-saber")));
        Assert.Contains("buy", ex.Message);
    }

    [Fact]
    public async Task OneUnitBacksOnlyOneHero()
    {
        var (client, _) = await _factory.RegisterAsync("I-OneUnit");
        var heroes = await client.ClaimStartersAsync();

        await client.BuyItemAsync("lucky-feather");

        await client.Heroes.EquipAsync(heroes[0].Id, new EquipRequest("lucky-feather"));

        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => client.Heroes.EquipAsync(heroes[1].Id, new EquipRequest("lucky-feather")));

        // Re-equipping the same hero's same slot is a no-op, not a double allocation.
        await client.Heroes.EquipAsync(heroes[0].Id, new EquipRequest("lucky-feather"));

        // Unequip frees the unit for the second hero.
        await client.Heroes.UnequipAsync(heroes[0].Id, new UnequipRequest("Trinket"));
        await client.Heroes.EquipAsync(heroes[1].Id, new EquipRequest("lucky-feather"));
    }

    [Fact]
    public async Task BuyingMoreUnitsAllowsMoreHeroes()
    {
        var (client, _) = await _factory.RegisterAsync("I-TwoUnits");
        var heroes = await client.ClaimStartersAsync();

        await client.BuyItemAsync("swift-anklet");
        var second = await client.BuyItemAsync("swift-anklet");
        Assert.Equal(2UL, second.UnitsHeld);

        await client.Heroes.EquipAsync(heroes[0].Id, new EquipRequest("swift-anklet"));
        await client.Heroes.EquipAsync(heroes[1].Id, new EquipRequest("swift-anklet"));
    }
}
