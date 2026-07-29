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
        // A tier-1 item, so a level-1 starter clears ItemCatalog's level gate and the ONLY thing left to
        // reject is the missing unit — which is what this test is about.
        var ex = await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => client.Heroes.EquipAsync(heroes[0].Id, new EquipRequest("lucky-feather")));
        Assert.Contains("buy", ex.Message);
    }

    [Fact]
    public async Task EquipBelowTheItemsLevelGateIsRejected_EvenWhenTheUnitIsHeld()
    {
        // The whale case the gate exists for: owning the top set does not put it on a level-1 hero.
        var (client, _) = await _factory.RegisterAsync("I-Gated");
        var heroes = await client.ClaimStartersAsync();

        await client.BuyItemAsync("covenant-plate");
        var ex = await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => client.Heroes.EquipAsync(heroes[0].Id, new EquipRequest("covenant-plate")));
        Assert.Contains("level-10", ex.Message);

        // …while the tier a starter HAS grown into still equips, so the gate delays the top set rather than
        // locking a new player out of gear altogether.
        await client.BuyItemAsync("padded-vest");
        await client.Heroes.EquipAsync(heroes[0].Id, new EquipRequest("padded-vest"));
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

        // Tier-1 again: this test is about UNITS, so it uses gear a level-1 starter can actually wear.
        await client.BuyItemAsync("padded-vest");
        var second = await client.BuyItemAsync("padded-vest");
        Assert.Equal(2UL, second.UnitsHeld);

        await client.Heroes.EquipAsync(heroes[0].Id, new EquipRequest("padded-vest"));
        await client.Heroes.EquipAsync(heroes[1].Id, new EquipRequest("padded-vest"));
    }

    [Fact]
    public async Task MineListsOnlyOwnedItems()
    {
        var (client, _) = await _factory.RegisterAsync("I-Owned");

        // Nothing owned before any purchase.
        Assert.Empty(await client.Items.MineAsync());

        await client.BuyItemAsync("steel-saber");
        await client.BuyItemAsync("lucky-feather");

        var owned = await client.Items.MineAsync();
        Assert.Contains("steel-saber", owned);
        Assert.Contains("lucky-feather", owned);
        Assert.DoesNotContain("covenant-plate", owned);   // never bought
    }
}
