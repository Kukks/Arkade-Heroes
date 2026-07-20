using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ArkadeHeroes.Tests;

/// <summary>
/// A resolved match is a public, trustlessly-watchable artifact: GET /matches/{id}/replay serves the
/// fight-time snapshots + the revealed commit-reveal seed, so ANY spectator (no auth) can replay it in
/// the browser arena AND independently verify it was fair via FairnessAudit.VerifyMatch.
/// </summary>
public class MatchReplayTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public MatchReplayTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task ResolvedMatch_ServesAVerifiableReplay_ToAnUnauthenticatedSpectator()
    {
        var (alice, _) = await _factory.RegisterAsync("Replay-A");
        var (bob, _) = await _factory.RegisterAsync("Replay-B");
        var aliceHero = (await alice.ClaimStartersAsync())[0];
        var bobHero = (await bob.ClaimStartersAsync())[0];

        var open = await alice.Matches.OpenAsync(new OpenMatchRequest(aliceHero.Id, bobHero.Id));   // friendly
        var fight = await alice.Matches.FightAsync(open.MatchId, new FightRequest("replay-nonce"));

        // A spectator with NO auth token fetches the replay of the resolved match...
        using var spectatorHttp = _factory.CreateClient();
        var spectator = new ArkadeHeroesClient(spectatorHttp);
        var replay = await spectator.Matches.ReplayAsync(open.MatchId);

        Assert.Equal(aliceHero.Id, replay.ChallengerSnapshot.Id);
        Assert.Equal(bobHero.Id, replay.DefenderSnapshot.Id);
        Assert.Equal(fight.Result.WinnerId, replay.WinnerHeroId);

        // ...and independently re-derives the fight from the revealed seed — trustless spectating.
        var fr = new FightResponse(replay.Result, replay.ServerSeedHex, replay.EntropyHex, 0, 0,
            replay.ChallengerSnapshot, replay.DefenderSnapshot, replay.ChallengerSnapshot, replay.DefenderSnapshot);
        var (ok, detail) = FairnessAudit.VerifyMatch(open.MatchId, "replay-nonce", replay.CommitmentHex, fr);
        Assert.True(ok, detail);
    }

    [Fact]
    public async Task ResolvedDeathMatch_ServesAVerifiableReplay()
    {
        var (alice, _) = await _factory.RegisterAsync("DMReplay-A");
        var (bob, _) = await _factory.RegisterAsync("DMReplay-B");
        var aliceHero = (await alice.ClaimStartersAsync())[0];
        var bobHero = (await bob.ClaimStartersAsync())[0];

        var open = await alice.DeathMatch.OpenAsync(new DeathMatchOpenRequest(aliceHero.Id, bobHero.Id));
        await alice.Dev.FundDeathMatchEscrowAsync(new { DeathMatchId = open.DeathMatchId, Role = "challenger" });
        await alice.Dev.PayInvoiceAsync(new { InvoiceId = open.FeeInvoice!.InvoiceId });
        var accept = await bob.DeathMatch.AcceptAsync(open.DeathMatchId);
        await bob.Dev.FundDeathMatchEscrowAsync(new { DeathMatchId = open.DeathMatchId, Role = "defender" });
        await bob.Dev.PayInvoiceAsync(new { InvoiceId = accept.FeeInvoice!.InvoiceId });
        var settle = await alice.DeathMatch.SettleAsync(open.DeathMatchId, new DeathMatchSettleRequest("dm-replay-nonce"));

        // A death-match is a watchable, verifiable artifact too — an unauthenticated spectator replays the permakill.
        using var spectatorHttp = _factory.CreateClient();
        var spectator = new ArkadeHeroesClient(spectatorHttp);
        var replay = await spectator.DeathMatch.ReplayAsync(open.DeathMatchId);

        Assert.Equal(settle.WinnerHeroId, replay.WinnerHeroId);
        var fr = new FightResponse(replay.Result, replay.ServerSeedHex, replay.EntropyHex, 0, 0,
            replay.ChallengerSnapshot, replay.DefenderSnapshot, replay.ChallengerSnapshot, replay.DefenderSnapshot);
        var (ok, detail) = FairnessAudit.VerifyMatch(open.DeathMatchId, "dm-replay-nonce", replay.CommitmentHex, fr);
        Assert.True(ok, detail);
    }

    [Fact]
    public async Task UnknownMatch_HasNoReplay()
    {
        var (alice, _) = await _factory.RegisterAsync("Replay-None");
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => alice.Matches.ReplayAsync("does-not-exist"));
    }
}
