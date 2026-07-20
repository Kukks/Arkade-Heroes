using ArkadeHeroes.Core.Progression;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ArkadeHeroes.Tests;

/// <summary>
/// The ranked SEASON ladder: staked-match wins within the current season window, computed trustlessly from
/// receipts (reusing LeaderboardBuilder). Friendly wins don't count — the stake + fee economy already
/// makes farming cost sats, so the ladder needs no separate anti-cheat.
/// </summary>
public class SeasonLeaderboardTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public SeasonLeaderboardTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task StakedWin_ShowsOnTheCurrentSeasonBoard()
    {
        var (alice, _) = await _factory.RegisterAsync("Season-A");
        var (bob, _) = await _factory.RegisterAsync("Season-B");
        var aliceHero = (await alice.ClaimStartersAsync())[0];
        var bobHero = (await bob.ClaimStartersAsync())[0];

        // A staked (wager > 0) invoice-mode match: both stake + pay the per-character fee (InMemory dev pay).
        var open = await alice.Matches.OpenAsync(new OpenMatchRequest(aliceHero.Id, bobHero.Id, 1000, "invoice"));
        await alice.Dev.PayInvoiceAsync(new { InvoiceId = open.StakeInvoice!.InvoiceId });
        await alice.Dev.PayInvoiceAsync(new { InvoiceId = open.MatchFeeInvoice!.InvoiceId });
        var accept = await bob.Matches.AcceptAsync(open.MatchId);
        await bob.Dev.PayInvoiceAsync(new { InvoiceId = accept.StakeInvoice!.InvoiceId });
        await bob.Dev.PayInvoiceAsync(new { InvoiceId = accept.MatchFeeInvoice!.InvoiceId });
        var fight = await alice.Matches.FightAsync(open.MatchId, new FightRequest("season-nonce"));

        // The public season board shows the current season number and the winner's staked win.
        var season = await alice.Leaderboard.SeasonAsync();
        Assert.Equal(Season.Current(DateTimeOffset.UtcNow, 14).Number, season.SeasonNumber);
        Assert.True(season.EndsAtUnix > DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        Assert.Contains(season.Standings, e => e.HeroId == fight.Result.WinnerId && e.Wins >= 1);
    }

    [Fact]
    public async Task FriendlyWin_DoesNotCountForTheSeason()
    {
        var (alice, _) = await _factory.RegisterAsync("Season-FriendlyA");
        var (bob, _) = await _factory.RegisterAsync("Season-FriendlyB");
        var aliceHero = (await alice.ClaimStartersAsync())[0];
        var bobHero = (await bob.ClaimStartersAsync())[0];

        // A friendly (wager 0) match needs no stake/accept and issues a "friendly" receipt → no ranked weight.
        var open = await alice.Matches.OpenAsync(new OpenMatchRequest(aliceHero.Id, bobHero.Id));
        var fight = await alice.Matches.FightAsync(open.MatchId, new FightRequest("friendly-nonce"));

        var season = await alice.Leaderboard.SeasonAsync();
        Assert.DoesNotContain(season.Standings, e => e.HeroId == fight.Result.WinnerId);
    }
}
