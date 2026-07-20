namespace ArkadeHeroes.Core.Progression;

/// <summary>One competitive season window: its 1-based number and its [Start, End) instant range.</summary>
public readonly record struct SeasonInfo(int Number, DateTimeOffset Start, DateTimeOffset End);

/// <summary>
/// Time-derived competitive seasons: season N is a fixed-length window measured from <see cref="Epoch"/>,
/// so the current season is a pure function of the clock — no manual reset, deterministic, verifiable. The
/// ranked ladder tallies staked-match wins within the current window (server: <c>GameService.SeasonLeaderboard</c>).
/// </summary>
public static class Season
{
    /// <summary>Season 1 starts here (UTC). Fixed, so season numbering is stable across the deployment.</summary>
    public static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The season containing <paramref name="now"/> for a given season length in days (clamped ≥ 1). Season
    /// numbers are 1-based; anything at or before the epoch is season 1. The window is half-open [Start, End).
    /// </summary>
    public static SeasonInfo Current(DateTimeOffset now, int lengthDays)
    {
        var len = Math.Max(1, lengthDays);
        var index = (int)Math.Floor((now - Epoch).TotalDays / len);
        if (index < 0) index = 0;   // before the epoch → season 1
        var start = Epoch.AddDays(index * (double)len);
        return new SeasonInfo(index + 1, start, start.AddDays(len));
    }

    /// <summary>The window for a specific 1-based season <paramref name="number"/>, independent of the
    /// clock — so a past season can be settled. Mirrors <see cref="Current"/>'s arithmetic.</summary>
    public static SeasonInfo ForNumber(int number, int lengthDays)
    {
        var len = Math.Max(1, lengthDays);
        var n = Math.Max(1, number);
        var start = Epoch.AddDays((n - 1) * (double)len);
        return new SeasonInfo(n, start, start.AddDays(len));
    }
}
