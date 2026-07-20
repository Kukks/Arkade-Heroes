using ArkadeHeroes.Chain;
using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Core.Progression;
using ArkadeHeroes.Server;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ArkadeHeroes.Tests;

/// <summary>The season prize pool end to end. Each test uses a FRESH factory — the pot + settled-marker
/// are global singleton state, so a shared fixture would let tests interfere. The settle path is driven
/// via the `now`-seam (`SeasonLeaderboardAt(futureNow)`): fights create receipts in the real current
/// season, then a `now` one season ahead makes that season "due" while its window still holds them.</summary>
public class SeasonPrizeLoopTests
{
    static async Task<FightResponse> StakedFight(
        ArkadeHeroesClient a, ArkadeHeroesClient b, string aHero, string bHero, string nonce)
    {
        var open = await a.Matches.OpenAsync(new OpenMatchRequest(aHero, bHero, 1000, "invoice"));
        await a.Dev.PayInvoiceAsync(new { InvoiceId = open.StakeInvoice!.InvoiceId });
        await a.Dev.PayInvoiceAsync(new { InvoiceId = open.MatchFeeInvoice!.InvoiceId });
        var acc = await b.Matches.AcceptAsync(open.MatchId);
        await b.Dev.PayInvoiceAsync(new { InvoiceId = acc.StakeInvoice!.InvoiceId });
        await b.Dev.PayInvoiceAsync(new { InvoiceId = acc.MatchFeeInvoice!.InvoiceId });
        return await a.Matches.FightAsync(open.MatchId, new FightRequest(nonce));
    }

    [Fact]
    public async Task StakedFight_GrowsTheSeasonPot()
    {
        using var factory = new WebApplicationFactory<Program>();
        var (alice, _) = await factory.RegisterAsync("Pot-A");
        var (bob, _) = await factory.RegisterAsync("Pot-B");
        var ah = (await alice.ClaimStartersAsync())[0];
        var bh = (await bob.ClaimStartersAsync())[0];

        var before = (await alice.Leaderboard.SeasonAsync()).PotSats;
        Assert.Equal(25_000, before);   // base pot, no fights yet
        await StakedFight(alice, bob, ah.Id, bh.Id, "pot-nonce-1");
        var after = (await alice.Leaderboard.SeasonAsync()).PotSats;

        Assert.True(after > before, $"pot should grow from staked-match fees: {before} -> {after}");
    }

    [Fact]
    public async Task RolledOverSeason_PaysPodium_AndIsIdempotent()
    {
        using var factory = new WebApplicationFactory<Program>();
        var (alice, _) = await factory.RegisterAsync("Prize-Set-A");
        var (bob, _) = await factory.RegisterAsync("Prize-Set-B");
        var ah = (await alice.ClaimStartersAsync())[0];
        var bh = (await bob.ClaimStartersAsync())[0];
        await alice.Dev.FundTreasuryAsync(new { Sats = 500_000L });   // cover the pot
        await StakedFight(alice, bob, ah.Id, bh.Id, "prize-settle-1");   // → 2 ranked competitors

        using var scope = factory.Services.CreateScope();
        var game = scope.ServiceProvider.GetRequiredService<GameService>();
        var chain = scope.ServiceProvider.GetRequiredService<IChainService>();

        var futureNow = Season.Current(DateTimeOffset.UtcNow, 14).End.AddDays(1);   // next season → current is due
        var treasuryBefore = await chain.TreasuryBalanceAsync();
        var board = await game.SeasonLeaderboardAt(futureNow, CancellationToken.None);

        var settled = board.LastSettlement;
        Assert.NotNull(settled);
        Assert.Equal(2, settled!.Winners.Count);                             // 2 competitors from 1 fight
        Assert.True(settled.PotSats > 25_000);                                // base + accrual
        Assert.Equal(settled.PotSats * 60 / 100, settled.Winners[0].AwardSats);
        Assert.Equal(settled.PotSats * 30 / 100, settled.Winners[1].AwardSats);
        var paid = settled.Winners.Sum(w => w.AwardSats);
        Assert.Equal(treasuryBefore - paid, await chain.TreasuryBalanceAsync());   // treasury dropped by exactly the payouts

        // Idempotent: settling again at the same `now` pays nothing more.
        await game.SeasonLeaderboardAt(futureNow, CancellationToken.None);
        Assert.Equal(treasuryBefore - paid, await chain.TreasuryBalanceAsync());
    }

    [Fact]
    public async Task UnderfundedTreasury_DoesNotSettle()
    {
        using var factory = new WebApplicationFactory<Program>();
        var (alice, _) = await factory.RegisterAsync("Prize-Under-A");
        var (bob, _) = await factory.RegisterAsync("Prize-Under-B");
        var ah = (await alice.ClaimStartersAsync())[0];
        var bh = (await bob.ClaimStartersAsync())[0];
        await StakedFight(alice, bob, ah.Id, bh.Id, "prize-under-1");   // treasury = fees only (< 25k base pot)

        using var scope = factory.Services.CreateScope();
        var game = scope.ServiceProvider.GetRequiredService<GameService>();

        var futureNow = Season.Current(DateTimeOffset.UtcNow, 14).End.AddDays(1);
        var board = await game.SeasonLeaderboardAt(futureNow, CancellationToken.None);

        Assert.Null(board.LastSettlement);   // underfunded → the season with competitors was not settled
    }
}
