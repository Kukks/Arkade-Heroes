using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Core.Fairness;
using ArkadeHeroes.Core.Progression;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ArkadeHeroes.Tests;

/// <summary>
/// The endless Trials server flow: open (commit, FREE — no fee) → run (endless ghost ladder) → score +
/// title + personal-best. Deterministic + client-replayable; awards no XP/item/sats (treasury-neutral).
/// </summary>
public class TrialsFlowTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public TrialsFlowTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task Trials_OpenRun_ScoresTitleReceipt_AndReplays_NoFee()
    {
        var (alice, _) = await _factory.RegisterAsync("Trials-Alice");
        var hero = (await alice.ClaimStartersAsync())[0];

        var open = await alice.Trials.OpenAsync(hero.Id);
        Assert.False(string.IsNullOrEmpty(open.CommitmentHex));

        // FREE: there is no fee invoice to pay — the run resolves immediately.
        var run = await alice.Trials.RunAsync(open.TrialsId, "trial-nonce");

        Assert.Equal(run.WavesCleared, run.Waves.Count(w => w.Won));    // score = number of wins
        Assert.Equal(Trials.TitleFor(run.WavesCleared), run.Title);     // title tracks the depth
        Assert.Equal(run.WavesCleared, run.BestScore);                  // first run sets the personal best
        Assert.Equal("trials", run.Receipt.Type);
        Assert.True(ReceiptVerifier.Verify(run.Receipt).Ok);           // signed receipt verifies
        Assert.Equal(run.WavesCleared, (int)run.Receipt.XpAwardB);     // the score is attested in the receipt

        // Client-replayable: re-resolving from the revealed seed + PRE-run snapshot reproduces the score.
        var entropy = CommitReveal.DeriveEntropy(
            Convert.FromHexString(run.ServerSeedHex), open.TrialsId, hero.Id, "trial-nonce");
        var replay = Trials.Resolve(FairnessAudit.RebuildHero(run.HeroSnapshot), entropy);
        Assert.Equal(run.WavesCleared, replay.WavesCleared);
    }

    [Fact]
    public async Task Trials_BestScore_OnlyClimbs()
    {
        var (alice, _) = await _factory.RegisterAsync("Trials-Best");
        var hero = (await alice.ClaimStartersAsync())[0];

        var o1 = await alice.Trials.OpenAsync(hero.Id);
        var r1 = await alice.Trials.RunAsync(o1.TrialsId, "nonce-a");
        var o2 = await alice.Trials.OpenAsync(hero.Id);
        var r2 = await alice.Trials.RunAsync(o2.TrialsId, "nonce-b");

        // The personal best is the max across runs and never regresses below an earlier run's score.
        Assert.True(r2.BestScore >= r1.WavesCleared);
        Assert.Equal(Math.Max(r1.WavesCleared, r2.WavesCleared), r2.BestScore);
    }

    [Fact]
    public async Task Trials_DoubleRun_IsRefused()
    {
        var (alice, _) = await _factory.RegisterAsync("Trials-Double");
        var hero = (await alice.ClaimStartersAsync())[0];

        var open = await alice.Trials.OpenAsync(hero.Id);
        await alice.Trials.RunAsync(open.TrialsId, "once");
        // The committed seed is single-use: a second run of the same session is refused.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => alice.Trials.RunAsync(open.TrialsId, "again"));
    }

    [Fact]
    public async Task Trials_ClientVerifiesTheRun_AndCatchesAnInflatedScore()
    {
        var (alice, _) = await _factory.RegisterAsync("Trials-Verify");
        var hero = (await alice.ClaimStartersAsync())[0];

        var open = await alice.Trials.OpenAsync(hero.Id);
        var run = await alice.Trials.RunAsync(open.TrialsId, "verify-nonce");

        // The honest run verifies: the ladder replays to the same score + title off the revealed seed.
        var (ok, detail) = FairnessAudit.VerifyTrials(open.TrialsId, "verify-nonce", run.Receipt.CommitmentHex, run);
        Assert.True(ok, detail);

        // A server that inflates the score is caught by the replay — the ghost ladder is pure in the entropy.
        var inflated = run with { WavesCleared = run.WavesCleared + 5 };
        var (tamperOk, _) = FairnessAudit.VerifyTrials(open.TrialsId, "verify-nonce", run.Receipt.CommitmentHex, inflated);
        Assert.False(tamperOk, "an inflated waves-survived count must fail the client replay");
    }

    [Fact]
    public async Task Trials_RejectsUnownedHero()
    {
        var (alice, _) = await _factory.RegisterAsync("Trials-Own-A");
        var (bob, _) = await _factory.RegisterAsync("Trials-Own-B");
        var bobHero = (await bob.ClaimStartersAsync())[0];
        // Alice cannot open a trials run on Bob's hero.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => alice.Trials.OpenAsync(bobHero.Id));
    }
}
