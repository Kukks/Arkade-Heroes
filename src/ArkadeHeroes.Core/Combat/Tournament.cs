using ArkadeHeroes.Core.Fairness;
using ArkadeHeroes.Core.Heroes;

namespace ArkadeHeroes.Core.Combat;

/// <summary>One resolved bracket slot: a 1v1 duel, or a bye (<see cref="Result"/> null, <see cref="BId"/> empty).</summary>
public readonly record struct TournamentMatch(int Round, int Index, string AId, string BId, BattleResult? Result, string WinnerId);

/// <summary>A resolved single-elimination tournament: the champion, every match (for replay), and the round count.</summary>
public readonly record struct TournamentResult(string ChampionId, IReadOnlyList<TournamentMatch> Matches, int Rounds);

/// <summary>
/// A single-elimination tournament resolver: entrants are paired 1v1 each round (via <see cref="BattleEngine.Fight"/>
/// with a per-match sub-seed derived from the tournament seed), winners advance, until one champion remains. An
/// odd entrant in a round takes a bye to the next round. Pure + deterministic in (entrants, seed, config), reusing
/// the engine unchanged — the server scores with it and the client replays it identically (mirrors
/// <see cref="SquadBattle"/> and <see cref="ArkadeHeroes.Core.Progression.Gauntlet"/>). Entrant order IS the
/// bracket seeding.
/// </summary>
public static class Tournament
{
    public const int MinEntrants = 2;

    public static TournamentResult Resolve(IReadOnlyList<Hero> entrants, ReadOnlySpan<byte> tournamentSeed, GameConfig? config = null)
    {
        if (entrants.Count < MinEntrants)
            throw new ArgumentException($"A tournament needs at least {MinEntrants} entrants.", nameof(entrants));

        var cfg = config ?? GameConfig.Default;
        var seed = tournamentSeed.ToArray();
        var byId = entrants.ToDictionary(h => h.Id);   // advance winners/byes carry forward by id
        var matches = new List<TournamentMatch>();
        var alive = entrants.Select(h => h.Id).ToList();
        var round = 0;

        while (alive.Count > 1)
        {
            var next = new List<string>((alive.Count + 1) / 2);
            for (var i = 0; i < alive.Count; i += 2)
            {
                if (i + 1 >= alive.Count)   // odd entrant → bye into the next round
                {
                    matches.Add(new TournamentMatch(round, i / 2, alive[i], "", null, alive[i]));
                    next.Add(alive[i]);
                    continue;
                }
                var (aId, bId) = (alive[i], alive[i + 1]);
                var fightSeed = CommitReveal.DeriveEntropy(seed, "tourney-fight", $"{round}-{i / 2}");
                var result = BattleEngine.Fight(byId[aId], byId[bId], fightSeed, cfg);
                matches.Add(new TournamentMatch(round, i / 2, aId, bId, result, result.WinnerId));
                next.Add(result.WinnerId);
            }
            alive = next;
            round++;
        }
        return new TournamentResult(alive[0], matches, round);
    }

    /// <summary>Prize split (%) for the podium: champion, then runner-up. A Core constant (like SeasonPrize.Weights).</summary>
    public static readonly IReadOnlyList<int> PrizeWeights = [70, 30];

    /// <summary>The podium — champion first, then the runner-up (the loser of the final match); the basis for
    /// the prize split. A degenerate all-bye bracket returns just the champion.</summary>
    public static IReadOnlyList<string> Podium(TournamentResult result)
    {
        var final = result.Matches
            .Where(m => m.Result is not null)
            .OrderByDescending(m => m.Round).ThenByDescending(m => m.Index)
            .FirstOrDefault();
        if (final.Result is null) return [result.ChampionId];
        var runnerUp = final.AId == final.WinnerId ? final.BId : final.AId;
        return [result.ChampionId, runnerUp];
    }
}
