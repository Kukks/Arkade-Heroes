using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ArkadeHeroes.Tests;

/// <summary>The daily loop end to end: a fresh player can claim the base once per day (a second
/// claim is rejected), and completing a real server-verified quest adds its bonus to the claim.
/// Same-day only — multi-day streak progression is proven at the pure-function level (DailyTests),
/// since the server has no injectable clock.</summary>
public class DailyLoopTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public DailyLoopTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task FreshPlayer_ClaimsBaseOncePerDay_SecondClaimRejected()
    {
        var (alice, _) = await _factory.RegisterAsync("Daily-A");
        await alice.ClaimStartersAsync();
        await alice.Dev.FundTreasuryAsync(new { Sats = 10_000L });   // the faucet pays from treasury reserves

        var status = await alice.Daily.StatusAsync();
        Assert.False(status.ClaimedToday);
        Assert.Equal(0, status.Streak);
        Assert.All(status.Quests, q => Assert.False(q.Done));
        Assert.Equal(status.BaseSats, status.ClaimableNowSats);   // no quests done yet → base only

        var claim = await alice.Daily.ClaimAsync();
        Assert.Equal(status.BaseSats, claim.AwardedSats);
        Assert.Equal(1, claim.Streak);

        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => alice.Daily.ClaimAsync());
        Assert.True((await alice.Daily.StatusAsync()).ClaimedToday);
    }

    [Fact]
    public async Task CompletingADuelWin_AddsTheQuestBonus()
    {
        // A staked (wager>0) invoice-mode duel produces an in-window "match" receipt; the winner
        // gets the "Win a duel" bonus IF that quest is in today's rotation (date-derived; guarded).
        var (alice, _) = await _factory.RegisterAsync("Daily-Duel-A");
        var (bob, _) = await _factory.RegisterAsync("Daily-Duel-B");
        var aliceHero = (await alice.ClaimStartersAsync())[0];
        var bobHero = (await bob.ClaimStartersAsync())[0];

        var open = await alice.Matches.OpenAsync(new OpenMatchRequest(aliceHero.Id, bobHero.Id, 1000, "invoice"));
        await alice.Dev.PayInvoiceAsync(new { InvoiceId = open.StakeInvoice!.InvoiceId });
        await alice.Dev.PayInvoiceAsync(new { InvoiceId = open.MatchFeeInvoice!.InvoiceId });
        var accept = await bob.Matches.AcceptAsync(open.MatchId);
        await bob.Dev.PayInvoiceAsync(new { InvoiceId = accept.StakeInvoice!.InvoiceId });
        await bob.Dev.PayInvoiceAsync(new { InvoiceId = accept.MatchFeeInvoice!.InvoiceId });
        var fight = await alice.Matches.FightAsync(open.MatchId, new FightRequest("daily-duel-nonce"));

        var winnerClient = fight.Result.WinnerId == aliceHero.Id ? alice : bob;
        var status = await winnerClient.Daily.StatusAsync();
        var duelQuest = status.Quests.FirstOrDefault(q => q.Id == "duel-win");

        if (duelQuest is not null)
        {
            Assert.True(duelQuest.Done);
            var claim = await winnerClient.Daily.ClaimAsync();
            Assert.Contains("duel-win", claim.CompletedQuestIds);
            Assert.True(claim.AwardedSats >= status.BaseSats + duelQuest.BonusSats);
        }
    }
}
