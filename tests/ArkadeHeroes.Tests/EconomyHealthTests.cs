using ArkadeHeroes.Chain;
using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Server;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ArkadeHeroes.Tests;

/// <summary>
/// Treasury-health telemetry: GET /api/economy/health surfaces the treasury balance, outflow tallied
/// by category, and season accrual — read-only observability, the economy control plane. Here a real
/// daily-faucet payout is tallied exactly once under the "daily" tag and the balance drops by it.
/// </summary>
public class EconomyHealthTests
{
    [Fact]
    public async Task Health_TalliesDailyPayoutOnce_AndReflectsBalance()
    {
        using var factory = new WebApplicationFactory<Program>();
        var chain = (InMemoryChainService)factory.Services.GetRequiredService<IChainService>();
        chain.FundTreasury(50_000);

        var (player, _) = await factory.RegisterAsync("Econ-Daily");

        var before = await player.Economy.HealthAsync();
        Assert.Equal(50_000, before.TreasuryBalanceSats);
        Assert.Equal(0, before.TotalOutflowSats);
        Assert.Empty(before.OutflowByTag);

        var claim = await player.Daily.ClaimAsync();
        Assert.True(claim.AwardedSats > 0, "the funded treasury should cover the daily reward");

        var after = await player.Economy.HealthAsync();
        Assert.Equal(claim.AwardedSats, after.OutflowByTag.GetValueOrDefault("daily"));   // tagged, once
        Assert.Equal(claim.AwardedSats, after.TotalOutflowSats);                          // no other outflow
        Assert.Equal(50_000 - claim.AwardedSats, after.TreasuryBalanceSats);              // balance dropped by exactly the payout
    }

    [Fact]
    public async Task Health_TalliesItemPurchaseInflow_Once()
    {
        using var factory = new WebApplicationFactory<Program>();
        var (player, _) = await factory.RegisterAsync("Econ-Inflow");
        await player.BuyItemAsync("rusty-blade");   // buy → pay → claim; the item fee is captured on claim

        var h1 = await player.Economy.HealthAsync();
        Assert.True(h1.InflowByTag.GetValueOrDefault("item") > 0, "the item fee should be tallied as inflow");
        Assert.Equal(h1.InflowByTag.Values.Sum(), h1.TotalInflowSats);

        // Deduped by invoice id: re-reading (which reconciles offers etc.) never re-counts the same fee.
        var h2 = await player.Economy.HealthAsync();
        Assert.Equal(h1.InflowByTag["item"], h2.InflowByTag["item"]);
    }

    [Fact]
    public async Task Health_TalliesGauntletFeeInflow()
    {
        using var factory = new WebApplicationFactory<Program>();
        var (client, _) = await factory.RegisterAsync("Econ-Gauntlet");
        var hero = (await client.ClaimStartersAsync())[0];

        var open = await client.Gauntlet.OpenAsync(hero.Id);
        await client.Dev.PayInvoiceAsync(new { InvoiceId = open.FeeInvoice.InvoiceId });
        await client.Gauntlet.RunAsync(open.GauntletId, "econ-nonce");

        var health = await client.Economy.HealthAsync();
        Assert.Equal(open.FeeInvoice.AmountSats, health.InflowByTag.GetValueOrDefault("gauntlet"));
    }

