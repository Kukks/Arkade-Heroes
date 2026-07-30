using ArkadeHeroes.Shared;

namespace ArkadeHeroes.Tests;

/// <summary>
/// The reclaim timelock as a player is shown it. A covenant reclaim leaf only opens once CHAIN time
/// (median-time-past) reaches the escrow's <c>ReclaimAfterUnixSeconds</c>, and the client flows refuse to
/// submit before then — so the UI has to answer "wait, or worry?" without pressing the button to find out.
/// These pin the boundary to the SAME comparison the covenant flows gate on, so a button can never be
/// enabled a second before the spend would be refused.
/// </summary>
public class ReclaimWindowTests
{
    // An arbitrary fixed chain time — nothing here reads the wall clock.
    const long Now = 1_800_000_000;

    [Fact]
    public void Unlocks_AtExactlyTheCovenantsOwnBoundary()
    {
        // The flows throw when `chainNow < refundAfter`, so chainNow == refundAfter is SPENDABLE.
        // Off-by-one here either hides a reclaim that would work or offers one that would be refused.
        Assert.True(ReclaimWindow.IsUnlocked(Now, Now));
        Assert.False(ReclaimWindow.IsUnlocked(Now + 1, Now));
        Assert.True(ReclaimWindow.IsUnlocked(Now - 1, Now));
    }

    [Fact]
    public void UnlockedEscrow_SaysSo_WithoutACountdown()
    {
        Assert.Equal("unlocked", ReclaimWindow.Describe(Now, Now));
        Assert.Equal("unlocked", ReclaimWindow.Describe(Now - 90_000, Now));
    }

    [Fact]
    public void LockedEscrow_CountsDownInTheLargestUsefulUnit()
    {
        // The breed/merge/offer windows are set a day out, so hours and days are the cases that matter.
        Assert.Equal("unlocks in ~24h", ReclaimWindow.Describe(Now + 86_400, Now));
        Assert.Equal("unlocks in ~3h", ReclaimWindow.Describe(Now + 3 * 3600, Now));
        Assert.Equal("unlocks in ~5d", ReclaimWindow.Describe(Now + 5 * 86_400, Now));
    }

    [Fact]
    public void NearlyOpen_StaysHonestRatherThanRoundingToZero()
    {
        // "~0h" would read as unlocked. Anything still locked must say a nonzero wait.
        Assert.Equal("unlocks in ~9m", ReclaimWindow.Describe(Now + 9 * 60, Now));
        Assert.Equal("unlocks in <1m", ReclaimWindow.Describe(Now + 1, Now));
        Assert.Equal("unlocks in <1m", ReclaimWindow.Describe(Now + 59, Now));
    }

    [Fact]
    public void SecondsRemaining_IsNeverNegative()
    {
        // The page ticks a countdown; a negative would render as a nonsense label.
        Assert.Equal(0, ReclaimWindow.SecondsRemaining(Now - 5_000, Now));
        Assert.Equal(600, ReclaimWindow.SecondsRemaining(Now + 600, Now));
    }
}
