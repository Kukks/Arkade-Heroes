using System.Net.Http.Json;
using ArkadeHeroes.Core.Progression;
using ArkadeHeroes.Server;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ArkadeHeroes.Tests;

/// <summary>
/// XP-weighted matchmaking: suggested opponents are OTHER players' heroes ranked by
/// how evenly matched they are (closest level first), each annotated with the
/// conserved XP a staked win would gain and a loss would cost.
/// </summary>
public class MatchmakingTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public void XpAnnotationsMirrorTheConservedTransfer()
    {
        Assert.Equal(Leveling.XpTransfer(5, 3), Matchmaking.XpIfWin(5, 3));
        Assert.Equal(Leveling.XpTransfer(3, 5), Matchmaking.XpIfLose(5, 3));
        Assert.Equal(2, Matchmaking.LevelGap(5, 3));
        // Beating a far-weaker hero wins nothing; losing to a far-stronger one costs nothing.
        Assert.Equal(0, Matchmaking.XpIfWin(20, 1));
        Assert.Equal(0, Matchmaking.XpIfLose(1, 20));
    }

    [Fact]
    public async Task Suggestions_RankByLevelProximity_ExcludeOwnHeroes_AnnotateXp()
    {
        var (alice, _) = await _factory.RegisterAsync("MM-Alice");
        var (bob, _) = await _factory.RegisterAsync("MM-Bob");
        var aliceHeroes = await alice.ClaimStartersAsync();
        var bobHeroes = await bob.ClaimStartersAsync();

        // Vary levels directly on the shared in-memory store.
        var store = _factory.Services.GetRequiredService<GameStore>();
        store.Heroes[aliceHeroes[0].Id].Level = 5;
        store.Heroes[bobHeroes[0].Id].Level = 5;   // peer — gap 0
        store.Heroes[bobHeroes[1].Id].Level = 12;  // far — gap 7

        var suggestions = (await alice.GetFromJsonAsync<List<OpponentSuggestionDto>>(
            $"/api/matchmaking/{aliceHeroes[0].Id}"))!;

        // Alice's own other hero is never a suggested opponent.
        Assert.DoesNotContain(suggestions, s => s.Hero.Id == aliceHeroes[1].Id);

        var peer = suggestions.First(s => s.Hero.Id == bobHeroes[0].Id);
        var far = suggestions.First(s => s.Hero.Id == bobHeroes[1].Id);

        // Closest level first: the peer (gap 0) outranks the far hero (gap 7).
        Assert.Equal(0, peer.LevelGap);
        Assert.Equal(7, far.LevelGap);
        Assert.True(suggestions.IndexOf(peer) < suggestions.IndexOf(far), "peer should outrank the far hero");

        // XP swings are the conserved transfer for Alice's level-5 hero.
        Assert.Equal(Leveling.XpTransfer(5, 5), peer.XpIfYouWin);   // peer win: the base
        Assert.Equal(Leveling.XpTransfer(5, 12), far.XpIfYouWin);   // upset win: a lot
        Assert.Equal(0, far.XpIfYouLose);                           // losing to the far hero costs nothing
    }
}
