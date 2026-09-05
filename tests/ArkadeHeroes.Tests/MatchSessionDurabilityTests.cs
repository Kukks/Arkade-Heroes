using ArkadeHeroes.Server;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ArkadeHeroes.Tests;

/// <summary>
/// A staked duel or squad match holds its wager in PER-PARTY covenant escrows, and
/// <c>ListReclaimableAsync</c> can only name them by walking <c>store.Matches</c> / <c>store.SquadMatches</c>.
/// Nothing persisted either, so a restart left both players with sats escrowed at an address neither could
/// point at — the last two money-holding sessions with that defect.
/// </summary>
public class MatchSessionDurabilityTests
{
    private static WebApplicationFactory<Program> HostOn(string dbPath) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("Game:StateDbPath", dbPath));

    private static void Cleanup(string dbPath)
    {
        SqliteTestDb.ReleasePool(dbPath);
        try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch (IOException) { }
    }

    // Two gen-0 starters plus one dev-minted hero — a squad lineup is exactly three.
    private static async Task<List<string>> Lineup(ArkadeHeroes.Client.Sdk.ArkadeHeroesClient c)
    {
        var ids = (await c.ClaimStartersAsync()).Select(h => h.Id).ToList();
        ids.Add((await c.Dev.MintHeroAsync()).Id);
        return ids.Take(3).ToList();
    }

    [Fact]
    public async Task AStakedDuelsEscrowAddresses_SurviveARestart()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"arkade-match-{Guid.NewGuid():N}.db");
        try
        {
            string matchId, challengerEscrow;
            using (var first = HostOn(dbPath))
            {
                var (alice, _) = await first.RegisterAsync("M-Challenger");
                var (bob, _) = await first.RegisterAsync("M-Defender");
                var aliceHeroes = await alice.ClaimStartersAsync();
                var bobHeroes = await bob.ClaimStartersAsync();

                var open = await alice.Matches.OpenAsync(
                    new OpenMatchRequest(aliceHeroes[0].Id, bobHeroes[0].Id, 2_000));
                matchId = open.MatchId;
                challengerEscrow = open.EscrowAddress!;
                Assert.NotNull(challengerEscrow);
            }

            using var restarted = HostOn(dbPath);
            _ = restarted.CreateClient();
            var store = restarted.Services.GetRequiredService<GameStore>();

            Assert.True(store.Matches.TryGetValue(matchId, out var session),
                "without this row /reclaim cannot name the escrow holding the challenger's stake");
            Assert.Equal(challengerEscrow, session!.EscrowChallengerAddress);
            Assert.Equal(2_000, session.WagerSats);
            Assert.Equal("covenant", session.Mode);
            Assert.Equal("open", session.Status);
        }
        finally { Cleanup(dbPath); }
    }

    [Fact]
    public async Task AnAcceptedDuel_ComesBackAcceptedWithBothSidesEscrowed()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"arkade-match-{Guid.NewGuid():N}.db");
        try
        {
            string matchId, defenderEscrow;
            using (var first = HostOn(dbPath))
            {
                var (alice, _) = await first.RegisterAsync("M-Challenger2");
                var (bob, bobDto) = await first.RegisterAsync("M-Defender2");
                var aliceHeroes = await alice.ClaimStartersAsync();
                var bobHeroes = await bob.ClaimStartersAsync();

                var open = await alice.Matches.OpenAsync(
                    new OpenMatchRequest(aliceHeroes[0].Id, bobHeroes[0].Id, 2_000));
                matchId = open.MatchId;
                await alice.Dev.StakeEscrowAsync(new { MatchId = matchId });
                var accept = await bob.Matches.AcceptAsync(matchId);
                defenderEscrow = accept.EscrowAddress!;

                var live = first.Services.GetRequiredService<GameStore>().Matches[matchId];
                Assert.Equal(bobDto.PlayerId, live.DefenderPlayerId);
            }

            using var restarted = HostOn(dbPath);
            _ = restarted.CreateClient();
            var store = restarted.Services.GetRequiredService<GameStore>();

            Assert.True(store.Matches.TryGetValue(matchId, out var session));
            Assert.Equal("accepted", session!.Status);
            Assert.NotNull(session.DefenderPlayerId);
            Assert.Equal(defenderEscrow, session.EscrowDefenderAddress);
        }
        finally { Cleanup(dbPath); }
    }

    [Fact]
    public async Task AStakedSquadMatch_KeepsItsLineupsAndEscrows()
    {
        // The lineups are what tell a returning player WHICH heroes are committed, and a duel is stored as a
        // squad of one — so an order or arity slip here would surface first on the multi-hero side.
        var dbPath = Path.Combine(Path.GetTempPath(), $"arkade-match-{Guid.NewGuid():N}.db");
        try
        {
            string squadId;
            IReadOnlyList<string> lineup;
            using (var first = HostOn(dbPath))
            {
                var (alice, _) = await first.RegisterAsync("S-Challenger");
                var (bob, _) = await first.RegisterAsync("S-Defender");
                var mine = await Lineup(alice);
                var theirs = await Lineup(bob);

                var open = await alice.Squad.OpenAsync(new OpenSquadMatchRequest(mine, theirs, 1_500));
                squadId = open.MatchId;
                lineup = mine;
            }

            using var restarted = HostOn(dbPath);
            _ = restarted.CreateClient();
            var store = restarted.Services.GetRequiredService<GameStore>();

            Assert.True(store.SquadMatches.TryGetValue(squadId, out var session));
            Assert.Equal(lineup, session!.ChallengerLineup);
            Assert.Equal(3, session.DefenderLineup.Count);
            Assert.Equal(1_500, session.WagerSats);
        }
        finally { Cleanup(dbPath); }
    }

    [Fact]
    public async Task AStakedSquadsEscrow_IsListedAsReclaimable()
    {
        // Persisting a squad session buys nothing unless something READS it: ListReclaimableAsync walked
        // only store.Matches. This is the half that makes the durability worth having.
        using var factory = new WebApplicationFactory<Program>();
        var (alice, _) = await factory.RegisterAsync("S-Reclaim-A");
        var (bob, _) = await factory.RegisterAsync("S-Reclaim-B");
        var mine = await Lineup(alice);
        var theirs = await Lineup(bob);

        var open = await alice.Squad.OpenAsync(new OpenSquadMatchRequest(mine, theirs, 1_500));
        await alice.Dev.StakeEscrowAsync(new { MatchId = open.MatchId });

        var reclaimable = await alice.Players.ReclaimableAsync();
        Assert.Contains(reclaimable, r => r.Kind == "wager" && r.Id == open.MatchId);

        Assert.DoesNotContain(await bob.Players.ReclaimableAsync(), r => r.Id == open.MatchId);
    }

    [Fact]
    public async Task AResolvedFriendlyDuel_IsNotRehydrated()
    {
        // A resolved match has settled escrows, so a returning row would offer a reclaim against nothing.
        var dbPath = Path.Combine(Path.GetTempPath(), $"arkade-match-{Guid.NewGuid():N}.db");
        try
        {
            using (var first = HostOn(dbPath))
            {
                var (alice, _) = await first.RegisterAsync("M-Friendly");
                var (bob, _) = await first.RegisterAsync("M-FriendlyFoe");
                var aliceHeroes = await alice.ClaimStartersAsync();
                var bobHeroes = await bob.ClaimStartersAsync();

                var open = await alice.Matches.OpenAsync(
                    new OpenMatchRequest(aliceHeroes[0].Id, bobHeroes[0].Id, 0));
                await alice.Matches.FightAsync(open.MatchId, new FightRequest("match-nonce"));
            }

            using var restarted = HostOn(dbPath);
            _ = restarted.CreateClient();
            Assert.Empty(restarted.Services.GetRequiredService<GameStore>().Matches);
        }
        finally { Cleanup(dbPath); }
    }
}
