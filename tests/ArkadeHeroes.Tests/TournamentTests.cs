using System.Linq;
using ArkadeHeroes.Core;
using ArkadeHeroes.Core.Combat;
using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Heroes;

namespace ArkadeHeroes.Tests;

/// <summary>
/// The single-elimination tournament resolver: a pure bracket over 1v1 duels, deterministic in
/// (entrants, seed) and reusing BattleEngine.Fight — so the server scores it and the client replays it,
/// exactly like SquadBattle / Gauntlet.
/// </summary>
public class TournamentTests
{
    static Hero H(string id, byte stat)
    {
        var b = new byte[32];
        for (var i = 0; i < 5; i++) b[i] = stat;   // stat genes → distinct power, so duels resolve clearly
        return new Hero { Id = id, OwnerId = "t", Name = id, Genome = new Genome(b), Level = 5 };
    }

    static byte[] Seed(byte s)
    {
        var x = new byte[32];
        for (var i = 0; i < 32; i++) x[i] = (byte)(s + i);
        return x;
    }

    [Fact]
    public void Resolve_PowerOfTwo_OneChampion_TwoRounds()
    {
        var field = new[] { H("a", 200), H("b", 150), H("c", 180), H("d", 120) };
        var r = Tournament.Resolve(field, Seed(1));
        Assert.Equal(2, r.Rounds);                                    // 4 → 2 → 1
        Assert.Contains(r.ChampionId, field.Select(h => h.Id));
        Assert.Equal(3, r.Matches.Count(m => m.Result is not null));  // 2 semis + 1 final, no byes
    }

    [Fact]
    public void Resolve_NonPowerOfTwo_HandlesByes()
    {
        var field = new[] { H("a", 200), H("b", 150), H("c", 180) };
        var r = Tournament.Resolve(field, Seed(2));
        Assert.Contains(r.ChampionId, field.Select(h => h.Id));
        Assert.Contains(r.Matches, m => m.Result is null);            // an odd entrant took a bye
    }

    [Fact]
    public void Resolve_IsDeterministic_SameSeedSameChampion()
    {
        var field = new[] { H("a", 200), H("b", 150), H("c", 180), H("d", 120) };
        var seed = Seed(7);
        Assert.Equal(Tournament.Resolve(field, seed).ChampionId, Tournament.Resolve(field, seed).ChampionId);
    }

    [Fact]
    public void Resolve_RejectsTooFewEntrants() =>
        Assert.Throws<ArgumentException>(() => Tournament.Resolve(new[] { H("solo", 100) }, Seed(1)));
}
