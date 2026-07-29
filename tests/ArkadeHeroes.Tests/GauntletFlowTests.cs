using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Core.Progression;
using ArkadeHeroes.Server;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ArkadeHeroes.Tests;

/// <summary>
/// F1 PvE gauntlet over the real HTTP surface: open (commit + fee) → pay → run the 5 ghost waves →
/// capped XP + a full-clear item, client-verifiable. The adversarial cases pin the anti-farming
/// contract: past the level cap a run mints ZERO, the fee gates the run, and a cooldown blocks farming.
/// </summary>
public class GauntletFlowTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public GauntletFlowTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task Gauntlet_Runs_Verifies_AndFoldsXpIntoLevel()
    {
        var (client, _) = await _factory.RegisterAsync("G-Runner");
        var hero = (await client.ClaimStartersAsync())[0];   // a level-1 starter — below the XP cap

        var open = await client.Gauntlet.OpenAsync(hero.Id);
        Assert.True(open.FeeInvoice.AmountSats > 500);   // entry always beats any drop
        await client.Dev.PayInvoiceAsync(new { InvoiceId = open.FeeInvoice.InvoiceId });

        var run = await client.Gauntlet.RunAsync(open.GauntletId, "g-nonce");

        // Gauntlet receipt (NOT "match" → no leaderboard weight) signs + commit-reveal verifies.
        Assert.Equal("gauntlet", run.Receipt.Type);
        Assert.True(ReceiptVerifier.Verify(run.Receipt).Ok);
        Assert.Equal(run.XpAwarded, run.Receipt.XpAwardA);

        // Client-side fairness recompute: re-derive the ghosts + fights, re-check the capped XP + item —
        // under the rules the run's own stamp names, not this client's compiled-in default.
        var (cfg, cfgError) = await client.Config.ResolveAsync(run.ConfigVersion);
        Assert.Null(cfgError);
        var (ok, detail) = FairnessAudit.VerifyGauntlet(open.GauntletId, "g-nonce", run.Receipt.CommitmentHex, run, cfg);
        Assert.True(ok, detail);

        // Each wave carries the ghost snapshot + fight log the browser replays in the arena: the
        // reconstructed ghost matches its wave level, and the fight has events + a winner in {hero, ghost}.
        Assert.NotEmpty(run.Waves);
        Assert.All(run.Waves, w =>
        {
            Assert.Equal(w.GhostLevel, w.Ghost.Level);
            Assert.NotEmpty(w.Result.Events);
            Assert.True(w.Result.WinnerId == hero.Id || w.Result.WinnerId == w.Ghost.Id);
        });

        // The gauntlet receipt folds its XP into the receipt-replayed level (from a level-1 genesis) —
        // recomputed purely from the player-held receipt, not from the server's stored level.
        Assert.Equal(
            Leveling.Apply(1, 0, run.Receipt.XpAwardA).Level,
            ReceiptVerifier.ReplayLevel(hero.Id, [run.Receipt]));
    }

    [Fact]
    public async Task Gauntlet_PastLevelCap_AwardsZeroXp()
    {
        var (client, _) = await _factory.RegisterAsync("G-Capped");
        var hero = (await client.ClaimStartersAsync())[0];
        var store = _factory.Services.GetRequiredService<GameStore>();
        store.Heroes[hero.Id].Level = 20;   // well past the level-10 cap → 0 XP however many waves it clears

        var open = await client.Gauntlet.OpenAsync(hero.Id);
        await client.Dev.PayInvoiceAsync(new { InvoiceId = open.FeeInvoice.InvoiceId });
        var run = await client.Gauntlet.RunAsync(open.GauntletId, "capped-nonce");

        Assert.Equal(0, run.XpAwarded);
        // And a client can prove it: the recompute, under the run's stamped rules, agrees the award is 0.
        var (cfg, cfgError) = await client.Config.ResolveAsync(run.ConfigVersion);
        Assert.Null(cfgError);
        Assert.True(FairnessAudit.VerifyGauntlet(open.GauntletId, "capped-nonce", run.Receipt.CommitmentHex, run, cfg).Ok);
    }

    [Fact]
    public async Task Gauntlet_RunBeforeFeePaid_IsRefused()
    {
        var (client, _) = await _factory.RegisterAsync("G-Unpaid");
        var hero = (await client.ClaimStartersAsync())[0];
        var open = await client.Gauntlet.OpenAsync(hero.Id);
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => client.Gauntlet.RunAsync(open.GauntletId, "n"));
    }

    [Fact]
    public async Task Gauntlet_CooldownBlocksBackToBackRuns()
    {
        var (client, _) = await _factory.RegisterAsync("G-Cooldown");
        var hero = (await client.ClaimStartersAsync())[0];
        var open = await client.Gauntlet.OpenAsync(hero.Id);
        await client.Dev.PayInvoiceAsync(new { InvoiceId = open.FeeInvoice.InvoiceId });
        await client.Gauntlet.RunAsync(open.GauntletId, "n");

        // Opening a second gauntlet immediately is refused — the hero is on cooldown (rate-limits the faucet).
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => client.Gauntlet.OpenAsync(hero.Id));
    }
}
