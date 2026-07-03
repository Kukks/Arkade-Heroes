using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ArkadeHeroes.Core.Combat;
using ArkadeHeroes.Core.Fairness;
using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Heroes;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ArkadeHeroes.Tests;

/// <summary>
/// Full game loop over the real HTTP surface (in-memory chain): register →
/// starters → breed (commit–reveal, client-audited) → fight (client-replayed)
/// → shop. The audit steps double as the provable-fairness contract tests.
/// </summary>
public class GameApiIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public GameApiIntegrationTests(WebApplicationFactory<Program> factory)
        => _factory = factory;

    private async Task<(HttpClient Client, PlayerDto Player)> RegisterAsync(string name)
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/players", new RegisterPlayerRequest(name));
        response.EnsureSuccessStatusCode();
        var player = (await response.Content.ReadFromJsonAsync<PlayerDto>())!;
        Assert.NotNull(player.Token);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", player.Token);
        return (client, player);
    }

    private static async Task<List<HeroDto>> ClaimStartersAsync(HttpClient client)
    {
        var response = await client.PostAsync("/api/heroes/starter", null);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<StarterResponse>())!.Heroes.ToList();
    }

    [Fact]
    public async Task FullGameLoop_Register_Starter_Breed_Fight_Shop()
    {
        var (alice, alicePlayer) = await RegisterAsync("Alice");
        var (bob, _) = await RegisterAsync("Bob");

        // ── Starters ───────────────────────────────────────────────────
        var aliceHeroes = await ClaimStartersAsync(alice);
        var bobHeroes = await ClaimStartersAsync(bob);
        Assert.Equal(2, aliceHeroes.Count);
        Assert.All(aliceHeroes, h => Assert.Equal(0, h.Generation));
        Assert.All(aliceHeroes, h => Assert.NotNull(h.AssetId));

        // ── Breed (commit → reveal) with client-side audit ─────────────
        var commitResponse = await alice.PostAsJsonAsync("/api/breeding/commit",
            new BreedCommitRequest(aliceHeroes[0].Id, aliceHeroes[1].Id));
        commitResponse.EnsureSuccessStatusCode();
        var commit = (await commitResponse.Content.ReadFromJsonAsync<BreedCommitResponse>())!;

        const string nonce = "alice-nonce-123";
        var revealResponse = await alice.PostAsJsonAsync($"/api/breeding/{commit.BreedingId}/reveal",
            new BreedRevealRequest(nonce));
        revealResponse.EnsureSuccessStatusCode();
        var reveal = (await revealResponse.Content.ReadFromJsonAsync<BreedRevealResponse>())!;

        var child = reveal.Hero;
        Assert.Equal(1, child.Generation);
        Assert.Equal(aliceHeroes[0].Id, child.ParentAId);
        Assert.Equal(aliceHeroes[1].Id, child.ParentBId);

        // Audit: seed → commitment, entropy derivation, genome = Mix(parents, entropy).
        var (breedOk, breedDetail) = FairnessAudit.VerifyBreeding(
            aliceHeroes[0], aliceHeroes[1], nonce, commit.CommitmentHex, reveal);
        Assert.True(breedOk, breedDetail);

        // Breeding fee was charged.
        var me = (await alice.GetFromJsonAsync<PlayerDto>("/api/players/me"))!;
        Assert.True(me.BalanceSats < 100_000, "Breeding fee should have been deducted.");

        // Parents now on cooldown → immediate rebreed rejected.
        var rebreed = await alice.PostAsJsonAsync("/api/breeding/commit",
            new BreedCommitRequest(aliceHeroes[0].Id, aliceHeroes[1].Id));
        Assert.Equal(HttpStatusCode.BadRequest, rebreed.StatusCode);

        // ── Fight (open → fight) with client-side replay ───────────────
        var openResponse = await alice.PostAsJsonAsync("/api/matches/open",
            new OpenMatchRequest(child.Id, bobHeroes[0].Id));
        openResponse.EnsureSuccessStatusCode();
        var open = (await openResponse.Content.ReadFromJsonAsync<OpenMatchResponse>())!;

        const string fightNonce = "fight-nonce-9";
        var fightResponse = await alice.PostAsJsonAsync($"/api/matches/{open.MatchId}/fight",
            new FightRequest(fightNonce));
        fightResponse.EnsureSuccessStatusCode();
        var fight = (await fightResponse.Content.ReadFromJsonAsync<FightResponse>())!;

        Assert.Contains(fight.Result.WinnerId, new[] { child.Id, bobHeroes[0].Id });
        Assert.NotEmpty(fight.Result.Events);
        Assert.True(fight.ChallengerXpAward > 0 && fight.DefenderXpAward > 0);

        // Audit: seed → commitment, entropy reproducible, battle replays
        // identically from the pre-fight snapshots (the shipped audit utility).
        var (matchOk, matchDetail) = FairnessAudit.VerifyMatch(
            open.MatchId, fightNonce, open.CommitmentHex, fight);
        Assert.True(matchOk, matchDetail);

        // ── Shop: buy + equip changes stats and charges the price ──────
        var heroBefore = (await alice.GetFromJsonAsync<HeroDto>($"/api/heroes/{child.Id}"))!;
        var equipResponse = await alice.PostAsJsonAsync($"/api/heroes/{child.Id}/equip",
            new EquipRequest("rusty-blade"));
        equipResponse.EnsureSuccessStatusCode();
        var equip = (await equipResponse.Content.ReadFromJsonAsync<EquipResponse>())!;
        Assert.Equal(heroBefore.Stats.Attack + 4, equip.Hero.Stats.Attack);
        Assert.True(equip.BalanceSats < me.BalanceSats);
    }

    [Fact]
    public async Task RuleViolationsReturn400()
    {
        var (alice, _) = await RegisterAsync("Carol");
        var heroes = await ClaimStartersAsync(alice);

        // Self-breeding.
        var selfBreed = await alice.PostAsJsonAsync("/api/breeding/commit",
            new BreedCommitRequest(heroes[0].Id, heroes[0].Id));
        Assert.Equal(HttpStatusCode.BadRequest, selfBreed.StatusCode);
        var error = await selfBreed.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.False(string.IsNullOrWhiteSpace(error!.Error));

        // Double starter claim.
        var again = await alice.PostAsync("/api/heroes/starter", null);
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);

        // Foreign hero use.
        var (mallory, _) = await RegisterAsync("Mallory");
        await ClaimStartersAsync(mallory);
        var steal = await mallory.PostAsJsonAsync("/api/breeding/commit",
            new BreedCommitRequest(heroes[0].Id, heroes[1].Id));
        Assert.Equal(HttpStatusCode.BadRequest, steal.StatusCode);

        // Missing auth.
        var anonymous = _factory.CreateClient();
        var unauthorized = await anonymous.PostAsync("/api/heroes/starter", null);
        Assert.Equal(HttpStatusCode.BadRequest, unauthorized.StatusCode);
    }
}
