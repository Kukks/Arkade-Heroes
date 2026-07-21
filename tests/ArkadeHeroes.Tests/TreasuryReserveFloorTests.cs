using ArkadeHeroes.Chain;
using ArkadeHeroes.Server;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ArkadeHeroes.Tests;

/// <summary>
/// Faucet governor reserve floor: the daily faucet clamps its payout to (treasury balance − floor), so it can
/// never drain the treasury below the configured permanent reserve. Default 0 leaves every other test unchanged.
/// </summary>
public class TreasuryReserveFloorTests
{
    [Fact]
    public async Task Daily_NeverDrainsBelowTheReserveFloor()
    {
        const long Floor = 10_000;
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.Configure<GameOptions>(o => o.TreasuryReserveFloorSats = Floor)));
        var chain = (InMemoryChainService)factory.Services.GetRequiredService<IChainService>();
        chain.FundTreasury(Floor + 30);   // only 30 sats sit above the floor (the daily reward is more)

        var (player, _) = await factory.RegisterAsync("Gov-Floor");
        var claim = await player.Daily.ClaimAsync();

        Assert.Equal(30, claim.AwardedSats);                       // paid only the surplus above the floor
        Assert.Equal(Floor, await chain.TreasuryBalanceAsync());   // the reserve is never touched
    }

    [Fact]
    public async Task Daily_ReserveSeasonPot_HoldsBackTheSeasonPotFromEmission()
    {
        const long SeasonPot = 25_000;   // GameConfig.Default.SeasonPotBaseSats, no accrual yet
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.Configure<GameOptions>(o => o.ReserveSeasonPot = true)));
        var chain = (InMemoryChainService)factory.Services.GetRequiredService<IChainService>();
        chain.FundTreasury(SeasonPot + 30);   // only 30 sats sit above the reserved season pot

        var (player, _) = await factory.RegisterAsync("Gov-Season");
        var claim = await player.Daily.ClaimAsync();

        Assert.Equal(30, claim.AwardedSats);                          // only the surplus above the pot is emitted
        Assert.Equal(SeasonPot, await chain.TreasuryBalanceAsync());  // the season pot is never drained by the faucet
    }
}
