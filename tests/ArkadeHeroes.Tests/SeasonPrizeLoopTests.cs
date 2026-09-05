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
        var (open, _) = await a.StakedMatchAsync(b, aHero, bHero, 1000);
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

        // Both heroes start with XP banked, which is the ordinary state of an established player and is
        // REQUIRED for this fixture to mean anything: a staked fight between two heroes holding nothing
        // moves nothing (the conserved transfer is clamped to what the loser owns), so neither side would
        // record a win and — since a prize now needs a win behind it — the season would pay nobody.
        var store = factory.Services.GetRequiredService<ArkadeHeroes.Server.GameStore>();
        store.Heroes[ah.Id].Xp = 500;
        store.Heroes[bh.Id].Xp = 500;

        await StakedFight(alice, bob, ah.Id, bh.Id, "prize-settle-1");   // → one real winner, one loser

        using var scope = factory.Services.CreateScope();
        var game = scope.ServiceProvider.GetRequiredService<GameService>();
        var chain = scope.ServiceProvider.GetRequiredService<IChainService>();

        var futureNow = Season.Current(DateTimeOffset.UtcNow, 14).End.AddDays(1);   // next season → current is due
        var treasuryBefore = await chain.TreasuryBalanceAsync();
        var board = await game.SeasonLeaderboardAt(futureNow, CancellationToken.None);

        var settled = board.LastSettlement;
        Assert.NotNull(settled);
        // ONE winner from one fight: the loser took part but won nothing, and a prize needs a win behind
        // it. The runner-up share stays in the treasury rather than being paid to someone who lost.
        Assert.Single(settled!.Winners);
        Assert.True(settled.PotSats > 25_000);                                // base + accrual
        Assert.Equal(settled.PotSats * 60 / 100, settled.Winners[0].AwardSats);
        var paid = settled.Winners.Sum(w => w.AwardSats);
        Assert.Equal(treasuryBefore - paid, await chain.TreasuryBalanceAsync());   // treasury dropped by exactly the payouts

        // Idempotent: settling again at the same `now` pays nothing more.
        await game.SeasonLeaderboardAt(futureNow, CancellationToken.None);
        Assert.Equal(treasuryBefore - paid, await chain.TreasuryBalanceAsync());
    }

    [Fact]
    public async Task SeasonWithNoRealWins_PaysNobody_AndKeepsThePot()
    {
        // The season a fresh playerbase actually produces. Two free starters hold no XP, so a staked fight
        // between them moves nothing — both end on ZERO wins while still logging a match. The board still
        // ranks them (it falls through to level, then match count), so before this rule there WAS a "top
        // three" with nothing behind it, and the pot paid out anyway — including the treasury-funded base.
        // Measured on the old behaviour: 15,124 + 7,562 sats to two heroes that had won nothing.
        using var factory = new WebApplicationFactory<Program>();
        var (alice, _) = await factory.RegisterAsync("Prize-NoWin-A");
        var (bob, _) = await factory.RegisterAsync("Prize-NoWin-B");
        var ah = (await alice.ClaimStartersAsync())[0];
        var bh = (await bob.ClaimStartersAsync())[0];
        await alice.Dev.FundTreasuryAsync(new { Sats = 500_000L });
        await StakedFight(alice, bob, ah.Id, bh.Id, "prize-nowin-1");

        var store = factory.Services.GetRequiredService<ArkadeHeroes.Server.GameStore>();
        // The premise: the fight happened and moved nothing, so nobody won anything.
        Assert.Equal((1, 0L), (store.Heroes[ah.Id].Level, store.Heroes[ah.Id].Xp));
        Assert.Equal((1, 0L), (store.Heroes[bh.Id].Level, store.Heroes[bh.Id].Xp));

        using var scope = factory.Services.CreateScope();
        var game = scope.ServiceProvider.GetRequiredService<GameService>();
        var chain = scope.ServiceProvider.GetRequiredService<IChainService>();

        var futureNow = Season.Current(DateTimeOffset.UtcNow, 14).End.AddDays(1);
        var treasuryBefore = await chain.TreasuryBalanceAsync();
        var board = await game.SeasonLeaderboardAt(futureNow, CancellationToken.None);

        Assert.Null(board.LastSettlement);                                   // nothing settled…
        Assert.Equal(treasuryBefore, await chain.TreasuryBalanceAsync());     // …and not one sat left
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
