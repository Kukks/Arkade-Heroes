namespace ArkadeHeroes.Core.Progression;

/// <summary>XP curve and award rules.</summary>
public static class Leveling
{
    public const int MaxLevel = 50;

    /// <summary>XP required to advance from <paramref name="level"/> to the next.</summary>
    public static long XpToNext(int level)
    {
        if (level < 1) throw new ArgumentOutOfRangeException(nameof(level));
        return 80 + (long)(45 * Math.Pow(level, 1.35));
    }

    /// <summary>Applies an XP award, returning the resulting level and remaining XP toward the next.</summary>
    public static (int Level, long Xp, int LevelsGained) Apply(int level, long xp, long award)
    {
        if (award < 0) throw new ArgumentOutOfRangeException(nameof(award));
        var gained = 0;
        xp += award;
        while (level < MaxLevel && xp >= XpToNext(level))
        {
            xp -= XpToNext(level);
            level++;
            gained++;
        }
        if (level >= MaxLevel) xp = 0;
        return (level, xp, gained);
    }

    public static long WinnerAward(int loserLevel) => 60 + 12L * loserLevel;
    public static long LoserAward(int winnerLevel) => 20 + 4L * winnerLevel;
}
