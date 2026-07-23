using ArkadeHeroes.Client.Sdk;
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

        var open = await alice.Matches.OpenAsync(
            new OpenMatchRequest(aliceHeroes[0].Id, bobHeroes[0].Id, 2_000, "covenant"));
        await alice.Dev.StakeEscrowAsync(new { MatchId = open.MatchId });
        var accept = await bob.Matches.AcceptAsync(open.MatchId);
        await bob.Dev.StakeEscrowAsync(new { MatchId = open.MatchId });
        await alice.PayInvoiceAsync(open.MatchFeeInvoice!.InvoiceId);
        await bob.PayInvoiceAsync(accept.MatchFeeInvoice!.InvoiceId);
        var fight = await alice.Matches.FightAsync(open.MatchId, new FightRequest("lb-duel"));

        var board = await alice.Leaderboard.TopAsync();
        // The two dueling heroes appear, and the winner sits above the loser.
        var winnerRank = board.First(e => e.HeroId == fight.Result.WinnerId).Rank;
        var loserId = fight.Result.WinnerId == aliceHeroes[0].Id ? bobHeroes[0].Id : aliceHeroes[0].Id;
        var loserRank = board.First(e => e.HeroId == loserId).Rank;
        Assert.True(winnerRank < loserRank, "winner should outrank the loser");
        Assert.Equal(1, board.First(e => e.HeroId == fight.Result.WinnerId).Wins);
    }

    [Fact]
    public void Leaderboard_TiedHeroes_RankIndependentOfRosterOrder()
    {
        // Two heroes tied on EVERY sort key including name (auto-names collide across the 256-name space).
        // The board promises "anyone recomputes the same ranking", so a tie must break on the unique hero id —
        // not on the roster's (ConcurrentDictionary, unstable) enumeration order.
        var twin = (Name: "Crimson Vanguard", Level: 1, OwnerId: "p");
        var forward = new Dictionary<string, (string Name, int Level, string OwnerId)> { ["z-hero"] = twin, ["a-hero"] = twin };
        var backward = new Dictionary<string, (string Name, int Level, string OwnerId)> { ["a-hero"] = twin, ["z-hero"] = twin };

        var b1 = LeaderboardBuilder.Build(forward, Array.Empty<ProgressionReceiptDto>());
        var b2 = LeaderboardBuilder.Build(backward, Array.Empty<ProgressionReceiptDto>());
        Assert.Equal(b1.Select(e => e.HeroId), b2.Select(e => e.HeroId));   // same facts ⇒ same ranking
    }

    [Fact]
    public void TrialsBoard_TiedHeroes_RankIndependentOfReceiptOrder()
    {
        // Same trustless-recompute invariant for the Trials ladder: two heroes tied on name/level/best score
        // must rank identically no matter what order their signed receipts arrive in.
        var heroes = new Dictionary<string, (string Name, int Level)> { ["z-hero"] = ("Twin", 1), ["a-hero"] = ("Twin", 1) };
        ProgressionReceiptDto Trial(string heroId) => new("trials", $"t-{heroId}", heroId, "", heroId, "", "", "", 0, 5, 0, 0, 0, "", "");

        var b1 = TrialsBoardBuilder.Build(heroes, new[] { Trial("z-hero"), Trial("a-hero") });
        var b2 = TrialsBoardBuilder.Build(heroes, new[] { Trial("a-hero"), Trial("z-hero") });
        Assert.Equal(b1.Select(e => e.HeroId), b2.Select(e => e.HeroId));
    }
}
