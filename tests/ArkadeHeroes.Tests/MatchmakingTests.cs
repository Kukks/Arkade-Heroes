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
        // Both heroes rich enough to settle the whole gap, so this pins the SHAPE of the transfer.
        const long solvent = 100_000;
        Assert.Equal(Leveling.XpTransfer(5, 3), Matchmaking.XpIfWin(5, 3, solvent));
        Assert.Equal(Leveling.XpTransfer(3, 5), Matchmaking.XpIfLose(5, 3, solvent));
        Assert.Equal(2, Matchmaking.LevelGap(5, 3));
        // Beating a far-weaker hero wins nothing; losing to a far-stronger one costs nothing.
        Assert.Equal(0, Matchmaking.XpIfWin(20, 1, solvent));
        Assert.Equal(0, Matchmaking.XpIfLose(1, 20, solvent));
    }

    /// <summary>
    /// The clamp, at the helper level: a peer fight has a real gap, and a peer who owns nothing still
    /// pays nothing. This is the pair <see cref="Leveling.PayableTransfer"/> exists to keep apart, and
    /// quoting the first where the second is what happens is what the duel card was doing.
    /// </summary>
    [Fact]
    public void XpAnnotationsAreClampedToWhatTheLoserOwns()
    {
        Assert.True(Leveling.XpTransfer(1, 1) > 0, "the raw peer gap must be non-zero, or this proves nothing");

        // A fresh hero has banked nothing, so beating it wins nothing and losing to it costs nothing.
        Assert.Equal(0, Matchmaking.XpIfWin(1, 1, opponentXp: 0));
        Assert.Equal(0, Matchmaking.XpIfLose(1, 1, heroXp: 0));

        // A loser part-way to the gap hands over exactly what it has — no more, and none minted.
        var gap = Leveling.XpTransfer(1, 1);
        Assert.Equal(gap - 1, Matchmaking.XpIfWin(1, 1, opponentXp: gap - 1));
        Assert.Equal(gap, Matchmaking.XpIfWin(1, 1, opponentXp: gap + 5));
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

        // XP swings are the conserved transfer for Alice's level-5 hero, CLAMPED to what the loser owns —
        // these heroes were levelled straight on the store, so they carry the banked XP of the level.
        Assert.Equal(Leveling.PayableTransfer(5, 5, peer.Hero.Xp), peer.XpIfYouWin);   // peer win: the base
        Assert.Equal(Leveling.PayableTransfer(5, 12, far.Hero.Xp), far.XpIfYouWin);    // upset win: a lot
        Assert.Equal(0, far.XpIfYouLose);                                              // losing to the far hero costs nothing
        // The clamp must not have flattened the peer/upset distinction this test is about.
        Assert.True(peer.XpIfYouWin > 0 && far.XpIfYouWin > peer.XpIfYouWin,
            "an upset must still pay more than a peer fight, or the ordering these annotations exist for is gone");

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

    /// <summary>
    /// The pre-stake pitch must quote a number the settle can actually PAY.
    ///
    /// <para>XP is a conserved transfer, and <see cref="Leveling.PayableTransfer"/> is the clamp that keeps
    /// it conserved: a loser hands over its whole balance and no more, because crediting the winner the
    /// unclamped figure would MINT XP. The suggestions annotated themselves with the raw
    /// <see cref="Leveling.XpTransfer"/> gap instead, so the duel card advertised "win +40 / lose −40" over
    /// a fight the rules then settled at "0 xp · 0 xp" — the 0 being the correct one.</para>
    ///
    /// <para>That is not a rounding difference on a cosmetic label. It is the pitch a player reads before
    /// staking real sats, and it is wrong in exactly the region every new player starts in: a fresh hero
    /// owns no XP, so it can pay nothing, so every fight against one is worth nothing.</para>
    /// </summary>
    [Fact]
    public async Task Suggestions_QuoteAnXpSwingTheSettleCanActuallyPay()
    {
        var (alice, _) = await _factory.RegisterAsync("MM-Payable-A");
        var (bob, _) = await _factory.RegisterAsync("MM-Payable-B");
        var mine = (await alice.ClaimStartersAsync())[0];
        await bob.ClaimStartersAsync();

        var opps = await alice.Matches.MatchmakingAsync(mine.Id);
        Assert.NotEmpty(opps);

        // Non-vacuity, stated in the test: at least one suggestion must name a hero too poor to pay the
        // raw gap, or every assertion below would hold against the unclamped transfer too.
        Assert.Contains(opps, o => Leveling.XpTransfer(mine.Level, o.Hero.Level)
                                 > Leveling.PayableTransfer(mine.Level, o.Hero.Level, o.Hero.Xp));

        foreach (var o in opps)
        {
            Assert.Equal(Leveling.PayableTransfer(mine.Level, o.Hero.Level, o.Hero.Xp), o.XpIfYouWin);
            Assert.Equal(Leveling.PayableTransfer(o.Hero.Level, mine.Level, mine.Xp), o.XpIfYouLose);
        }
    }

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
