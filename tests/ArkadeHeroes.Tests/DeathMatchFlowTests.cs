using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Core.Combat;
using ArkadeHeroes.Core.Fairness;
using ArkadeHeroes.Server;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Hosting;
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

        var open = await alice.DeathMatch.OpenAsync(new DeathMatchOpenRequest(aliceHeroId, bobHeroId));
        Assert.Equal("favored", open.Favorability.Label);          // Alice is 19 levels up
        Assert.Equal(levelBefore[bobHeroId] - 20, open.Favorability.LevelGap); // signed: theirs − mine

        // Capture pre-settle snapshots — settle deletes the loser's record.
        var aliceBefore = await alice.Heroes.GetAsync(aliceHeroId);
        var bobBefore = await bob.Heroes.GetAsync(bobHeroId);

        // Both stake their hero.
        await alice.Dev.FundDeathMatchEscrowAsync(new { DeathMatchId = open.DeathMatchId, Role = "challenger" });
        await bob.DeathMatch.AcceptAsync(open.DeathMatchId);
        await bob.Dev.FundDeathMatchEscrowAsync(new { DeathMatchId = open.DeathMatchId, Role = "defender" });

        var settle = await alice.DeathMatch.SettleAsync(open.DeathMatchId, new DeathMatchSettleRequest("dm-nonce"));

        // The loser's hero is BURNED — gone from BOTH players' wallets (outcome-agnostic).
        var aliceMine = await alice.Heroes.MineAsync();
        var bobMine = await bob.Heroes.MineAsync();
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

        var open = await alice.DeathMatch.OpenAsync(new DeathMatchOpenRequest(a[0].Id, b[0].Id));
        await alice.Dev.FundDeathMatchEscrowAsync(new { DeathMatchId = open.DeathMatchId, Role = "challenger" });
        // Bob never accepts/stakes → settle is refused (deposit-gated).
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => alice.DeathMatch.SettleAsync(open.DeathMatchId, new DeathMatchSettleRequest("n")));
    }

    [Fact]
    public async Task DeathMatch_RejectsUnownedChallenger()
    {
        var (alice, _) = await _factory.RegisterAsync("DM-Own-A");
        var (bob, _) = await _factory.RegisterAsync("DM-Own-B");
        var a = await alice.ClaimStartersAsync();
        var b = await bob.ClaimStartersAsync();
        // Alice tries to stake BOB's hero as the challenger.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => alice.DeathMatch.OpenAsync(new DeathMatchOpenRequest(b[0].Id, a[0].Id)));
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
        await bob.Heroes.EquipAsync(b[0].Id, new EquipRequest("rusty-blade"));

        // Open bakes Bob's loadout as his required stake; Alice has none.
        var open = await alice.DeathMatch.OpenAsync(new DeathMatchOpenRequest(a[0].Id, b[0].Id));
        var stake = Assert.Single(open.DefenderGear);
        Assert.Equal("rusty-blade", stake.ItemId);
        Assert.Equal(1, stake.Amount);
        Assert.Empty(open.ChallengerGear);

        await alice.Dev.FundDeathMatchEscrowAsync(new { DeathMatchId = open.DeathMatchId, Role = "challenger" });
        await bob.DeathMatch.AcceptAsync(open.DeathMatchId);
        await bob.Dev.FundDeathMatchEscrowAsync(new { DeathMatchId = open.DeathMatchId, Role = "defender" });

        // Staking moved Bob's unit INTO the escrow — he no longer holds it (can't sell it).
        Assert.Equal(0UL, await chain.GetItemAssetBalanceAsync(bobPlayer.PlayerId, "rusty-blade"));

        var settle = await alice.DeathMatch.SettleAsync(open.DeathMatchId, new DeathMatchSettleRequest("gear-nonce"));

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
        await bob.Heroes.EquipAsync(b[0].Id, new EquipRequest("rusty-blade"));

        var open1 = await alice.DeathMatch.OpenAsync(new DeathMatchOpenRequest(a[0].Id, b[0].Id));
        var open2 = await alice.DeathMatch.OpenAsync(new DeathMatchOpenRequest(a[1].Id, b[0].Id));

        // Funding match 1 moves the unit into ITS escrow; the same unit cannot fund match 2.
        await bob.DeathMatch.AcceptAsync(open1.DeathMatchId);
        await bob.Dev.FundDeathMatchEscrowAsync(new { DeathMatchId = open1.DeathMatchId, Role = "defender" });

        await bob.DeathMatch.AcceptAsync(open2.DeathMatchId);
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => bob.Dev.FundDeathMatchEscrowAsync(new { DeathMatchId = open2.DeathMatchId, Role = "defender" }));
    }

    [Fact]
    public async Task DeathMatch_HalfFundedChallengerReclaimsAfterExpiry_GearReturned()
    {
        // Zero reclaim window so the abandoned stake is immediately reclaimable in-test.
        using var factory = _factory.WithWebHostBuilder(b => b.UseSetting("Game:WagerEscrowRefundAfter", "00:00:00"));
        var (alice, alicePlayer) = await factory.RegisterAsync("DM-Reclaim-A");
        var (bob, _) = await factory.RegisterAsync("DM-Reclaim-B");
        var a = await alice.ClaimStartersAsync();
        var b = await bob.ClaimStartersAsync();
        var chain = factory.Services.GetRequiredService<ArkadeHeroes.Chain.IChainService>();

        // Alice (challenger) equips gear — baked into her stake at open.
        await alice.BuyItemAsync("rusty-blade");
        await alice.Heroes.EquipAsync(a[0].Id, new EquipRequest("rusty-blade"));

        var open = await alice.DeathMatch.OpenAsync(new DeathMatchOpenRequest(a[0].Id, b[0].Id));
        var stake = Assert.Single(open.ChallengerGear);
        Assert.Equal("rusty-blade", stake.ItemId);

        // Alice stakes her hero + gear; Bob never accepts → half-funded.
        await alice.Dev.FundDeathMatchEscrowAsync(new { DeathMatchId = open.DeathMatchId, Role = "challenger" });
        Assert.Equal(0UL, await chain.GetItemAssetBalanceAsync(alicePlayer.PlayerId, "rusty-blade")); // staked into escrow

        // Post-expiry reclaim → Alice reclaims her staked gear.
        await alice.Dev.ReclaimDeathMatchAsync(new { DeathMatchId = open.DeathMatchId });
        Assert.Equal(1UL, await chain.GetItemAssetBalanceAsync(alicePlayer.PlayerId, "rusty-blade")); // returned

        // Settle is now refused — the escrow is emptied (funded-check fails).
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => alice.DeathMatch.SettleAsync(open.DeathMatchId, new DeathMatchSettleRequest("n")));
    }

    [Fact]
    public async Task DeathMatch_FullyFundedEachSideReclaimsAfterExpiry()
    {
        using var factory = _factory.WithWebHostBuilder(b => b.UseSetting("Game:WagerEscrowRefundAfter", "00:00:00"));
        var (alice, _) = await factory.RegisterAsync("DM-FullReclaim-A");
        var (bob, _) = await factory.RegisterAsync("DM-FullReclaim-B");
        var a = await alice.ClaimStartersAsync();
        var b = await bob.ClaimStartersAsync();

        var open = await alice.DeathMatch.OpenAsync(new DeathMatchOpenRequest(a[0].Id, b[0].Id));
        await alice.Dev.FundDeathMatchEscrowAsync(new { DeathMatchId = open.DeathMatchId, Role = "challenger" });
        await bob.DeathMatch.AcceptAsync(open.DeathMatchId);
        await bob.Dev.FundDeathMatchEscrowAsync(new { DeathMatchId = open.DeathMatchId, Role = "defender" });

        // Each side reclaims their OWN stake post-expiry.
        await alice.Dev.ReclaimDeathMatchAsync(new { DeathMatchId = open.DeathMatchId });
        await bob.Dev.ReclaimDeathMatchAsync(new { DeathMatchId = open.DeathMatchId });
    }

    [Fact]
    public async Task DeathMatch_ReclaimBeforeExpiry_IsRefused()
    {
        // Default 24h window: a reclaim right after staking is locked.
        var (alice, _) = await _factory.RegisterAsync("DM-EarlyReclaim-A");
        var (bob, _) = await _factory.RegisterAsync("DM-EarlyReclaim-B");
        var a = await alice.ClaimStartersAsync();
        var b = await bob.ClaimStartersAsync();

        var open = await alice.DeathMatch.OpenAsync(new DeathMatchOpenRequest(a[0].Id, b[0].Id));
        await alice.Dev.FundDeathMatchEscrowAsync(new { DeathMatchId = open.DeathMatchId, Role = "challenger" });

        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => alice.Dev.ReclaimDeathMatchAsync(new { DeathMatchId = open.DeathMatchId }));
    }
}
