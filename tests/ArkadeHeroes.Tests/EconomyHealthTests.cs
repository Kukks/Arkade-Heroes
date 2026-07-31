using ArkadeHeroes.Chain;
using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Heroes;
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
        using var factory = new WebApplicationFactory<Program>().WithDailyFaucetOpen().WithFreeStarters();
        var chain = (InMemoryChainService)factory.Services.GetRequiredService<IChainService>();
        chain.FundTreasury(50_000);

        var (player, _) = await factory.RegisterAsync("Econ-Daily");
        await player.ClaimStartersAsync();   // the faucet only pays players who own a hero

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

    // Sats are the insolvency gauge; hero supply is the inflation gauge — heroes have no hard cap. The
    // telemetry must surface both, and separate the free-starter float (gen-0) from the bred total.
    [Fact]
    public async Task Health_ReportsHeroSupply_AndSeparatesTheGen0StarterFloat()
    {
        using var factory = new WebApplicationFactory<Program>();
        var (player, playerDto) = await factory.RegisterAsync("Econ-Supply");

        var fresh = await player.Economy.HealthAsync();
        Assert.Equal(0, fresh.HeroSupply);
        Assert.Equal(0, fresh.Gen0Supply);

        await player.ClaimStartersAsync();   // two gen-0 starters — the free float
        var afterStarters = await player.Economy.HealthAsync();
        Assert.Equal(2, afterStarters.HeroSupply);
        Assert.Equal(2, afterStarters.Gen0Supply);

        // A bred hero (gen > 0) grows the supply but NOT the starter float — the two must not conflate.
        var store = factory.Services.GetRequiredService<GameStore>();
        store.Heroes["bred-1"] = new Hero
        {
            Id = "bred-1", OwnerId = playerDto.PlayerId, Name = "Child", Level = 1,
            Genome = new Genome(new byte[32]), Generation = 1,
        };

        var afterBreed = await player.Economy.HealthAsync();
        Assert.Equal(3, afterBreed.HeroSupply);   // total climbs
        Assert.Equal(2, afterBreed.Gen0Supply);   // free float unchanged
    }

    // A flat HeroSupply can't tell "nothing happened" from "mints and burns netted out" — the churn is what
    // the mint/burn counts surface. BOTH are counted at their own choke point: mints at the mint, burns at
    // each burn site (what the merge/absorb/death-match flows do alongside removing the hero).
    [Fact]
    public async Task Health_CountsMints_AndCountsBurns()
    {
        using var factory = new WebApplicationFactory<Program>();
        var (player, _) = await factory.RegisterAsync("Econ-Churn");

        Assert.Equal(0, (await player.Economy.HealthAsync()).HeroesMinted);

        var starters = await player.ClaimStartersAsync();   // two mints
        var afterMint = await player.Economy.HealthAsync();
        Assert.Equal(2, afterMint.HeroesMinted);
        Assert.Equal(0, afterMint.HeroesBurned);   // nothing burned yet

        // Exactly what a burn flow does: drop the hero and record the burn.
        var store = factory.Services.GetRequiredService<GameStore>();
        store.Heroes.TryRemove(starters[0].Id, out _);
        store.RecordBurn();

        var afterBurn = await player.Economy.HealthAsync();
        Assert.Equal(2, afterBurn.HeroesMinted);   // mints never decrement
        Assert.Equal(1, afterBurn.HeroesBurned);
        Assert.Equal(1, afterBurn.HeroSupply);
    }

    /// <summary>
    /// The regression that made counting necessary. Heroes are durable (they survive a restart), while the
    /// churn counters are per-uptime — so a fresh process starts with minted = 0 against a full surviving
    /// supply. While burns were DERIVED as minted − supply, that difference was negative and clamped to zero,
    /// hiding every real burn until mints overtook the entire population. The mint half kept counting, so the
    /// treasury card showed mints and no burns: the exact alarm shape, from a perfectly healthy game.
    /// </summary>
    [Fact]
    public async Task Health_CountsABurn_EvenWhenHeroesOutliveTheProcessThatMintedThem()
    {
        using var factory = new WebApplicationFactory<Program>();
        var (player, playerDto) = await factory.RegisterAsync("Econ-Restart");
        var store = factory.Services.GetRequiredService<GameStore>();

        // Stand in for rehydration: heroes present with nothing minted this process, as after a restart.
        foreach (var id in new[] { "rehydrated-1", "rehydrated-2", "rehydrated-3" })
            store.Heroes[id] = new Hero
            {
                Id = id, OwnerId = playerDto.PlayerId, Name = id, Level = 1,
                Genome = new Genome(new byte[32]), Generation = 1,
            };

        var afterRestart = await player.Economy.HealthAsync();
        Assert.Equal(0, afterRestart.HeroesMinted);   // this process minted nothing
        Assert.Equal(3, afterRestart.HeroSupply);     // but the heroes are here

        store.Heroes.TryRemove("rehydrated-1", out _);
        store.RecordBurn();

        // Derived, this read max(0, 0 − 2) = 0 and the burn vanished.
        var afterBurn = await player.Economy.HealthAsync();
        Assert.Equal(1, afterBurn.HeroesBurned);
        Assert.Equal(2, afterBurn.HeroSupply);
    }

    // Market liquidity: resting inventory (active) vs cleared (closed). Active outrunning closed is the
    // listings-outran-sales glut that called the CryptoKitties top. Pending offers (not yet buyable) count
    // toward neither.
    [Fact]
    public async Task Health_CountsRestingVsClearedOffers_IgnoringPending()
    {
        using var factory = new WebApplicationFactory<Program>();
        var (player, _) = await factory.RegisterAsync("Econ-Market");
        var store = factory.Services.GetRequiredService<GameStore>();

        static OfferListing Offer(string id, string status) => new()
        {
            Id = id, SellerId = "s", ItemId = "rusty-blade", AskSats = 1000,
            OfferAddress = $"addr-{id}", ItemAssetId = $"asset-{id}", OfferValueSats = 1000,
            RefundAfterUnixSeconds = 0, Status = status,
        };
        store.Offers["a1"] = Offer("a1", "active");
        store.Offers["a2"] = Offer("a2", "active");
        store.Offers["c1"] = Offer("c1", "closed");
        store.Offers["p1"] = Offer("p1", "pending");   // created but not yet buyable

        var h = await player.Economy.HealthAsync();
        Assert.Equal(2, h.ActiveOfferCount);   // two resting on the market
        Assert.Equal(1, h.ClosedOfferCount);   // one cleared
        // pending is neither resting nor cleared — it must not inflate either gauge.
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

    [Fact]
    public async Task Health_TalliesBreedFeeInflow_InvoiceMode()
    {
        using var factory = new WebApplicationFactory<Program>();
        var (alice, _) = await factory.RegisterAsync("Econ-Breed-Inv");
        var heroes = await alice.ClaimStartersAsync();

        // Invoice mode (no mode arg): pay the fee invoice, then reveal.
        var commit = await alice.Breeding.CommitAsync(new BreedCommitRequest(heroes[0].Id, heroes[1].Id));
        await alice.Dev.PayInvoiceAsync(new { InvoiceId = commit.Invoice!.InvoiceId });
        await alice.Breeding.RevealAsync(commit.BreedingId, new BreedRevealRequest("breed-inv-nonce"));

        var health = await alice.Economy.HealthAsync();
        Assert.Equal(commit.Invoice!.AmountSats, health.InflowByTag.GetValueOrDefault("breed"));
    }

    [Fact]
    public async Task Health_TalliesBreedFeeInflow_CovenantMode()
    {
        using var factory = new WebApplicationFactory<Program>();
        var (alice, _) = await factory.RegisterAsync("Econ-Breed-Cov");
        var heroes = await alice.ClaimStartersAsync();

        // Covenant mode: the fee rides in the breed escrow and is captured structurally at execution.
        var commit = await alice.Breeding.CommitAsync(new BreedCommitRequest(heroes[0].Id, heroes[1].Id, "covenant"));
        await alice.Dev.FundBreedEscrowAsync(new { BreedingId = commit.BreedingId });
        await alice.Breeding.RevealAsync(commit.BreedingId, new BreedRevealRequest("breed-cov-nonce"));

        var health = await alice.Economy.HealthAsync();
        Assert.Equal(commit.EscrowFeeSats, health.InflowByTag.GetValueOrDefault("breed"));
    }

    [Fact]
    public async Task Health_TalliesMergeFeeInflow()
    {
        using var factory = new WebApplicationFactory<Program>();
        var (alice, _) = await factory.RegisterAsync("Econ-Merge");
        var heroes = await alice.ClaimStartersAsync();

        // Merge is covenant-only: the fee rides in the escrow, retired to the treasury at reveal.
        var commit = await alice.Merge.CommitAsync(new MergeCommitRequest(heroes[0].Id, heroes[1].Id));
        await alice.Dev.FundMergeEscrowAsync(new { MergeId = commit.MergeId });
        await alice.Merge.RevealAsync(commit.MergeId, new MergeRevealRequest("merge-nonce"));

        var health = await alice.Economy.HealthAsync();
        Assert.True(health.InflowByTag.GetValueOrDefault("merge") > 0, "the merge fee is tallied as inflow at reveal");
    }

    static async Task<List<string>> SquadLineup(ArkadeHeroesClient c)
    {
        var ids = (await c.ClaimStartersAsync()).Select(h => h.Id).ToList();
        ids.Add((await c.Dev.MintHeroAsync()).Id);
        return ids.Take(3).ToList();
    }
}