    [Fact]
    public async Task Health_TalliesDeathMatchFeeInflow()
    {
        using var factory = new WebApplicationFactory<Program>();
        var (alice, _) = await factory.RegisterAsync("Econ-DM-A");
        var (bob, _) = await factory.RegisterAsync("Econ-DM-B");
        var aliceHero = (await alice.ClaimStartersAsync())[0].Id;
        var bobHero = (await bob.ClaimStartersAsync())[0].Id;

        var open = await alice.DeathMatch.OpenAsync(new DeathMatchOpenRequest(aliceHero, bobHero, Absorb: false));
        await alice.Dev.FundDeathMatchEscrowAsync(new { DeathMatchId = open.DeathMatchId, Role = "challenger" });
        await alice.Dev.PayInvoiceAsync(new { InvoiceId = open.FeeInvoice!.InvoiceId });
        var accept = await bob.DeathMatch.AcceptAsync(open.DeathMatchId);
        await bob.Dev.FundDeathMatchEscrowAsync(new { DeathMatchId = open.DeathMatchId, Role = "defender" });
        await bob.Dev.PayInvoiceAsync(new { InvoiceId = accept.FeeInvoice!.InvoiceId });
        await alice.DeathMatch.SettleAsync(open.DeathMatchId, new DeathMatchSettleRequest("dm-nonce"));

        var health = await alice.Economy.HealthAsync();
        Assert.True(health.InflowByTag.GetValueOrDefault("deathmatch") > 0, "both death-match fees are tallied at settle");
    }

    [Fact]
    public async Task Health_TalliesMatchFeeInflow()
    {
        using var factory = new WebApplicationFactory<Program>();
        var (alice, _) = await factory.RegisterAsync("Econ-M-A");
        var (bob, _) = await factory.RegisterAsync("Econ-M-B");
        var aliceHero = (await alice.ClaimStartersAsync())[0].Id;
        var bobHero = (await bob.ClaimStartersAsync())[0].Id;

        var open = await alice.Matches.OpenAsync(new OpenMatchRequest(aliceHero, bobHero, 1000, "invoice"));
        await alice.Dev.PayInvoiceAsync(new { InvoiceId = open.StakeInvoice!.InvoiceId });
        await alice.Dev.PayInvoiceAsync(new { InvoiceId = open.MatchFeeInvoice!.InvoiceId });
        var accept = await bob.Matches.AcceptAsync(open.MatchId);
        await bob.Dev.PayInvoiceAsync(new { InvoiceId = accept.StakeInvoice!.InvoiceId });
        await bob.Dev.PayInvoiceAsync(new { InvoiceId = accept.MatchFeeInvoice!.InvoiceId });
        await alice.Matches.FightAsync(open.MatchId, new FightRequest("duel-nonce"));

        var health = await alice.Economy.HealthAsync();
        Assert.True(health.InflowByTag.GetValueOrDefault("match") > 0, "both match fees are tallied at the fight");
    }

    [Fact]
    public async Task Health_TalliesSquadFeeInflow()
    {
        using var factory = new WebApplicationFactory<Program>();
        var (alice, _) = await factory.RegisterAsync("Econ-Sq-A");
        var (bob, _) = await factory.RegisterAsync("Econ-Sq-B");
        var mine = await SquadLineup(alice);
        var theirs = await SquadLineup(bob);

        var open = await alice.Squad.OpenAsync(new OpenSquadMatchRequest(mine, theirs, 1000, "invoice"));
        await alice.Dev.PayInvoiceAsync(new { InvoiceId = open.StakeInvoice!.InvoiceId });
        await alice.Dev.PayInvoiceAsync(new { InvoiceId = open.MatchFeeInvoice!.InvoiceId });
        var accept = await bob.Squad.AcceptAsync(open.MatchId);
        await bob.Dev.PayInvoiceAsync(new { InvoiceId = accept.StakeInvoice!.InvoiceId });
        await bob.Dev.PayInvoiceAsync(new { InvoiceId = accept.MatchFeeInvoice!.InvoiceId });
        await alice.Squad.ResolveAsync(open.MatchId, new FightRequest("squad-nonce"));

        var health = await alice.Economy.HealthAsync();
        Assert.True(health.InflowByTag.GetValueOrDefault("squad-fee") > 0, "both squad match fees are tallied at settle");
    }

    static async Task<List<string>> SquadLineup(ArkadeHeroesClient c)
    {
        var ids = (await c.ClaimStartersAsync()).Select(h => h.Id).ToList();
        ids.Add((await c.Dev.MintHeroAsync()).Id);
        return ids.Take(3).ToList();
    }
}
