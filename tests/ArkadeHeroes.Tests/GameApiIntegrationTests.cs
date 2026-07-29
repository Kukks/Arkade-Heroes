using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Heroes;
using ArkadeHeroes.Core.Progression;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ArkadeHeroes.Tests;

/// <summary>
/// Full game loop over the real HTTP surface (in-memory chain, non-custodial
/// semantics): register with own address → starters → breed (invoice paid by
/// the simulated client wallet, commit–reveal client-audited) → fight
/// (client-replayed) → shop (invoice → claim → equip).
/// </summary>
public class GameApiIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public GameApiIntegrationTests(WebApplicationFactory<Program> factory)
        => _factory = factory;

    [Fact]
    public async Task FullGameLoop_Register_Starter_Breed_Fight_Shop()
    {
        var (alice, alicePlayer) = await _factory.RegisterAsync("Alice");
        var (bob, _) = await _factory.RegisterAsync("Bob");
        Assert.StartsWith("sim-wallet-", alicePlayer.ArkadeAddress);

        // ── Starters ───────────────────────────────────────────────────
        var aliceHeroes = await alice.ClaimStartersAsync();
        var bobHeroes = await bob.ClaimStartersAsync();
        Assert.Equal(2, aliceHeroes.Count);
        Assert.All(aliceHeroes, h => Assert.Equal(0, h.Generation));
        Assert.All(aliceHeroes, h => Assert.NotNull(h.AssetId));

        // ── Breed: invoice → simulated-wallet payment → reveal + audit ─
        const string nonce = "alice-nonce-123";
        var (commit, reveal) = await alice.BreedAsync(aliceHeroes[0].Id, aliceHeroes[1].Id, nonce);

        var child = reveal.Hero;
        Assert.Equal(1, child.Generation);
        Assert.Equal(aliceHeroes[0].Id, child.ParentAId);

        var (breedOk, breedDetail) = FairnessAudit.VerifyBreeding(
            aliceHeroes[0], aliceHeroes[1], nonce, commit.CommitmentHex, reveal);
        Assert.True(breedOk, breedDetail);

        // Fee left the (simulated) client wallet.
        var me = await alice.Players.MeAsync();
        Assert.True(me.BalanceSats < 100_000, "Breeding fee should have been paid from the player's wallet.");

        // Reveal without paying is impossible: Bob commits (fresh parents), skips payment.
        var unpaidCommit = await bob.Breeding.CommitAsync(
            new BreedCommitRequest(bobHeroes[0].Id, bobHeroes[1].Id));
        var unpaidError = await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => bob.Breeding.RevealAsync(unpaidCommit.BreedingId, new BreedRevealRequest("n")));
        Assert.Contains("invoice", unpaidError.Message);

        // Parents now on cooldown → immediate rebreed rejected.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => alice.Breeding.CommitAsync(new BreedCommitRequest(aliceHeroes[0].Id, aliceHeroes[1].Id)));

        // ── Fight (friendly, no stakes) with client-side replay ────────
        var open = await alice.Matches.OpenAsync(new OpenMatchRequest(child.Id, bobHeroes[0].Id));
        Assert.Null(open.StakeInvoice);

        const string fightNonce = "fight-nonce-9";
        var fight = await alice.Matches.FightAsync(open.MatchId, new FightRequest(fightNonce));

        Assert.NotEmpty(fight.Result.Events);
        // Replay under the rules the server STAMPED this fight with, exactly as the browser and console do.
        // The challenger here is a BRED child, so if it inherited an expressed cosmetic trait (mutation, in
        // roughly one child in seven) an innate proc fires and a GameConfig.Default replay legitimately
        // diverges -- which is the honest verdict for a client that ignored the stamp, not a flake.
        var (cfg, cfgError) = await alice.Config.ResolveAsync(fight.ConfigVersion);
        Assert.Null(cfgError);
        var (matchOk, matchDetail) = FairnessAudit.VerifyMatch(
            open.MatchId, fightNonce, open.CommitmentHex, fight, cfg);
        Assert.True(matchOk, matchDetail);

        // ── Shop: invoice → claim delivers the unit → equip ────────────
        var heroBefore = await alice.Heroes.GetAsync(child.Id);
        var claim = await alice.BuyItemAsync("rusty-blade");
        Assert.Equal(1UL, claim.UnitsHeld);

        var equip = await alice.Heroes.EquipAsync(child.Id, new EquipRequest("rusty-blade"));
        Assert.Equal(heroBefore.Stats.Attack + 4, equip.Hero.Stats.Attack);
    }

    [Fact]
    public async Task RuleViolationsReturn400()
    {
        var (alice, _) = await _factory.RegisterAsync("Carol");
        var heroes = await alice.ClaimStartersAsync();

        // Self-breeding.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => alice.Breeding.CommitAsync(new BreedCommitRequest(heroes[0].Id, heroes[0].Id)));

        // Double starter claim.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => alice.Heroes.ClaimStartersAsync());

        // Foreign hero use.
        var (mallory, _) = await _factory.RegisterAsync("Mallory");
        await mallory.ClaimStartersAsync();
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => mallory.Breeding.CommitAsync(new BreedCommitRequest(heroes[0].Id, heroes[1].Id)));

        // Missing auth.
        var anonymous = new ArkadeHeroesClient(_factory.CreateClient());
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => anonymous.Heroes.ClaimStartersAsync());

        // Registration without an address is refused — keys must exist client-side.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => anonymous.Players.RegisterAsync(new RegisterPlayerRequest("NoWallet", "")));
    }

    [Fact]
    public async Task BreedFee_EscalatesWithParentBreedCount()
    {
        var (alice, _) = await _factory.RegisterAsync("BF-Alice");
        var heroes = await alice.ClaimStartersAsync();

        // Fresh parents → base fee in the invoice.
        var fresh = await alice.Breeding.CommitAsync(new BreedCommitRequest(heroes[0].Id, heroes[1].Id));
        Assert.Equal(1000, fresh.Invoice!.AmountSats);

        // Bump the parents' combined breed count to 2 → 4x fee.
        var store = _factory.Services.GetRequiredService<ArkadeHeroes.Server.GameStore>();
        store.Heroes[heroes[0].Id].BreedCount = 1;
        store.Heroes[heroes[1].Id].BreedCount = 1;
        var bred = await alice.Breeding.CommitAsync(new BreedCommitRequest(heroes[0].Id, heroes[1].Id));
        Assert.Equal(4000, bred.Invoice!.AmountSats);
    }

    [Fact]
    public async Task SterileHero_CannotBreed()
    {
        var (alice, _) = await _factory.RegisterAsync("Sterile-Alice");
        var heroes = await alice.ClaimStartersAsync();
        var store = _factory.Services.GetRequiredService<ArkadeHeroes.Server.GameStore>();

        // Find a Legendary genome that rolls sterile (~50% chance → found fast).
        Genome? sterileGenome = null;
        for (byte m = 0; m < 200 && sterileGenome is null; m++)
        {
            var b = new byte[32];
            b[16 + (int)TraitCategory.Aura * 2] = 255; // Legendary
            b[0] = m;
            var g = new Genome(b);
            if (Sterility.IsSterile(g)) sterileGenome = g;
        }
        Assert.NotNull(sterileGenome);

        // Inject the sterile hero, owned by Alice (OwnerId from a claimed starter).
        store.Heroes["sterile"] = new Hero
            { Id = "sterile", OwnerId = heroes[0].OwnerId, Name = "TheLast", Genome = sterileGenome!.Value, Generation = 3 };

        var ex = await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => alice.Breeding.CommitAsync(new BreedCommitRequest("sterile", heroes[0].Id)));
        Assert.Contains("sterile", ex.Message);
    }

    [Fact]
    public async Task Heroes_AreRarityOrderedAndPaged()
    {
        var (alice, _) = await _factory.RegisterAsync("Pager");
        var starters = await alice.ClaimStartersAsync();
        var owner = starters[0].OwnerId;

        // Inject a clearly-rarest hero so the rarity sort is observable.
        var store = _factory.Services.GetRequiredService<ArkadeHeroes.Server.GameStore>();
        var b = new byte[32];
        b[16 + (int)TraitCategory.Aura * 2] = 255; // Legendary aura → top rarity score
        store.Heroes["legend"] = new Hero
            { Id = "legend", OwnerId = owner, Name = "Legend", Genome = new Genome(b), Generation = 1 };

        // Full set (no take): rarity-descending, the injected hero first.
        var all = await alice.Heroes.MineAsync();
        Assert.Equal(3, all.Count);
        Assert.Equal("legend", all[0].Id);
        for (var i = 1; i < all.Count; i++)
            Assert.True((all[i - 1].Rarity?.Score ?? 0) >= (all[i].Rarity?.Score ?? 0), "heroes must be rarity-descending");

        // Paged: take slices the same ordered sequence; pages reconstruct the full set.
        var page0 = await alice.Heroes.MineAsync(0, 2);
        var page1 = await alice.Heroes.MineAsync(2, 2);
        Assert.Equal(new[] { all[0].Id, all[1].Id }, page0.Select(h => h.Id));
        Assert.Equal(new[] { all[2].Id }, page1.Select(h => h.Id));

        // Skip past the end → empty.
        Assert.Empty(await alice.Heroes.MineAsync(3, 2));
    }
}
