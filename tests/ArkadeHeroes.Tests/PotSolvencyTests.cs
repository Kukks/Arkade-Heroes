using ArkadeHeroes.Core;
using ArkadeHeroes.Core.Combat;
using ArkadeHeroes.Core.Progression;

namespace ArkadeHeroes.Tests;

/// <summary>
/// STRUCTURAL solvency of the two split-pot payouts — the season prize pool and the tournament bracket. Both
/// hand real BTC out of a pot the treasury already holds, split among a podium by fixed percentage weights, so
/// the load-bearing invariant is that the shares can NEVER sum past the pot. <see cref="EconomySolvencyTests"/>
/// guards the daily faucet the same way; this guards the pots. These catch a future tuning change — a fatter
/// first-place weight that pushes a split over 100%, a <see cref="SeasonPrize.Split"/> that rounds up instead of
/// down, a rake default knocked out of band — that would quietly let a pot pay out more than it took in, long
/// before it drains a real treasury.
/// </summary>
public class PotSolvencyTests
{
    // Awkward pots on purpose: primes and just-over-round values expose any rounding that leaks a sat.
    private static readonly long[] Pots = [0, 1, 7, 99, 100, 101, 999, 1_000, 12_345, 1_000_000_007];

    [Fact]
    public void PrizeWeights_NeverSumPastTheWholePot()
    {
        // The single most likely future break: a designer rebalances a podium and the weights sum past 100, so
        // the floored shares add up past the pot. Pin BOTH split tables at or below the whole pot.
        Assert.True(Tournament.PrizeWeights.Sum() <= 100,
            $"tournament prize weights sum to {Tournament.PrizeWeights.Sum()}% — a >100% split pays the podium more than the pot");
        Assert.True(SeasonPrize.Weights.Sum() <= 100,
            $"season prize weights sum to {SeasonPrize.Weights.Sum()}% — a >100% split pays the podium more than the pot");
    }

    [Fact]
    public void Split_NeverPaysMoreThanThePot()
    {
        // The real Split, over awkward pots and every podium size from none to one past the table. Floor plus a
        // ≤100 weight sum means the shares can't out-run the pot; this pins that against a rounding change.
        foreach (var weights in new[] { Tournament.PrizeWeights, SeasonPrize.Weights })
            foreach (var pot in Pots)
                for (var winners = 0; winners <= weights.Count + 1; winners++)
                {
                    var paid = SeasonPrize.Split(pot, winners, weights).Sum();
                    Assert.True(paid <= pot,
                        $"splitting a {pot}-sat pot among {winners} winners by [{string.Join(',', weights)}] pays out {paid} — more than the pot");
                }
    }

    [Fact]
    public void TournamentPrizePool_NeverExceedsTheCollectedPot()
    {
        // The shipped default is in band, but the invariant must hold for ANY configured rake — a negative
        // one would otherwise inflate the pool ABOVE the pot and fund the gap on every tournament from a
        // real-BTC treasury. Tournament.PrizePool clamps to [0,100] so it can't; the default is 10% today.
        Assert.InRange(GameConfig.Default.TournamentRakePct, 0, 100);

        // Drives the REAL Tournament.PrizePool (what ResolveTournamentAsync calls), across every rake INCLUDING
        // out-of-band negatives and >100: pot = buy-in × entrants, the house keeps whatever the podium doesn't.
        // The pool must stay within [0, pot] and the podium payout PLUS the rake kept must never exceed the pot.
        foreach (var buyIn in new long[] { 1, 500, 10_000, 1_000_000 })
            foreach (var entrants in new[] { 2, 3, 4, 8, 16, 17 })
                foreach (var rakePct in new[] { -50, 0, 10, 50, 100, 150 })
                {
                    var pot = buyIn * entrants;
                    var prizePool = Tournament.PrizePool(pot, rakePct);
                    Assert.InRange(prizePool, 0, pot);        // clamped: never above the pot, never negative
                    var rakeKept = pot - prizePool;           // the house keeps whatever isn't the pool

                    foreach (var podium in new[] { 1, 2 })   // champion only, or champion + runner-up
                    {
                        var paid = SeasonPrize.Split(prizePool, podium, Tournament.PrizeWeights).Sum();
                        Assert.True(paid + rakeKept <= pot,
                            $"buy-in {buyIn} × {entrants} entrants at rake {rakePct}%: podium {paid} + rake {rakeKept} exceeds the {pot}-sat pot");
                    }
                }
    }

    [Fact]
    public void TheGuardHasTeeth_AnOver100SplitReallyDoesOverpay()
    {
        // Proves the ≤100 guard above is load-bearing, not vacuous: a hypothetical 70/40 table (110%) DOES pay a
        // divisible pot more than it holds. That is exactly what PrizeWeights_NeverSumPastTheWholePot exists to
        // catch — if such a table ever ships, that test fails, and this is the reason it would.
        IReadOnlyList<int> broken = [70, 40];
        var paid = SeasonPrize.Split(1_000, broken.Count, broken).Sum();
        Assert.True(paid > 1_000,
            "sanity: a 110% split should over-pay a 1,000-sat pot, else the solvency guard proves nothing");
    }
}
