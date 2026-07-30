namespace ArkadeHeroes.Core.Progression;

/// <summary>XP curve, the conserved match transfer, and the per-character match fee.</summary>
public static class Leveling
{
    public const int MaxLevel = 50;

    /// <summary>XP required to advance from <paramref name="level"/> to the next.</summary>
    public static long XpToNext(int level, GameConfig? config = null)
    {
        if (level < 1) throw new ArgumentOutOfRangeException(nameof(level));
        var c = (config ?? GameConfig.Default).Curve;
        return c.Base + (long)(c.Coefficient * Math.Pow(level, c.Exponent));
    }

    // ── The conserved match transfer ───────────────────────────────────
    // A staked win MOVES XP from the loser to the winner: the winner gains
    // exactly what the loser loses, so self-play — across any number of wallets —
    // can only CONCENTRATE XP between your heroes, never MINT it (Sybil-proof).
    // That holds only when a settle uses PayableTransfer below; XpTransfer alone is
    // the SIZE OF THE GAP, and a loser too poor to pay it cannot make up the balance.
    // The amount scales with the level DIFFERENCE, not the absolute level: beating
    // a much-weaker hero transfers 0 (no farming down the ladder), a peer transfers
    // the base, an upset over a higher hero transfers a lot (and strips it off them).

    public const long BaseTransfer = 40;
    public const long TransferPerLevel = 12;

    /// <summary>XP the winner gains and the loser loses, from the level gap (clamped at 0).</summary>
    public static long XpTransfer(int winnerLevel, int loserLevel)
        => Math.Max(0, BaseTransfer + TransferPerLevel * (loserLevel - winnerLevel));

    /// <summary>Total XP a hero has banked: everything spent climbing to its level, plus progress toward the next.
    /// This is exactly what <see cref="Apply"/> can take off it before it bottoms out at level 1 with 0 XP.</summary>
    public static long TotalXp(int level, long xp, GameConfig? config = null)
    {
        if (level < 1) throw new ArgumentOutOfRangeException(nameof(level));
        var total = xp;
        for (var l = 1; l < level; l++) total += XpToNext(l, config);
        return total;
    }

    /// <summary>
    /// The transfer a staked win actually MOVES — <see cref="XpTransfer"/> clamped to what the loser owns.
    ///
    /// Settle with this, never with the raw amount. <see cref="Apply"/> floors a losing hero at level 1 / 0 XP,
    /// so a loser that owns less than the gap says still hands over its whole balance and no more; awarding the
    /// winner the unclamped figure would credit XP the loser never paid, i.e. MINT it. That is not hypothetical
    /// at the bottom of the ladder: a free starter sits at level 1 with 0 XP, pays nothing when it loses, and
    /// does not deplete — so it can be beaten over and over, each win conjuring the full base transfer. That
    /// would break the Sybil-proofness the conserved transfer exists to provide.
    ///
    /// The ceiling is deliberately NOT clamped here: a hero at <see cref="XpCurve.MaxLevel"/> keeps no surplus,
    /// so beating one still costs the loser its stake and that XP leaves the game. Draining is a sink, and a
    /// sink cannot make the books insolvent the way a mint can.
    /// </summary>
    public static long PayableTransfer(int winnerLevel, int loserLevel, long loserXp, GameConfig? config = null)
        => Math.Min(XpTransfer(winnerLevel, loserLevel), TotalXp(loserLevel, loserXp, config));

    // ── The per-character match fee (a level-proportional sats sink) ────
    // Each fighter pays to enter a staked match, proportional to its OWN level —
    // fielding a high-level hero always costs you, attacker or defender.

    // A fixed base makes the fee clear Ark's on-chain dust limit (330 sats) even
    // for a level-1 hero — the fee is paid as a real VTXO, so a sub-dust amount
    // simply can't land. The per-level term is the "training surcharge" that makes
    // fielding a higher-level hero cost progressively more.
    public const long MatchFeeBaseSats = 500;
    public const long MatchFeePerLevel = 20;

    /// <summary>Sats a hero's owner pays to stage a staked match: a dust-clearing base plus a per-level surcharge.</summary>
    public static long MatchFee(int level, GameConfig? config = null)
    {
        var c = config ?? GameConfig.Default;
        return c.MatchFeeBaseSats + c.MatchFeePerLevel * Math.Max(1, level);
    }

    // ── The death-match fee (a MULTIPLE of the per-character match fee) ──
    // Permadeath is the highest-stakes match, so entering costs more than a wager
    // match; absorb mode costs more still (extra escrow leaves + a possible re-mint,
    // and the extra EV the winner buys). NO rarity term: the staked hero IS the risk
    // — a Legendary already wagers its full market value — so the fee prices spam and
    // a consistent house take, not the sink (which is the permakill itself).
    public const int DeathMatchFeeMultiplier = 2;
    public const int AbsorbFeeMultiplier = 3;

    /// <summary>Sats a hero's owner pays to stage a death-match: a multiple of its <see cref="MatchFee"/> (higher for absorb).</summary>
    public static long DeathMatchFee(int level, bool absorb, GameConfig? config = null)
    {
        var c = config ?? GameConfig.Default;
        return (absorb ? c.AbsorbFeeMultiplier : c.DeathMatchFeeMultiplier) * MatchFee(level, config);
    }

    /// <summary>
    /// Applies a SIGNED XP delta. Positive levels up across thresholds; NEGATIVE
    /// can delevel — it refunds into the level below and floors at level 1 / 0 XP,
    /// so a lost staked fight can knock a champion down. Returns the resulting
    /// level, remaining XP toward the next, and the signed level change.
    /// </summary>
    public static (int Level, long Xp, int LevelsChanged) Apply(int level, long xp, long delta, GameConfig? config = null)
    {
        if (level < 1) throw new ArgumentOutOfRangeException(nameof(level));
        var maxLevel = (config ?? GameConfig.Default).Curve.MaxLevel;
        var startLevel = level;
        xp += delta;
        if (delta >= 0)
        {
            while (level < maxLevel && xp >= XpToNext(level, config))
            {
                xp -= XpToNext(level, config);
                level++;
            }
            if (level >= maxLevel) xp = 0;
        }
        else
        {
            while (xp < 0 && level > 1)
            {
                level--;
                xp += XpToNext(level, config);
            }
            if (xp < 0) xp = 0;
        }
        return (level, xp, level - startLevel);
    }
}
