using ArkadeHeroes.Chain;
using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Server;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ArkadeHeroes.Tests;

/// <summary>
/// The server tournament flow: players pay a buy-in into the treasury, fill a bracket, and once full it runs
/// the pure resolver and pays the podium (champion + runner-up, 70/30) out of the pot minus the house rake.
/// Treasury-mediated — the net treasury gain from a resolved tournament is exactly the rake.
/// </summary>
public class TournamentServerTests
{
    const long BuyIn = 1_000;

    static async Task<List<(ArkadeHeroesClient Client, string HeroId)>> FourPlayersAsync(WebApplicationFactory<Program> factory)
    {
        var players = new List<(ArkadeHeroesClient, string)>();
        for (var i = 0; i < 4; i++)
        {
            var (c, _) = await factory.RegisterAsync($"T-P{i}");
            var heroes = await c.ClaimStartersAsync();
            players.Add((c, heroes[0].Id));
        }
        return players;
    }

    [Fact]
    public async Task Tournament_FullFlow_PaysPodium_AndTreasuryNetsTheRake()
    {
        using var factory = new WebApplicationFactory<Program>();
        var chain = (InMemoryChainService)factory.Services.GetRequiredService<IChainService>();
        var players = await FourPlayersAsync(factory);
        var treasuryStart = await chain.TreasuryBalanceAsync();

        var open = await players[0].Client.Tournament.OpenAsync(new OpenTournamentRequest(players[0].HeroId, BuyIn, 4));
        var tid = open.Tournament.Id;
        await players[0].Client.Dev.PayInvoiceAsync(new { open.BuyIn.InvoiceId });
        for (var i = 1; i < 4; i++)
        {
            var join = await players[i].Client.Tournament.JoinAsync(tid, new JoinTournamentRequest(players[i].HeroId));
            await players[i].Client.Dev.PayInvoiceAsync(new { join.BuyIn.InvoiceId });
        }

        var resolved = await players[0].Client.Tournament.ResolveAsync(tid, new FightRequest("nonce-1"));
        Assert.Equal("resolved", resolved.Tournament.Status);
        Assert.NotNull(resolved.Tournament.ChampionHeroId);
        Assert.Equal(3, resolved.Bracket.Count);                             // 2 semis + 1 final
        Assert.Equal(new long[] { 2520, 1080 }, resolved.Prizes.ToArray());  // 3600 pool → 70/30
        Assert.Equal(2520, resolved.Tournament.ChampionPrizeSats);           // champion's share, surfaced for the Hall of Champions

        // Buy-ins (4000) in, prizes (3600) out → the treasury nets exactly the 10% rake (400).
        Assert.Equal(treasuryStart + BuyIn * 4 * 10 / 100, await chain.TreasuryBalanceAsync());
    }

    [Fact]
    public async Task Tournament_CannotResolveBeforeFull()
    {
        using var factory = new WebApplicationFactory<Program>();
        var players = await FourPlayersAsync(factory);
        var open = await players[0].Client.Tournament.OpenAsync(new OpenTournamentRequest(players[0].HeroId, BuyIn, 4));
        await players[1].Client.Tournament.JoinAsync(open.Tournament.Id, new JoinTournamentRequest(players[1].HeroId));

        // Only 2 of 4 seats filled — not resolvable.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => players[0].Client.Tournament.ResolveAsync(open.Tournament.Id, new FightRequest("n")));
    }

    [Fact]
    public async Task Tournament_CannotResolveWithAnUnpaidBuyIn()
    {
        using var factory = new WebApplicationFactory<Program>();
        var players = await FourPlayersAsync(factory);
        var open = await players[0].Client.Tournament.OpenAsync(new OpenTournamentRequest(players[0].HeroId, BuyIn, 4));
        var tid = open.Tournament.Id;
        await players[0].Client.Dev.PayInvoiceAsync(new { open.BuyIn.InvoiceId });
        for (var i = 1; i < 4; i++)
        {
            var join = await players[i].Client.Tournament.JoinAsync(tid, new JoinTournamentRequest(players[i].HeroId));
            if (i < 3) await players[i].Client.Dev.PayInvoiceAsync(new { join.BuyIn.InvoiceId });   // leave the last unpaid
        }

        // Full bracket, but one buy-in is unpaid → refuse (an unpaid entry would leak the treasury).
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => players[0].Client.Tournament.ResolveAsync(tid, new FightRequest("n")));
    }

    [Fact]
    public async Task Config_PublishesRakePct_ForDisplay()
    {
        // The tournaments page previews the house rake from GET /api/chain/info.
        using var factory = new WebApplicationFactory<Program>();
        var client = new ArkadeHeroesClient(factory.CreateClient());
        var info = await client.Chain.InfoAsync();
        Assert.Equal(10, info.Config?.TournamentRakePct);   // GameOptions.TournamentRakePct default
    }
}
