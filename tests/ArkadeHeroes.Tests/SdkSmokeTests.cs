using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ArkadeHeroes.Tests;

/// <summary>
/// The SDK's first real consumer: proves the typed transport + a couple of facades
/// resolve and round-trip against the in-memory server (register auto-sets the token;
/// starters mint; the roster reads back).
/// </summary>
public class SdkSmokeTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public SdkSmokeTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task Sdk_RegistersAndClaimsStarters_ThroughTypedFacades()
    {
        var sdk = new ArkadeHeroesClient(_factory.CreateClient());
        var player = await sdk.Players.RegisterAsync(
            new RegisterPlayerRequest("Sdk-Smoke", $"sim-wallet-{Guid.NewGuid():N}"));
        Assert.NotNull(player.Token);

        // Starter heroes carry a fee, so the typed facades have to walk quote → pay → claim.
        var quote = await sdk.Heroes.RequestStartersAsync();
        await sdk.PayInvoiceAsync(quote.Fee!.InvoiceId);
        var starters = await sdk.Heroes.ClaimStartersAsync();
        Assert.Equal(StarterPolicy.HeroCount, starters.Heroes.Count);

        var mine = await sdk.Heroes.MineAsync();
        Assert.Equal(StarterPolicy.HeroCount, mine.Count);
    }
}
