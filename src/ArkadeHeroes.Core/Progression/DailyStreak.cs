namespace ArkadeHeroes.Core.Progression;

/// <summary>Streak transition, isolated as a pure function so multi-day progression is unit-testable
/// without an injectable clock (the server can only exercise same-day behaviour).</summary>
public static class DailyStreak
{
    /// <summary>Advance the streak for a claim on <paramref name="todayIndex"/>: consecutive day → +1,
    /// a gap or first-ever claim → reset to 1, same day → unchanged (defensive; the caller rejects
    /// a same-day re-claim earlier).</summary>
    public static int Next(int? lastClaimDay, int todayIndex, int currentStreak) =>
        lastClaimDay == todayIndex       ? currentStreak
        : lastClaimDay == todayIndex - 1 ? currentStreak + 1
        :                                  1;
}
