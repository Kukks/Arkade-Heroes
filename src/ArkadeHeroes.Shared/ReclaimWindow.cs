namespace ArkadeHeroes.Shared;

/// <summary>
/// The reclaim timelock, phrased for a player looking at a stranded asset. Every covenant escrow in the
/// game recovers through a leaf that only opens once CHAIN time (median-time-past, never the wall clock)
/// reaches the escrow's <c>ReclaimAfterUnixSeconds</c>, and the client reclaim flows refuse to submit a
/// second early — a refused submission permanently poisons the canonical txid's event stream on arkd, so
/// "try it and see" is not a safe way to discover the window is shut.
///
/// That makes the wait itself information the player is owed: a disabled button that says how long is left
/// answers "wait, or worry?", where a button that throws answers neither. <see cref="IsUnlocked"/> gates on
/// exactly the comparison the flows gate on, so the UI and the covenant can never disagree about whether a
/// reclaim is spendable.
/// </summary>
public static class ReclaimWindow
{
    /// <summary>
    /// True when the reclaim leaf is spendable at <paramref name="chainUnixSeconds"/>. The covenant flows
    /// throw <c>RefundNotYetDueException</c> when <c>chainNow &lt; reclaimAfter</c>, so the boundary second
    /// itself IS spendable — this mirrors that comparison rather than restating it, because a stricter UI
    /// would hide a working reclaim and a looser one would offer a spend that gets refused.
    /// </summary>
    public static bool IsUnlocked(long reclaimAfterUnixSeconds, long chainUnixSeconds) =>
        chainUnixSeconds >= reclaimAfterUnixSeconds;

    /// <summary>
    /// Chain-seconds still to wait, clamped at zero. Clamped rather than signed so a page ticking a
    /// countdown past the unlock cannot render a negative wait.
    /// </summary>
    public static long SecondsRemaining(long reclaimAfterUnixSeconds, long chainUnixSeconds) =>
        Math.Max(0, reclaimAfterUnixSeconds - chainUnixSeconds);

    /// <summary>
    /// A short player-facing label for the wait: <c>"unlocked"</c>, or an approximate countdown in the
    /// largest useful unit. Deliberately approximate (<c>~</c>) — it is derived from the chain's
    /// median-time-past, which advances in jumps as blocks arrive, so a to-the-second promise would be a
    /// lie. Anything still locked reports a nonzero wait rather than rounding down to <c>~0h</c>, which
    /// would read as unlocked.
    /// </summary>
    public static string Describe(long reclaimAfterUnixSeconds, long chainUnixSeconds)
    {
        var remaining = SecondsRemaining(reclaimAfterUnixSeconds, chainUnixSeconds);
        return remaining switch
        {
            0 => "unlocked",
            < 60 => "unlocks in <1m",
            < 3_600 => $"unlocks in ~{remaining / 60}m",
            // Hours stay useful well past a day: the breed/merge/offer windows are set ~24h out, and
            // "~36h" tells a player more than "~1d" does.
            < 2 * 86_400 => $"unlocks in ~{remaining / 3_600}h",
            _ => $"unlocks in ~{remaining / 86_400}d",
        };
    }
}
