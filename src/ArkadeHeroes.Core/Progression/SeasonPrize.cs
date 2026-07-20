namespace ArkadeHeroes.Core.Progression;

/// <summary>Pot math for the season prize pool — pure, no clock, no state. The pot is split among the
/// top finishers by fixed weights; which seasons are due to settle is a function of two numbers.</summary>
public static class SeasonPrize
{
    /// <summary>Top-3 split weights (%). A Core constant, NOT a GameConfig positional param — a record
    /// positional default must be a compile-time constant and a collection can't be one.</summary>
    public static readonly IReadOnlyList<int> Weights = [60, 30, 10];

    /// <summary>Split <paramref name="pot"/> among the top <paramref name="winnerCount"/> finishers by
    /// <paramref name="weightPcts"/> (floored per share; the remainder/unclaimed weight stays behind).</summary>
    public static IReadOnlyList<long> Split(long pot, int winnerCount, IReadOnlyList<int> weightPcts)
    {
        var take = Math.Min(Math.Max(0, winnerCount), weightPcts.Count);
        return Enumerable.Range(0, take).Select(i => pot * weightPcts[i] / 100).ToList();
    }

    /// <summary>The ended-but-unsettled season numbers: [lastSettled+1 .. currentSeason-1].</summary>
    public static IEnumerable<int> DueSeasons(int lastSettled, int currentSeason)
    {
        for (var s = Math.Max(1, lastSettled + 1); s <= currentSeason - 1; s++) yield return s;
    }
}
