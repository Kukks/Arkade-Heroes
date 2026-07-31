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

    /// <summary>
    /// The other half of the /watch page's claim: the recompute has to REFUSE a doctored replay, or the
    /// green verdict beside it means nothing. Each case is a distinct lie a server could tell about a
    /// fight it already resolved, and each must be caught by the spectator's own machine.
    /// </summary>
    [Fact]
    public async Task TamperedReplay_FailsVerification()
    {
        var (alice, _) = await _factory.RegisterAsync("Tamper-A");
        var (bob, _) = await _factory.RegisterAsync("Tamper-B");
        var aliceHero = (await alice.ClaimStartersAsync())[0];
        var bobHero = (await bob.ClaimStartersAsync())[0];

        var open = await alice.Matches.OpenAsync(new OpenMatchRequest(aliceHero.Id, bobHero.Id));
        await alice.Matches.FightAsync(open.MatchId, new FightRequest("tamper-nonce"));

        using var spectatorHttp = _factory.CreateClient();
        var spectator = new ArkadeHeroesClient(spectatorHttp);
        var replay = await spectator.Matches.ReplayAsync(open.MatchId);

        FightResponse Wire(BattleResultDto result, string seedHex, string entropyHex) =>
            new(result, seedHex, entropyHex, 0, 0,
                replay.ChallengerSnapshot, replay.DefenderSnapshot, replay.ChallengerSnapshot, replay.DefenderSnapshot);

        (bool Ok, string Detail) Verify(FightResponse fight) =>
            FairnessAudit.VerifyMatch(open.MatchId, "tamper-nonce", replay.CommitmentHex, fight);

        // Honest, as served: the control the three lies below are measured against.
        Assert.True(Verify(Wire(replay.Result, replay.ServerSeedHex, replay.EntropyHex)).Ok);

        // (1) The winner is swapped — the loser is announced as having taken the pot.
        var flipped = replay.Result with { WinnerId = replay.Result.LoserId, LoserId = replay.Result.WinnerId };
        var swapped = Verify(Wire(flipped, replay.ServerSeedHex, replay.EntropyHex));
        Assert.False(swapped.Ok);
        Assert.Contains("winner", swapped.Detail, StringComparison.OrdinalIgnoreCase);

        // (2) A single number inside the log is doctored — one extra point of damage on the first blow.
        Assert.NotEmpty(replay.Result.Events);
        var events = replay.Result.Events.ToList();
        events[0] = events[0] with { Damage = events[0].Damage + 1 };
        var doctored = Verify(Wire(replay.Result with { Events = events }, replay.ServerSeedHex, replay.EntropyHex));
        Assert.False(doctored.Ok);
        Assert.Contains("diverges at event 0", doctored.Detail);

        // (3) A seed swapped for one that produces a different fight. It cannot hash to the commitment the
        //     server published before the match, which is the check that makes the seed unchooseable after.
        //     Flipping the leading nibble is deterministic (no dependence on which digits the seed happens
        //     to contain) and still valid hex, so the swap is a real seed rather than a parse failure.
        var otherSeed = (replay.ServerSeedHex[0] == '0' ? '1' : '0') + replay.ServerSeedHex[1..];
        Assert.NotEqual(replay.ServerSeedHex, otherSeed);
        var substituted = Verify(Wire(replay.Result, otherSeed, replay.EntropyHex));
        Assert.False(substituted.Ok);
        Assert.Contains("commitment", substituted.Detail);
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
