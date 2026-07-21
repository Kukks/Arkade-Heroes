using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Core.Combat;
using ArkadeHeroes.Core.Fairness;
using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Heroes;
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
    public async Task DeathMatch_List_SurfacesOpenAndAccepted_ForBrowserDiscovery()
    {
        var (alice, _) = await _factory.RegisterAsync("DM-List-A");
        var (bob, _) = await _factory.RegisterAsync("DM-List-B");
        var aliceHero = (await alice.ClaimStartersAsync())[0].Id;
        var bobHero = (await bob.ClaimStartersAsync())[0].Id;

        var open = await alice.DeathMatch.OpenAsync(new DeathMatchOpenRequest(aliceHero, bobHero, Absorb: true));

        // The list is the browser's discovery path (the death-match API is otherwise all by-id). It
        // surfaces the open death-match with its parties + absorb flag so the challenged defender finds it.
        var listed = (await bob.DeathMatch.ListAsync()).Single(d => d.DeathMatchId == open.DeathMatchId);
        Assert.Equal("open", listed.Status);
        Assert.Equal(aliceHero, listed.ChallengerHeroId);
        Assert.Equal(bobHero, listed.DefenderHeroId);
        Assert.True(listed.Absorb);
        Assert.Null(listed.WinnerHeroId);

        // Accepting (consent) flips it to "accepted" — ready for the challenger to settle.
        await bob.DeathMatch.AcceptAsync(open.DeathMatchId);
        var afterAccept = (await alice.DeathMatch.ListAsync()).Single(d => d.DeathMatchId == open.DeathMatchId);
        Assert.Equal("accepted", afterAccept.Status);
    }

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

        // Both stake their hero + pay the per-character death-match fee (F13).
        await alice.Dev.FundDeathMatchEscrowAsync(new { DeathMatchId = open.DeathMatchId, Role = "challenger" });
        await alice.Dev.PayInvoiceAsync(new { InvoiceId = open.FeeInvoice!.InvoiceId });
        var accept = await bob.DeathMatch.AcceptAsync(open.DeathMatchId);
        await bob.Dev.FundDeathMatchEscrowAsync(new { DeathMatchId = open.DeathMatchId, Role = "defender" });
        await bob.Dev.PayInvoiceAsync(new { InvoiceId = accept.FeeInvoice!.InvoiceId });

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
        await alice.Dev.PayInvoiceAsync(new { InvoiceId = open.FeeInvoice!.InvoiceId });
        var accept = await bob.DeathMatch.AcceptAsync(open.DeathMatchId);
        await bob.Dev.FundDeathMatchEscrowAsync(new { DeathMatchId = open.DeathMatchId, Role = "defender" });
        await bob.Dev.PayInvoiceAsync(new { InvoiceId = accept.FeeInvoice!.InvoiceId });

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

    /// <summary>Clones a hero with one dominant trait gene set — starters are blank on traits, so this
    /// gives the loser a candidate the winner can absorb (Genome is init-only, so we replace the record).</summary>
    private static Hero WithTrait(Hero h, TraitCategory cat, byte value)
    {
        var bytes = h.Genome.Bytes.ToArray();
        bytes[16 + (int)cat * 2] = value;
        return new Hero
        {
            Id = h.Id, OwnerId = h.OwnerId, Name = h.Name, Genome = new Genome(bytes),
            Generation = h.Generation, ParentAId = h.ParentAId, ParentBId = h.ParentBId,
            Level = h.Level, Xp = h.Xp, BreedCount = h.BreedCount,
            EntropyHex = h.EntropyHex, ServerSeedHex = h.ServerSeedHex, PlayerNonce = h.PlayerNonce,
            AssetId = h.AssetId, MintArkTxId = h.MintArkTxId,
        };
    }

    [Fact]
    public async Task AbsorbDeathMatch_MintsAbsorbedHeroToWinner_BothBurned_Verified()
    {
        // Force the absorb roll to (nearly always) fire; a ~1/256 keep roll just retries fresh heroes.
        using var factory = _factory.WithWebHostBuilder(b => b.UseSetting("Game:AbsorbChance", "255"));
        var store = factory.Services.GetRequiredService<GameStore>();
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var (alice, _) = await factory.RegisterAsync($"DM-Absorb-A{attempt}");
            var (bob, _) = await factory.RegisterAsync($"DM-Absorb-B{attempt}");
            var aliceHeroId = (await alice.ClaimStartersAsync())[0].Id;
            var bobHeroId = (await bob.ClaimStartersAsync())[0].Id;
            store.Heroes[aliceHeroId].Level = 20;                                    // Alice wins (favored)
            store.Heroes[bobHeroId] = WithTrait(store.Heroes[bobHeroId], TraitCategory.Aura, 255); // Bob has a Legendary Aura to absorb

            var aliceBefore = await alice.Heroes.GetAsync(aliceHeroId);
            var bobBefore = await bob.Heroes.GetAsync(bobHeroId);

            var open = await alice.DeathMatch.OpenAsync(new DeathMatchOpenRequest(aliceHeroId, bobHeroId, Absorb: true));
            await alice.Dev.FundDeathMatchEscrowAsync(new { DeathMatchId = open.DeathMatchId, Role = "challenger" });
            await alice.Dev.PayInvoiceAsync(new { InvoiceId = open.FeeInvoice!.InvoiceId });
            var accept = await bob.DeathMatch.AcceptAsync(open.DeathMatchId);
            await bob.Dev.FundDeathMatchEscrowAsync(new { DeathMatchId = open.DeathMatchId, Role = "defender" });
            await bob.Dev.PayInvoiceAsync(new { InvoiceId = accept.FeeInvoice!.InvoiceId });
            var settle = await alice.DeathMatch.SettleAsync(open.DeathMatchId, new DeathMatchSettleRequest("absorb-nonce"));
            if (settle.WinnerHeroId != aliceHeroId) continue;   // rare seeded-fight upset — retry with fresh heroes
            if (!settle.Minted) continue;                        // rare keep roll — try again with fresh heroes

            Assert.Equal(aliceHeroId, settle.WinnerHeroId);
            Assert.NotNull(settle.NewHero);
            Assert.True(settle.TraitsAbsorbed >= 1);

            // BOTH old heroes are gone; the NEW absorbed hero belongs to the winner.
            var aliceMine = await alice.Heroes.MineAsync();
            Assert.DoesNotContain(aliceMine, h => h.Id == aliceHeroId);
            Assert.DoesNotContain(aliceMine, h => h.Id == bobHeroId);
            var absorbed = aliceMine.Single(h => h.Id == settle.NewHero!.Id);
            Assert.Equal(20, absorbed.Level);                                        // inherited the winner's level
            Assert.Equal(255, Genome.FromHex(absorbed.GenomeHex).DominantGene(TraitCategory.Aura)); // absorbed Bob's Aura

            // Client-verifiable: recompute Absorb.Resolve from the revealed seed + published odds.
            var verify = FairnessAudit.VerifyAbsorb(open.DeathMatchId, aliceBefore, bobBefore, challengerWon: true,
                "absorb-nonce", open.CommitmentHex, new AbsorbOdds(255, 90),
                settle.Minted, settle.NewGenomeHex, settle.ServerSeedHex, settle.EntropyHex);
            Assert.True(verify.Ok, verify.Detail);

            // The absorb receipt replays to the inherited level (progression preserved).
            Assert.Equal("absorb", settle.Receipt!.Type);
            Assert.Equal(20, ReceiptVerifier.ReplayLevel(settle.NewHero!.Id, [settle.Receipt!]));
            return;
        }
        Assert.Fail("expected an absorb mint within 8 attempts at AbsorbChance=255");
    }

    [Fact]
    public async Task AbsorbDeathMatch_KeepRoll_WinnerKeepsExactHero()
    {
        // AbsorbChance=0 → the roll never fires → the classic keep outcome, even with a candidate present.
        using var factory = _factory.WithWebHostBuilder(b => b.UseSetting("Game:AbsorbChance", "0"));
        var (alice, _) = await factory.RegisterAsync("DM-Keep-A");
        var (bob, _) = await factory.RegisterAsync("DM-Keep-B");
        var a = await alice.ClaimStartersAsync();
        var b = await bob.ClaimStartersAsync();
        var store = factory.Services.GetRequiredService<GameStore>();
        store.Heroes[a[0].Id].Level = 20;
        store.Heroes[b[0].Id] = WithTrait(store.Heroes[b[0].Id], TraitCategory.Aura, 255); // a candidate exists, but chance is 0

        var open = await alice.DeathMatch.OpenAsync(new DeathMatchOpenRequest(a[0].Id, b[0].Id, Absorb: true));
        await alice.Dev.FundDeathMatchEscrowAsync(new { DeathMatchId = open.DeathMatchId, Role = "challenger" });
        await alice.Dev.PayInvoiceAsync(new { InvoiceId = open.FeeInvoice!.InvoiceId });
        var accept = await bob.DeathMatch.AcceptAsync(open.DeathMatchId);
        await bob.Dev.FundDeathMatchEscrowAsync(new { DeathMatchId = open.DeathMatchId, Role = "defender" });
        await bob.Dev.PayInvoiceAsync(new { InvoiceId = accept.FeeInvoice!.InvoiceId });
        var settle = await alice.DeathMatch.SettleAsync(open.DeathMatchId, new DeathMatchSettleRequest("keep-nonce"));

        Assert.False(settle.Minted);
        Assert.Null(settle.NewHero);
        // Classic keep (AbsorbChance=0 → the roll never fires): the WINNER keeps their EXACT hero and the
        // loser's is burned. A level edge favours Alice, but the death-match fight is SEEDED — Bob wins the
        // occasional upset — so assert on the actual winner rather than assuming Alice took it.
        var winnerClient = settle.WinnerHeroId == a[0].Id ? alice : bob;
        var winnerMine = await winnerClient.Heroes.MineAsync();
        Assert.Contains(winnerMine, h => h.Id == settle.WinnerHeroId);
        Assert.DoesNotContain(winnerMine, h => h.Id == settle.LoserHeroId);
        Assert.Equal("deathmatch", settle.Receipt!.Type);
    }

    [Fact]
    public async Task DeathMatch_SettleRefusedUntilBothFeesPaid_FeeScalesWithLevel()
    {
        var (alice, _) = await _factory.RegisterAsync("DM-Fee-A");
        var (bob, _) = await _factory.RegisterAsync("DM-Fee-B");
        var a = await alice.ClaimStartersAsync();
        var b = await bob.ClaimStartersAsync();
        var store = _factory.Services.GetRequiredService<GameStore>();
        store.Heroes[a[0].Id].Level = 10;   // challenger level 10 → higher fee than the level-1 defender

        var open = await alice.DeathMatch.OpenAsync(new DeathMatchOpenRequest(a[0].Id, b[0].Id));
        Assert.Equal(2 * (500 + 20 * 10), open.FeeInvoice!.AmountSats);   // classic = 2× MatchFee(10)

        await alice.Dev.FundDeathMatchEscrowAsync(new { DeathMatchId = open.DeathMatchId, Role = "challenger" });
        var accept = await bob.DeathMatch.AcceptAsync(open.DeathMatchId);
        await bob.Dev.FundDeathMatchEscrowAsync(new { DeathMatchId = open.DeathMatchId, Role = "defender" });
        Assert.Equal(2 * (500 + 20 * 1), accept.FeeInvoice!.AmountSats);  // defender level 1

        // Fully staked but no fee paid → settle refused.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => alice.DeathMatch.SettleAsync(open.DeathMatchId, new DeathMatchSettleRequest("n")));
        // Only the challenger's fee paid → still refused.
        await alice.Dev.PayInvoiceAsync(new { InvoiceId = open.FeeInvoice!.InvoiceId });
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => alice.DeathMatch.SettleAsync(open.DeathMatchId, new DeathMatchSettleRequest("n")));
        // Both fees paid → settles.
        await bob.Dev.PayInvoiceAsync(new { InvoiceId = accept.FeeInvoice!.InvoiceId });
        var settle = await alice.DeathMatch.SettleAsync(open.DeathMatchId, new DeathMatchSettleRequest("n"));
        Assert.Equal("deathmatch", settle.Receipt!.Type);
    }

    [Fact]
    public async Task AbsorbDeathMatch_FeeIsHigherThanClassic()
    {
        var (alice, _) = await _factory.RegisterAsync("DM-AbsorbFee-A");
        var (bob, _) = await _factory.RegisterAsync("DM-AbsorbFee-B");
        var a = await alice.ClaimStartersAsync();
        var b = await bob.ClaimStartersAsync();

        var classic = await alice.DeathMatch.OpenAsync(new DeathMatchOpenRequest(a[0].Id, b[0].Id));
        var absorb = await alice.DeathMatch.OpenAsync(new DeathMatchOpenRequest(a[1].Id, b[0].Id, Absorb: true));

        Assert.Equal(2 * (500 + 20 * 1), classic.FeeInvoice!.AmountSats);  // 2× MatchFee(1)
        Assert.Equal(3 * (500 + 20 * 1), absorb.FeeInvoice!.AmountSats);   // 3× MatchFee(1) — the absorb premium
    }
}
