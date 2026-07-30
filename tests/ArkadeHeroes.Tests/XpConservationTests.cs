using ArkadeHeroes.Core;
using ArkadeHeroes.Core.Progression;

namespace ArkadeHeroes.Tests;

/// <summary>
/// XP is never CREATED by a staked fight — the one property that makes the ladder Sybil-proof.
///
/// The conserved transfer exists so self-play across any number of wallets can only concentrate XP
/// between your own heroes. That argument only holds if the winner gains exactly what the loser paid.
/// <see cref="Leveling.Apply"/> floors a losing hero at level 1 with 0 XP, so a loser can be asked for
/// more than it owns; settling on the raw <see cref="Leveling.XpTransfer"/> gap therefore credited the
/// winner XP nobody paid. <see cref="Leveling.PayableTransfer"/> is the clamp, and these are its teeth.
///
/// The exploit this closes needs no exotic setup: a free starter sits at level 1 with 0 XP, which is
/// already the floor, so it pays nothing when it loses and does not deplete. Beaten repeatedly it minted
/// the full base transfer every time, and the level-gap clamp still pays out while the winner is within
/// three levels of it — so the bottom of the ladder printed XP for as long as someone kept fighting.
/// </summary>
public class XpConservationTests
{
    /// <summary>Total XP a hero holds — banked in the levels it climbed, plus progress toward the next.</summary>
    private static long Banked(int level, long xp) => Leveling.TotalXp(level, xp);

    /// <summary>Settles one staked fight the way the server does and reports the change in total XP.</summary>
    private static (long Moved, long SystemDelta) Settle(
        (int Level, long Xp) winner, (int Level, long Xp) loser)
    {
        var moved = Leveling.PayableTransfer(winner.Level, loser.Level, loser.Xp);
        var before = Banked(winner.Level, winner.Xp) + Banked(loser.Level, loser.Xp);

        var (wl, wx, _) = Leveling.Apply(winner.Level, winner.Xp, +moved);
        var (ll, lx, _) = Leveling.Apply(loser.Level, loser.Xp, -moved);

        return (moved, Banked(wl, wx) + Banked(ll, lx) - before);
    }

    [Fact]
    public void ABrokeLoserPaysNothing_SoTheFightCannotMintXp()
    {
        // Two free starters. The loser is already at the floor, so it has nothing to hand over —
        // and the winner must therefore receive nothing, however large the gap says the prize is.
        Assert.True(Leveling.XpTransfer(1, 1) > 0, "the raw gap must be non-zero, or this proves nothing");

        var (moved, systemDelta) = Settle(winner: (1, 0), loser: (1, 0));

        Assert.Equal(0, moved);
        Assert.Equal(0, systemDelta);
    }

    [Fact]
    public void TheFreeStarterPunchingBagStaysWorthless_AcrossEveryWinnerTheGapStillPays()
    {
        // The clamp pays out while the winner is within a few levels of the loser, so a level-1 bag was
        // farmable by a whole band of winners, not just a peer. None of them may mint.
        for (var winnerLevel = 1; Leveling.XpTransfer(winnerLevel, 1) > 0; winnerLevel++)
        {
            var (moved, systemDelta) = Settle(winner: (winnerLevel, 0), loser: (1, 0));
            Assert.Equal(0, moved);
            Assert.Equal(0, systemDelta);
        }
    }

    [Fact]
    public void APartlyBrokeLoserPaysExactlyWhatItOwns_AndNotAFractionMore()
    {
        // The in-between case: the loser owns something, but less than the gap asks for.
        var gap = Leveling.XpTransfer(1, 1);
        var owned = gap - 1;

        var (moved, systemDelta) = Settle(winner: (1, 0), loser: (1, owned));

        Assert.Equal(owned, moved);
        Assert.Equal(0, systemDelta);
    }

    [Fact]
    public void ASolventLoserPaysTheFullGap_SoOrdinaryFightsAreUnchanged()
    {
        // The clamp must be invisible in normal play: a loser with plenty banked pays the gap in full.
        // If this ever fails the fix has started rewriting ordinary progression, not just the edge.
        var solvent = (Level: 6, Xp: 0L);
        var gap = Leveling.XpTransfer(5, solvent.Level);

        Assert.True(Banked(solvent.Level, solvent.Xp) > gap, "fixture must be able to afford the gap");

        var (moved, systemDelta) = Settle(winner: (5, 0), loser: solvent);

        Assert.Equal(gap, moved);
        Assert.Equal(0, systemDelta);
    }

    [Fact]
    public void PayableTransferNeverExceedsTheGap_NorTheLosersBalance()
    {
        // Swept rather than sampled, because the two bounds bind in different regions and a single
        // fixture only ever demonstrates one of them.
        for (var winnerLevel = 1; winnerLevel <= 12; winnerLevel++)
        for (var loserLevel = 1; loserLevel <= 12; loserLevel++)
        foreach (var loserXp in new long[] { 0, 1, 39, 250, 5_000 })
        {
            var moved = Leveling.PayableTransfer(winnerLevel, loserLevel, loserXp);

            Assert.True(moved >= 0, $"negative transfer at w{winnerLevel} l{loserLevel} xp{loserXp}");
            Assert.True(moved <= Leveling.XpTransfer(winnerLevel, loserLevel),
                $"exceeded the gap at w{winnerLevel} l{loserLevel} xp{loserXp}");
            Assert.True(moved <= Banked(loserLevel, loserXp),
                $"took more than the loser owns at w{winnerLevel} l{loserLevel} xp{loserXp}");
        }
    }

    [Fact]
    public void AtTheLevelCeilingXpIsBurned_NotMinted_WhichIsTheSafeDirection()
    {
        // Documented asymmetry, deliberately NOT "fixed": a hero at MaxLevel keeps no surplus, so the
        // loser's stake leaves the game. That is a sink. Sinks cannot make the books insolvent; mints can.
        // Pinned so the direction of the leak stays a decision rather than an accident.
        var max = GameConfig.Default.Curve.MaxLevel;
        var (moved, systemDelta) = Settle(winner: (max, 0), loser: (max, 0));

        Assert.True(moved > 0, "a peer fight at the ceiling should still charge the loser");
        Assert.True(systemDelta < 0, "the ceiling must destroy XP, never create it");
        Assert.Equal(-moved, systemDelta);
    }

    [Fact]
    public void TotalXpCountsEveryLevelClimbed()
    {
        Assert.Equal(0, Leveling.TotalXp(1, 0));
        Assert.Equal(7, Leveling.TotalXp(1, 7));
        Assert.Equal(Leveling.XpToNext(1), Leveling.TotalXp(2, 0));
        Assert.Equal(Leveling.XpToNext(1) + Leveling.XpToNext(2) + 5, Leveling.TotalXp(3, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => Leveling.TotalXp(0, 0));
    }
}
