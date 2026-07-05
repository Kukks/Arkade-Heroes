namespace ArkadeHeroes.Core.Progression;

/// <summary>XP curve, the conserved match transfer, and the per-character match fee.</summary>
public static class Leveling
{
    public const int MaxLevel = 50;

    /// <summary>XP required to advance from <paramref name="level"/> to the next.</summary>
    public static long XpToNext(int level)
    {
        if (level < 1) throw new ArgumentOutOfRangeException(nameof(level));
        return 80 + (long)(45 * Math.Pow(level, 1.35));
    }

    // ── The conserved match transfer ───────────────────────────────────
    // A staked win MOVES XP from the loser to the winner: the winner gains
    // exactly what the loser loses, so self-play — across any number of wallets —
    // can only CONCENTRATE XP between your heroes, never MINT it (Sybil-proof).
    // The amount scales with the level DIFFERENCE, not the absolute level: beating
    // a much-weaker hero transfers 0 (no farming down the ladder), a peer transfers
    // the base, an upset over a higher hero transfers a lot (and strips it off them).

    public const long BaseTransfer = 40;
    public const long TransferPerLevel = 12;

    /// <summary>XP the winner gains and the loser loses, from the level gap (clamped at 0).</summary>
    public static long XpTransfer(int winnerLevel, int loserLevel)
        => Math.Max(0, BaseTransfer + TransferPerLevel * (loserLevel - winnerLevel));

    // ── The per-character match fee (a level-proportional sats sink) ────
    // Each fighter pays to enter a staked match, proportional to its OWN level —
    // fielding a high-level hero always costs you, attacker or defender.

    public const long MatchFeePerLevel = 20;

    /// <summary>Sats a hero's owner pays to stage a staked match, proportional to the hero's level.</summary>
    public static long MatchFee(int level) => MatchFeePerLevel * Math.Max(1, level);

    /// <summary>
    /// Applies a SIGNED XP delta. Positive levels up across thresholds; NEGATIVE
    /// can delevel — it refunds into the level below and floors at level 1 / 0 XP,
    /// so a lost staked fight can knock a champion down. Returns the resulting
    /// level, remaining XP toward the next, and the signed level change.
    /// </summary>
    public static (int Level, long Xp, int LevelsChanged) Apply(int level, long xp, long delta)
    {
        if (level < 1) throw new ArgumentOutOfRangeException(nameof(level));
        var startLevel = level;
        xp += delta;
        if (delta >= 0)
        {
            while (level < MaxLevel && xp >= XpToNext(level))
            {
                xp -= XpToNext(level);
                level++;
            }
            if (level >= MaxLevel) xp = 0;
        }
        else
        {
            while (xp < 0 && level > 1)
            {
                level--;
                xp += XpToNext(level);
            }
            if (xp < 0) xp = 0;
        }
        return (level, xp, level - startLevel);
    }
}
