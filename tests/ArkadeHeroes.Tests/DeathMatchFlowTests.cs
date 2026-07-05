using System.Net;
using System.Net.Http.Json;
using ArkadeHeroes.Core.Combat;
using ArkadeHeroes.Core.Fairness;
using ArkadeHeroes.Server;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ArkadeHeroes.Tests;

/// <summary>
/// Escrow/InMemory death-match over the real HTTP surface: both players stake their
/// hero → settle runs the deterministic fight → the LOSER's hero is permanently
/// burned (gone from every wallet), the winner keeps their hero unchanged (no XP),
/// and the winner is client-verifiable (replay the fight). Gear transfer + the live
/// covenant are rung 2.
/// </summary>
public class DeathMatchFlowTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public DeathMatchFlowTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task DeathMatch_LoserHeroBurned_WinnerSurvivesUnchanged_WinnerVerifiable()
    {
        var (alice, _) = await _factory.RegisterAsync("DM-Alice");
        var (bob, _) = await _factory.RegisterAsync("DM-Bob");
        var aliceHeroes = await alice.ClaimStartersAsync();
        var bobHeroes = await bob.ClaimStartersAsync();
        var store = _factory.Services.GetRequiredService<GameStore>();

        var aliceHeroId = aliceHeroes[0].Id;
        var bobHeroId = bobHeroes[0].Id;
        // Give Alice a big level lead so the fight has a near-certain, favored outcome.
        store.Heroes[aliceHeroId].Level = 20;
        var levelBefore = new Dictionary<string, int>
        {
            [aliceHeroId] = 20,
            [bobHeroId] = store.Heroes[bobHeroId].Level,
        };

        var open = (await (await alice.PostAsJsonAsync("/api/deathmatch/open",
                new DeathMatchOpenRequest(aliceHeroId, bobHeroId)))
            .Content.ReadFromJsonAsync<DeathMatchOpenResponse>())!;
        Assert.Equal("favored", open.Favorability.Label);          // Alice is 19 levels up
        Assert.Equal(levelBefore[bobHeroId] - 20, open.Favorability.LevelGap); // signed: theirs − mine

        // Capture pre-settle snapshots — settle deletes the loser's record.
        var aliceBefore = (await alice.GetFromJsonAsync<HeroDto>($"/api/heroes/{aliceHeroId}"))!;
        var bobBefore = (await bob.GetFromJsonAsync<HeroDto>($"/api/heroes/{bobHeroId}"))!;

        // Both stake their hero.
        await alice.PostAsJsonAsync("/api/dev/fund-deathmatch-escrow", new { DeathMatchId = open.DeathMatchId, Role = "challenger" });
        await bob.PostAsync($"/api/deathmatch/{open.DeathMatchId}/accept", null);
        await bob.PostAsJsonAsync("/api/dev/fund-deathmatch-escrow", new { DeathMatchId = open.DeathMatchId, Role = "defender" });

        var settle = (await (await alice.PostAsJsonAsync($"/api/deathmatch/{open.DeathMatchId}/settle",
                new DeathMatchSettleRequest("dm-nonce")))
            .Content.ReadFromJsonAsync<DeathMatchSettleResponse>())!;

        // The loser's hero is BURNED — gone from BOTH players' wallets (outcome-agnostic).
        var aliceMine = (await alice.GetFromJsonAsync<List<HeroDto>>("/api/heroes/mine"))!;
        var bobMine = (await bob.GetFromJsonAsync<List<HeroDto>>("/api/heroes/mine"))!;
        Assert.DoesNotContain(aliceMine, h => h.Id == settle.LoserHeroId);
        Assert.DoesNotContain(bobMine, h => h.Id == settle.LoserHeroId);

        // The winner's hero survives, UNCHANGED — a death-match awards no XP.
        var winner = aliceMine.Concat(bobMine).Single(h => h.Id == settle.WinnerHeroId);
        Assert.Equal(levelBefore[settle.WinnerHeroId], winner.Level);

        // The winner is client-verifiable: replaying the deterministic fight from the
        // revealed seed reproduces the reported winner (an incorrect settle is detectable).
        var entropy = CommitReveal.DeriveEntropy(
            Convert.FromHexString(settle.ServerSeedHex), open.DeathMatchId, aliceHeroId, bobHeroId, "dm-nonce");
        var replay = BattleEngine.Fight(FairnessAudit.RebuildHero(aliceBefore), FairnessAudit.RebuildHero(bobBefore), entropy);
        Assert.Equal(settle.WinnerHeroId, replay.WinnerId);

        // The receipt verifies (signature + commit–reveal); no XP → ReplayLevel ignores it.
        Assert.NotNull(settle.Receipt);
        Assert.Equal("deathmatch", settle.Receipt!.Type);
        Assert.True(ReceiptVerifier.Verify(settle.Receipt).Ok);
    }

    [Fact]
    public async Task DeathMatch_SettleBeforeBothFunded_IsRefused()
    {
        var (alice, _) = await _factory.RegisterAsync("DM-Guard-A");
        var (bob, _) = await _factory.RegisterAsync("DM-Guard-B");
        var a = await alice.ClaimStartersAsync();
        var b = await bob.ClaimStartersAsync();

        var open = (await (await alice.PostAsJsonAsync("/api/deathmatch/open",
                new DeathMatchOpenRequest(a[0].Id, b[0].Id)))
            .Content.ReadFromJsonAsync<DeathMatchOpenResponse>())!;
        await alice.PostAsJsonAsync("/api/dev/fund-deathmatch-escrow", new { DeathMatchId = open.DeathMatchId, Role = "challenger" });
        // Bob never accepts/stakes → settle is refused (deposit-gated).
        var early = await alice.PostAsJsonAsync($"/api/deathmatch/{open.DeathMatchId}/settle", new DeathMatchSettleRequest("n"));
        Assert.Equal(HttpStatusCode.BadRequest, early.StatusCode);
    }

    [Fact]
    public async Task DeathMatch_RejectsUnownedChallenger()
    {
        var (alice, _) = await _factory.RegisterAsync("DM-Own-A");
        var (bob, _) = await _factory.RegisterAsync("DM-Own-B");
        var a = await alice.ClaimStartersAsync();
        var b = await bob.ClaimStartersAsync();
        // Alice tries to stake BOB's hero as the challenger.
        var resp = await alice.PostAsJsonAsync("/api/deathmatch/open", new DeathMatchOpenRequest(b[0].Id, a[0].Id));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
