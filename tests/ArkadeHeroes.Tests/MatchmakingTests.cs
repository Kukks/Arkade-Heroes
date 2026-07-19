using ArkadeHeroes.Client.Sdk;
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

        var suggestions = await alice.Matches.MatchmakingAsync(aliceHeroes[0].Id);

        // Alice's own other hero is never a suggested opponent.
        Assert.DoesNotContain(suggestions, s => s.Hero.Id == aliceHeroes[1].Id);

        var peer = suggestions.First(s => s.Hero.Id == bobHeroes[0].Id);
        var far = suggestions.First(s => s.Hero.Id == bobHeroes[1].Id);

        // LevelGap annotations stay level-based (peer gap 0, far gap 7). Ordering is now by realized
        // POWER (F18) — not a level-vs-power coincidence, so it's asserted in
        // Suggestions_CarryPowerScore_OrderedByPowerGap, not here (random starter genomes make a
        // level-order assertion flaky against the power ordering).
        Assert.Equal(0, peer.LevelGap);
        Assert.Equal(7, far.LevelGap);

        // XP swings are the conserved transfer for Alice's level-5 hero.
        Assert.Equal(Leveling.XpTransfer(5, 5), peer.XpIfYouWin);   // peer win: the base
        Assert.Equal(Leveling.XpTransfer(5, 12), far.XpIfYouWin);   // upset win: a lot
        Assert.Equal(0, far.XpIfYouLose);                           // losing to the far hero costs nothing

        // F2: the coarse favor label rides along the suggestion. The peer is "even"; the far hero is
        // an "underdog" shot — and since XpIfYouLose == 0 above, it's the "free shot" the UI badges.
        Assert.Equal("even", peer.Favor);
        Assert.Equal("underdog", far.Favor);
    }

    [Theory]
    [InlineData(10, 10, "even")]
    [InlineData(10, 12, "even")]    // within 2
    [InlineData(10, 8, "even")]
    [InlineData(10, 4, "favored")]  // 6 up
    [InlineData(4, 10, "underdog")] // 6 down
    [InlineData(10, 13, "underdog")] // 3 down
    [InlineData(13, 10, "favored")]  // 3 up
    public void Favor_LabelsByLevelGap(int mine, int theirs, string expected)
        => Assert.Equal(expected, Matchmaking.Favor(mine, theirs));

    [Fact]
    public async Task Suggestions_CarryPowerScore_OrderedByPowerGap()
    {
        var (alice, _) = await _factory.RegisterAsync("MM-Power-A");
        var (bob, _) = await _factory.RegisterAsync("MM-Power-B");
        var mine = (await alice.ClaimStartersAsync())[0];
        await bob.ClaimStartersAsync();
        var store = _factory.Services.GetRequiredService<GameStore>();
        store.Heroes[mine.Id].Level = 6;

        var opps = await alice.Matches.MatchmakingAsync(mine.Id);

        Assert.NotEmpty(opps);
        Assert.All(opps, o => Assert.True(o.PowerScore > 0, "each suggestion carries a realized power score"));
        // F18's primary key: suggestions ordered by ascending realized-power gap.
        for (var i = 1; i < opps.Count; i++)
            Assert.True(opps[i].PowerGapPercent >= opps[i - 1].PowerGapPercent,
                $"ordered by power gap ({opps[i - 1].PowerGapPercent} then {opps[i].PowerGapPercent})");
    }
}
