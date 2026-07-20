using ArkadeHeroes.Core.Combat;
using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Heroes;

namespace ArkadeHeroes.Tests;

/// <summary>The 3v3 squad resolver: a positional best-of-3 relay of deterministic 1v1 fights — pure,
/// reuses BattleEngine.Fight byte-for-byte (a copy of the Gauntlet's resolve pattern).</summary>
public class SquadBattleTests
{
    static Hero MakeHero(string id, int level, byte seed)
    {
        var g = new byte[32];
        Array.Fill(g, seed);
        return new Hero { Id = id, OwnerId = "t", Name = id, Genome = Genome.NewGen0(g), Level = level };
    }

    static (List<Hero> A, List<Hero> B) Lineups() => (
        new() { MakeHero("a0", 5, 1), MakeHero("a1", 5, 2), MakeHero("a2", 5, 3) },
        new() { MakeHero("b0", 5, 10), MakeHero("b1", 5, 20), MakeHero("b2", 5, 30) });

    static byte[] Seed(byte v) { var s = new byte[32]; Array.Fill(s, v); return s; }

    [Fact]
    public void Resolve_ThreeDuels_MajorityDecidesWinner()
    {
        var (a, b) = Lineups();
        var r = SquadBattle.Resolve(a, b, Seed(7));

        Assert.Equal(3, r.Duels.Count);
        Assert.Equal(new[] { 0, 1, 2 }, r.Duels.Select(d => d.Slot).ToArray());
        Assert.Equal(3, r.ChallengerWins + r.DefenderWins);       // odd → no ties possible
        Assert.Equal(r.ChallengerWins > r.DefenderWins, r.ChallengerWon);
    }

    [Fact]
    public void Resolve_IsDeterministic()
    {
        var (a, b) = Lineups();
        var r1 = SquadBattle.Resolve(a, b, Seed(7));
        var r2 = SquadBattle.Resolve(a, b, Seed(7));

        Assert.Equal(r1.ChallengerWon, r2.ChallengerWon);
        Assert.Equal(r1.Duels.Select(d => d.Result.WinnerId), r2.Duels.Select(d => d.Result.WinnerId));
    }

    [Fact]
    public void Resolve_EachDuelIsSlotVsSlot_WithIndependentSubSeeds()
    {
        var (a, b) = Lineups();
        var r = SquadBattle.Resolve(a, b, Seed(99));
        // Each duel is slot i of A vs slot i of B — the winner is always one of that slot's two heroes.
        Assert.All(r.Duels, d => Assert.Contains(d.Result.WinnerId, new[] { a[d.Slot].Id, b[d.Slot].Id }));
    }
}
