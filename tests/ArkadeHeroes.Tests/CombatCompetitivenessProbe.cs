using ArkadeHeroes.Core.Combat;
using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Heroes;

namespace ArkadeHeroes.Tests;

/// <summary>
/// Measures how much of a fight is decided BEFORE the seed is drawn. A collectible battler is
/// only fun if a matchup is a contest; if the same pair produces the same winner across every
/// seed, the replay animation is decoration over a lookup. Reports the distribution of per-pair
/// win rates over many seeds so a balance change can be judged on numbers, not vibes.
/// </summary>
public class CombatCompetitivenessProbe
{
    private const int Pairs = 300;
    private const int SeedsPerPair = 120;

    private static Hero HeroAt(Genome genome, string id, int level) => new()
    {
        Id = id, OwnerId = "probe", Name = id, Genome = genome, Generation = 0, Level = level,
    };

    [Fact]
    public void Report_HowOftenTheSeedActuallyDecidesTheFight()
    {
        var rng = new Random(20260722); // fixed: the report must be reproducible
        var buckets = new int[5];       // [40-60), [60-75), [75-90), [90-98), [98-100]
        var decided = 0;
        var sum = 0.0;

        for (var p = 0; p < Pairs; p++)
        {
            var ea = new byte[32]; var eb = new byte[32];
            rng.NextBytes(ea); rng.NextBytes(eb);
            var a = HeroAt(Genome.NewGen0(ea), "a", 10);
            var b = HeroAt(Genome.NewGen0(eb), "b", 10);

            var aWins = 0;
            for (var s = 0; s < SeedsPerPair; s++)
            {
                var seed = new byte[32];
                rng.NextBytes(seed);
                if (BattleEngine.Fight(a, b, seed).WinnerId == "a") aWins++;
            }

            // Fold to "how lopsided", regardless of which side won.
            var rate = Math.Max(aWins, SeedsPerPair - aWins) * 100.0 / SeedsPerPair;
            sum += rate;
            if (rate >= 98) { buckets[4]++; decided++; }
            else if (rate >= 90) buckets[3]++;
            else if (rate >= 75) buckets[2]++;
            else if (rate >= 60) buckets[1]++;
            else buckets[0]++;
        }

        var competitive = buckets[0] * 100.0 / Pairs;
        var preDecided = decided * 100.0 / Pairs;
        var report = $"""
            COMBAT COMPETITIVENESS — {Pairs} random equal-level (10) gen-0 pairs x {SeedsPerPair} seeds
              competitive   40-60%  : {buckets[0],4}  ({competitive:F1}%)
              slight edge   60-75%  : {buckets[1],4}  ({buckets[1] * 100.0 / Pairs:F1}%)
              strong edge   75-90%  : {buckets[2],4}  ({buckets[2] * 100.0 / Pairs:F1}%)
              near-locked   90-98%  : {buckets[3],4}  ({buckets[3] * 100.0 / Pairs:F1}%)
              PRE-DECIDED   98-100% : {buckets[4],4}  ({preDecided:F1}%)
              mean lopsidedness     : {sum / Pairs:F1}%   (50 = pure coin flip)
            """;

        // Measured on 2026-07-22 at the constants of the day: 6.3% competitive, 40.7% pre-decided,
        // 87.8% mean. Those are POOR numbers — 4 in 10 random equal-level matchups have the same
        // winner under every seed — and this guard is deliberately loose so it only trips on a
        // change that makes it WORSE. It is a ratchet, not an endorsement of the current tuning.
        //
        // The dominant lever is NOT the element ring: it is StatBlock.StatValue, where the base
        // term 10 + gene/4 spans 10..73 (7.3x) and the growth term 1 + growthGene/64 multiplies
        // 1..4 per level. Softening the ring alone moves pre-decided only 40.7% -> 34.0% (measured).
        Assert.True(preDecided <= 50.0, $"combat got MORE pre-determined (was 40.7%)\n{report}");
        Assert.True(competitive >= 3.0, $"combat lost competitive matchups (was 6.3%)\n{report}");
    }
}
