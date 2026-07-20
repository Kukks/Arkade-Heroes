namespace ArkadeHeroes.Core.Progression;

/// <summary>A single UTC day window on the daily-engagement clock: half-open [Start, End).</summary>
public readonly record struct DailyWindow(int DayIndex, DateTimeOffset Start, DateTimeOffset End);

/// <summary>Day boundaries for the daily loop — a pure function of the clock off the shared epoch,
/// mirroring <see cref="Season"/>. UTC midnight resets; pre-epoch clamps to day 0.</summary>
public static class Daily
{
    public static readonly DateTimeOffset Epoch = Season.Epoch;   // 2026-01-01 UTC

    public static int DayIndex(DateTimeOffset now)
    {
        var i = (int)Math.Floor((now - Epoch).TotalDays);
        return i < 0 ? 0 : i;
    }

    public static DailyWindow ForDay(DateTimeOffset now)
    {
        var i = DayIndex(now);
        var start = Epoch.AddDays(i);
        return new DailyWindow(i, start, start.AddDays(1));
    }
}
