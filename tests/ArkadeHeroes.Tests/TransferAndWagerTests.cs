using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ArkadeHeroes.Chain;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ArkadeHeroes.Tests;

public class TransferAndWagerTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public TransferAndWagerTests(WebApplicationFactory<Program> factory)
        => _factory = factory;

    private async Task<(HttpClient Client, PlayerDto Player, List<HeroDto> Heroes)> PlayerWithStartersAsync(string name)
    {
        var client = _factory.CreateClient();
        var registerResponse = await client.PostAsJsonAsync("/api/players", new RegisterPlayerRequest(name));
        registerResponse.EnsureSuccessStatusCode();
        var player = (await registerResponse.Content.ReadFromJsonAsync<PlayerDto>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", player.Token);

        var starterResponse = await client.PostAsync("/api/heroes/starter", null);
        starterResponse.EnsureSuccessStatusCode();
        var heroes = (await starterResponse.Content.ReadFromJsonAsync<StarterResponse>())!.Heroes.ToList();
        return (client, player, heroes);
    }

    [Fact]
    public async Task TransferMovesHeroBetweenPlayers()
    {
        var (alice, alicePlayer, aliceHeroes) = await PlayerWithStartersAsync("T-Alice");
        var (bob, bobPlayer, _) = await PlayerWithStartersAsync("T-Bob");
        var gift = aliceHeroes[0];

        var response = await alice.PostAsJsonAsync($"/api/heroes/{gift.Id}/transfer",
            new TransferRequest(bobPlayer.PlayerId));
        response.EnsureSuccessStatusCode();
        var transfer = (await response.Content.ReadFromJsonAsync<TransferResponse>())!;
        Assert.Equal(bobPlayer.PlayerId, transfer.Hero.OwnerId);
        Assert.False(string.IsNullOrEmpty(transfer.ArkTxId));

        // Bob now owns it; Alice doesn't.
        var bobHeroes = (await bob.GetFromJsonAsync<List<HeroDto>>("/api/heroes/mine"))!;
        Assert.Contains(bobHeroes, h => h.Id == gift.Id);
        var aliceMine = (await alice.GetFromJsonAsync<List<HeroDto>>("/api/heroes/mine"))!;
        Assert.DoesNotContain(aliceMine, h => h.Id == gift.Id);

        // Alice can no longer act with it.
        var stale = await alice.PostAsJsonAsync("/api/breeding/commit",
            new BreedCommitRequest(gift.Id, aliceHeroes[1].Id));
        Assert.Equal(HttpStatusCode.BadRequest, stale.StatusCode);

        // Bob can transfer it back (round trip).
        var back = await bob.PostAsJsonAsync($"/api/heroes/{gift.Id}/transfer",
            new TransferRequest(alicePlayer.PlayerId));
        back.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task WageredMatchEscrowsStakesAndPaysWinner()
    {
        var (alice, alicePlayer, aliceHeroes) = await PlayerWithStartersAsync("W-Alice");
        var (bob, bobPlayer, bobHeroes) = await PlayerWithStartersAsync("W-Bob");
        const long wager = 5_000;
        var start = InMemoryChainService.FaucetSats;

        // Open: challenger stake escrowed immediately.
        var openResponse = await alice.PostAsJsonAsync("/api/matches/open",
            new OpenMatchRequest(aliceHeroes[0].Id, bobHeroes[0].Id, wager));
        openResponse.EnsureSuccessStatusCode();
        var open = (await openResponse.Content.ReadFromJsonAsync<OpenMatchResponse>())!;
        Assert.Equal("open", open.Status);
        var aliceAfterOpen = (await alice.GetFromJsonAsync<PlayerDto>("/api/players/me"))!;
        Assert.Equal(start - wager, aliceAfterOpen.BalanceSats);

        // Fight before acceptance is rejected.
        var early = await alice.PostAsJsonAsync($"/api/matches/{open.MatchId}/fight",
            new FightRequest("early"));
        Assert.Equal(HttpStatusCode.BadRequest, early.StatusCode);

        // Only the defender's owner may accept.
        var wrongAccept = await alice.PostAsync($"/api/matches/{open.MatchId}/accept", null);
        Assert.Equal(HttpStatusCode.BadRequest, wrongAccept.StatusCode);

        // Accept: defender stake escrowed.
        var accept = await bob.PostAsync($"/api/matches/{open.MatchId}/accept", null);
        accept.EnsureSuccessStatusCode();
        var bobAfterAccept = (await bob.GetFromJsonAsync<PlayerDto>("/api/players/me"))!;
        Assert.Equal(start - wager, bobAfterAccept.BalanceSats);

        // Duel: pot pays the winner's owner; replay audit still holds.
        var fightResponse = await alice.PostAsJsonAsync($"/api/matches/{open.MatchId}/fight",
            new FightRequest("duel-nonce"));
        fightResponse.EnsureSuccessStatusCode();
        var fight = (await fightResponse.Content.ReadFromJsonAsync<FightResponse>())!;
        Assert.Equal(wager, fight.WagerSats);
        Assert.Equal(wager * 2, fight.WinnerPayoutSats);

        var (ok, detail) = FairnessAudit.VerifyMatch(open.MatchId, "duel-nonce", open.CommitmentHex, fight);
        Assert.True(ok, detail);

        var aliceFinal = (await alice.GetFromJsonAsync<PlayerDto>("/api/players/me"))!;
        var bobFinal = (await bob.GetFromJsonAsync<PlayerDto>("/api/players/me"))!;
        var challengerWon = fight.Result.WinnerId == aliceHeroes[0].Id;
        var (winnerBalance, loserBalance) = challengerWon
            ? (aliceFinal.BalanceSats, bobFinal.BalanceSats)
            : (bobFinal.BalanceSats, aliceFinal.BalanceSats);
        Assert.Equal(start + wager, winnerBalance);
        Assert.Equal(start - wager, loserBalance);

        // Settled matches can't be re-fought or re-accepted.
        var again = await alice.PostAsJsonAsync($"/api/matches/{open.MatchId}/fight",
            new FightRequest("again"));
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
    }

    [Fact]
    public async Task WagerAgainstOwnHeroIsRejected()
    {
        var (alice, _, aliceHeroes) = await PlayerWithStartersAsync("W-Self");
        var response = await alice.PostAsJsonAsync("/api/matches/open",
            new OpenMatchRequest(aliceHeroes[0].Id, aliceHeroes[1].Id, 1_000));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
