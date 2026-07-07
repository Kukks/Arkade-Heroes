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

    [Fact]
    public async Task DeathMatch_StakedGearMovesToTheWinner()
    {
        var (alice, alicePlayer) = await _factory.RegisterAsync("DM-GearA");
        var (bob, bobPlayer) = await _factory.RegisterAsync("DM-GearB");
        var a = await alice.ClaimStartersAsync();
        var b = await bob.ClaimStartersAsync();
        var store = _factory.Services.GetRequiredService<GameStore>();
        var chain = _factory.Services.GetRequiredService<ArkadeHeroes.Chain.IChainService>();

        // Bob's hero carries gear (bought + equipped); Alice out-levels him.
        store.Heroes[a[0].Id].Level = 20;
        await bob.BuyItemAsync("rusty-blade");
        var equip = await bob.PostAsJsonAsync($"/api/heroes/{b[0].Id}/equip", new EquipRequest("rusty-blade"));
        Assert.True(equip.IsSuccessStatusCode, await equip.Content.ReadAsStringAsync());

        // Open bakes Bob's loadout as his required stake; Alice has none.
        var open = (await (await alice.PostAsJsonAsync("/api/deathmatch/open",
                new DeathMatchOpenRequest(a[0].Id, b[0].Id)))
            .Content.ReadFromJsonAsync<DeathMatchOpenResponse>())!;
        var stake = Assert.Single(open.DefenderGear);
        Assert.Equal("rusty-blade", stake.ItemId);
        Assert.Equal(1, stake.Amount);
        Assert.Empty(open.ChallengerGear);

        await alice.PostAsJsonAsync("/api/dev/fund-deathmatch-escrow", new { DeathMatchId = open.DeathMatchId, Role = "challenger" });
        await bob.PostAsync($"/api/deathmatch/{open.DeathMatchId}/accept", null);
        await bob.PostAsJsonAsync("/api/dev/fund-deathmatch-escrow", new { DeathMatchId = open.DeathMatchId, Role = "defender" });

        // Staking moved Bob's unit INTO the escrow — he no longer holds it (can't sell it).
        Assert.Equal(0UL, await chain.GetItemAssetBalanceAsync(bobPlayer.PlayerId, "rusty-blade"));

        var settle = (await (await alice.PostAsJsonAsync($"/api/deathmatch/{open.DeathMatchId}/settle",
                new DeathMatchSettleRequest("gear-nonce")))
            .Content.ReadFromJsonAsync<DeathMatchSettleResponse>())!;

        // ALL staked gear goes to the WINNER (outcome-agnostic: Bob winning gets his own back).
        var winnerId = settle.WinnerHeroId == a[0].Id ? alicePlayer.PlayerId : bobPlayer.PlayerId;
        var loserId = settle.WinnerHeroId == a[0].Id ? bobPlayer.PlayerId : alicePlayer.PlayerId;
        Assert.Equal(1UL, await chain.GetItemAssetBalanceAsync(winnerId, "rusty-blade"));
        Assert.Equal(0UL, await chain.GetItemAssetBalanceAsync(loserId, "rusty-blade"));
    }

    [Fact]
    public async Task DeathMatch_CannotStakeTheSameGearUnitTwice()
    {
        var (alice, _) = await _factory.RegisterAsync("DM-Dbl-A");
        var (bob, _) = await _factory.RegisterAsync("DM-Dbl-B");
        var a = await alice.ClaimStartersAsync();
        var b = await bob.ClaimStartersAsync();

        // Bob owns ONE unit, equipped on his hero — baked into BOTH matches' stakes.
        await bob.BuyItemAsync("rusty-blade");
        var equip = await bob.PostAsJsonAsync($"/api/heroes/{b[0].Id}/equip", new EquipRequest("rusty-blade"));
        Assert.True(equip.IsSuccessStatusCode, await equip.Content.ReadAsStringAsync());

        var open1 = (await (await alice.PostAsJsonAsync("/api/deathmatch/open",
                new DeathMatchOpenRequest(a[0].Id, b[0].Id)))
            .Content.ReadFromJsonAsync<DeathMatchOpenResponse>())!;
        var open2 = (await (await alice.PostAsJsonAsync("/api/deathmatch/open",
                new DeathMatchOpenRequest(a[1].Id, b[0].Id)))
            .Content.ReadFromJsonAsync<DeathMatchOpenResponse>())!;

        // Funding match 1 moves the unit into ITS escrow; the same unit cannot fund match 2.
        await bob.PostAsync($"/api/deathmatch/{open1.DeathMatchId}/accept", null);
        var fund1 = await bob.PostAsJsonAsync("/api/dev/fund-deathmatch-escrow", new { DeathMatchId = open1.DeathMatchId, Role = "defender" });
        Assert.True(fund1.IsSuccessStatusCode, await fund1.Content.ReadAsStringAsync());

        await bob.PostAsync($"/api/deathmatch/{open2.DeathMatchId}/accept", null);
        var fund2 = await bob.PostAsJsonAsync("/api/dev/fund-deathmatch-escrow", new { DeathMatchId = open2.DeathMatchId, Role = "defender" });
        Assert.Equal(HttpStatusCode.BadRequest, fund2.StatusCode);
    }

    [Fact]
    public async Task DeathMatch_HalfFundedChallengerAborts_GearReturned_SettleThenRefused()
    {
        var (alice, alicePlayer) = await _factory.RegisterAsync("DM-Abort-A");
        var (bob, _) = await _factory.RegisterAsync("DM-Abort-B");
        var a = await alice.ClaimStartersAsync();
        var b = await bob.ClaimStartersAsync();
        var chain = _factory.Services.GetRequiredService<ArkadeHeroes.Chain.IChainService>();

        // Alice (challenger) equips gear — baked into her stake at open.
        await alice.BuyItemAsync("rusty-blade");
        var equip = await alice.PostAsJsonAsync($"/api/heroes/{a[0].Id}/equip", new EquipRequest("rusty-blade"));
        Assert.True(equip.IsSuccessStatusCode, await equip.Content.ReadAsStringAsync());

        var open = (await (await alice.PostAsJsonAsync("/api/deathmatch/open",
                new DeathMatchOpenRequest(a[0].Id, b[0].Id)))
            .Content.ReadFromJsonAsync<DeathMatchOpenResponse>())!;
        var stake = Assert.Single(open.ChallengerGear);
        Assert.Equal("rusty-blade", stake.ItemId);

        // Alice stakes her hero + gear; Bob never accepts → half-funded.
        await alice.PostAsJsonAsync("/api/dev/fund-deathmatch-escrow", new { DeathMatchId = open.DeathMatchId, Role = "challenger" });
        Assert.Equal(0UL, await chain.GetItemAssetBalanceAsync(alicePlayer.PlayerId, "rusty-blade")); // staked into escrow

        // Abort → Alice reclaims her staked gear.
        var abort = await alice.PostAsJsonAsync("/api/dev/abort-deathmatch", new { DeathMatchId = open.DeathMatchId });
        Assert.True(abort.IsSuccessStatusCode, await abort.Content.ReadAsStringAsync());
        Assert.Equal(1UL, await chain.GetItemAssetBalanceAsync(alicePlayer.PlayerId, "rusty-blade")); // returned

        // Settle is now refused — the match was aborted.
        var settle = await alice.PostAsJsonAsync($"/api/deathmatch/{open.DeathMatchId}/settle", new DeathMatchSettleRequest("n"));
        Assert.Equal(HttpStatusCode.BadRequest, settle.StatusCode);
    }

    [Fact]
    public async Task DeathMatch_FullyFundedAbort_IsRefused()
    {
        var (alice, _) = await _factory.RegisterAsync("DM-FullAbort-A");
        var (bob, _) = await _factory.RegisterAsync("DM-FullAbort-B");
        var a = await alice.ClaimStartersAsync();
        var b = await bob.ClaimStartersAsync();

        var open = (await (await alice.PostAsJsonAsync("/api/deathmatch/open",
                new DeathMatchOpenRequest(a[0].Id, b[0].Id)))
            .Content.ReadFromJsonAsync<DeathMatchOpenResponse>())!;
        await alice.PostAsJsonAsync("/api/dev/fund-deathmatch-escrow", new { DeathMatchId = open.DeathMatchId, Role = "challenger" });
        await bob.PostAsync($"/api/deathmatch/{open.DeathMatchId}/accept", null);
        await bob.PostAsJsonAsync("/api/dev/fund-deathmatch-escrow", new { DeathMatchId = open.DeathMatchId, Role = "defender" });

        // Both staked → abort refused (reclaim would be the timelocked refund, not an abort).
        var abort = await alice.PostAsJsonAsync("/api/dev/abort-deathmatch", new { DeathMatchId = open.DeathMatchId });
        Assert.Equal(HttpStatusCode.BadRequest, abort.StatusCode);
    }
}
