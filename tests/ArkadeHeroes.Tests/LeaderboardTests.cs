using System.Net.Http.Json;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ArkadeHeroes.Tests;

public class LeaderboardTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public LeaderboardTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public void Build_RanksWinnersFirst_ThenByLevel()
    {
        var heroes = new Dictionary<string, (string Name, int Level, string OwnerId)>
        {
            ["h1"] = ("Ada", 3, "p1"),
            ["h2"] = ("Bo", 5, "p2"),
            ["h3"] = ("Cy", 5, "p3"),
        };
        ProgressionReceiptDto Match(string id, string a, string b, string winner) =>
            new("match", id, a, b, winner, "", "", "", 0, 0, 0, 0, 0, "", "");
        var receipts = new[]
        {
            Match("m1", "h1", "h2", "h1"), // h1 beats h2
            Match("m2", "h1", "h3", "h1"), // h1 beats h3
            Match("m3", "h2", "h3", "h2"), // h2 beats h3
        };

        var board = LeaderboardBuilder.Build(heroes, receipts);
        Assert.Equal("h1", board[0].HeroId);   // 2 wins → rank 1
        Assert.Equal(2, board[0].Wins);
        Assert.Equal("h2", board[1].HeroId);   // 1 win → rank 2
        Assert.Equal("h3", board[2].HeroId);   // 0 wins, level 5 → last
        Assert.Equal(2, board[2].Matches);     // h3 played two matches
    }

    [Fact]
    public async Task LeaderboardEndpoint_ReflectsAResolvedMatch()
    {
        var (alice, _) = await _factory.RegisterAsync("LB-Alice");
        var (bob, _) = await _factory.RegisterAsync("LB-Bob");
        var aliceHeroes = await alice.ClaimStartersAsync();
        var bobHeroes = await bob.ClaimStartersAsync();

        var open = (await (await alice.PostAsJsonAsync("/api/matches/open",
                new OpenMatchRequest(aliceHeroes[0].Id, bobHeroes[0].Id, 2_000, "covenant")))
            .Content.ReadFromJsonAsync<OpenMatchResponse>())!;
        await alice.PostAsJsonAsync("/api/dev/stake-escrow", new { MatchId = open.MatchId });
        var accept = (await (await bob.PostAsync($"/api/matches/{open.MatchId}/accept", null))
            .Content.ReadFromJsonAsync<AcceptMatchResponse>())!;
        await bob.PostAsJsonAsync("/api/dev/stake-escrow", new { MatchId = open.MatchId });
        await alice.PayInvoiceAsync(open.MatchFeeInvoice!.InvoiceId);
        await bob.PayInvoiceAsync(accept.MatchFeeInvoice!.InvoiceId);
        var fight = (await (await alice.PostAsJsonAsync($"/api/matches/{open.MatchId}/fight",
                new FightRequest("lb-duel"))).Content.ReadFromJsonAsync<FightResponse>())!;

        var board = (await alice.GetFromJsonAsync<List<LeaderboardEntryDto>>("/api/leaderboard"))!;
        // The two dueling heroes appear, and the winner sits above the loser.
        var winnerRank = board.First(e => e.HeroId == fight.Result.WinnerId).Rank;
        var loserId = fight.Result.WinnerId == aliceHeroes[0].Id ? bobHeroes[0].Id : aliceHeroes[0].Id;
        var loserRank = board.First(e => e.HeroId == loserId).Rank;
        Assert.True(winnerRank < loserRank, "winner should outrank the loser");
        Assert.Equal(1, board.First(e => e.HeroId == fight.Result.WinnerId).Wins);
    }
}
