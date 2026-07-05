using System.Net;
using System.Net.Http.Json;
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
        var me = (await alice.GetFromJsonAsync<PlayerDto>("/api/players/me"))!;
        Assert.True(me.BalanceSats < 100_000, "Breeding fee should have been paid from the player's wallet.");

        // Reveal without paying is impossible: Bob commits (fresh parents), skips payment.
        var unpaidCommitResponse = await bob.PostAsJsonAsync("/api/breeding/commit",
            new BreedCommitRequest(bobHeroes[0].Id, bobHeroes[1].Id));
        unpaidCommitResponse.EnsureSuccessStatusCode();
        var unpaidCommit = (await unpaidCommitResponse.Content.ReadFromJsonAsync<BreedCommitResponse>())!;
        var unpaidReveal = await bob.PostAsJsonAsync($"/api/breeding/{unpaidCommit.BreedingId}/reveal",
            new BreedRevealRequest("n"));
        Assert.Equal(HttpStatusCode.BadRequest, unpaidReveal.StatusCode);
        var unpaidError = await unpaidReveal.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Contains("invoice", unpaidError!.Error);

        // Parents now on cooldown → immediate rebreed rejected.
        var rebreed = await alice.PostAsJsonAsync("/api/breeding/commit",
            new BreedCommitRequest(aliceHeroes[0].Id, aliceHeroes[1].Id));
        Assert.Equal(HttpStatusCode.BadRequest, rebreed.StatusCode);

        // ── Fight (friendly, no stakes) with client-side replay ────────
        var openResponse = await alice.PostAsJsonAsync("/api/matches/open",
            new OpenMatchRequest(child.Id, bobHeroes[0].Id));
        openResponse.EnsureSuccessStatusCode();
        var open = (await openResponse.Content.ReadFromJsonAsync<OpenMatchResponse>())!;
        Assert.Null(open.StakeInvoice);

        const string fightNonce = "fight-nonce-9";
        var fightResponse = await alice.PostAsJsonAsync($"/api/matches/{open.MatchId}/fight",
            new FightRequest(fightNonce));
        fightResponse.EnsureSuccessStatusCode();
        var fight = (await fightResponse.Content.ReadFromJsonAsync<FightResponse>())!;

        Assert.NotEmpty(fight.Result.Events);
        var (matchOk, matchDetail) = FairnessAudit.VerifyMatch(
            open.MatchId, fightNonce, open.CommitmentHex, fight);
        Assert.True(matchOk, matchDetail);

        // ── Shop: invoice → claim delivers the unit → equip ────────────
        var heroBefore = (await alice.GetFromJsonAsync<HeroDto>($"/api/heroes/{child.Id}"))!;
        var claim = await alice.BuyItemAsync("rusty-blade");
        Assert.Equal(1UL, claim.UnitsHeld);

        var equipResponse = await alice.PostAsJsonAsync($"/api/heroes/{child.Id}/equip",
            new EquipRequest("rusty-blade"));
        equipResponse.EnsureSuccessStatusCode();
        var equip = (await equipResponse.Content.ReadFromJsonAsync<EquipResponse>())!;
        Assert.Equal(heroBefore.Stats.Attack + 4, equip.Hero.Stats.Attack);
    }

    [Fact]
    public async Task RuleViolationsReturn400()
    {
        var (alice, _) = await _factory.RegisterAsync("Carol");
        var heroes = await alice.ClaimStartersAsync();

        // Self-breeding.
        var selfBreed = await alice.PostAsJsonAsync("/api/breeding/commit",
            new BreedCommitRequest(heroes[0].Id, heroes[0].Id));
        Assert.Equal(HttpStatusCode.BadRequest, selfBreed.StatusCode);

        // Double starter claim.
        var again = await alice.PostAsync("/api/heroes/starter", null);
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);

        // Foreign hero use.
        var (mallory, _) = await _factory.RegisterAsync("Mallory");
        await mallory.ClaimStartersAsync();
        var steal = await mallory.PostAsJsonAsync("/api/breeding/commit",
            new BreedCommitRequest(heroes[0].Id, heroes[1].Id));
        Assert.Equal(HttpStatusCode.BadRequest, steal.StatusCode);

        // Missing auth.
        var anonymous = _factory.CreateClient();
        var unauthorized = await anonymous.PostAsync("/api/heroes/starter", null);
        Assert.Equal(HttpStatusCode.BadRequest, unauthorized.StatusCode);

        // Registration without an address is refused — keys must exist client-side.
        var noAddress = await anonymous.PostAsJsonAsync("/api/players",
            new RegisterPlayerRequest("NoWallet", ""));
        Assert.Equal(HttpStatusCode.BadRequest, noAddress.StatusCode);
    }

    [Fact]
    public async Task BreedFee_EscalatesWithParentBreedCount()
    {
        var (alice, _) = await _factory.RegisterAsync("BF-Alice");
        var heroes = await alice.ClaimStartersAsync();

        // Fresh parents → base fee in the invoice.
        var fresh = (await (await alice.PostAsJsonAsync("/api/breeding/commit",
                new BreedCommitRequest(heroes[0].Id, heroes[1].Id)))
            .Content.ReadFromJsonAsync<BreedCommitResponse>())!;
        Assert.Equal(1000, fresh.Invoice!.AmountSats);

        // Bump the parents' combined breed count to 2 → 4x fee.
        var store = _factory.Services.GetRequiredService<ArkadeHeroes.Server.GameStore>();
        store.Heroes[heroes[0].Id].BreedCount = 1;
        store.Heroes[heroes[1].Id].BreedCount = 1;
        var bred = (await (await alice.PostAsJsonAsync("/api/breeding/commit",
                new BreedCommitRequest(heroes[0].Id, heroes[1].Id)))
            .Content.ReadFromJsonAsync<BreedCommitResponse>())!;
        Assert.Equal(4000, bred.Invoice!.AmountSats);
    }
}
