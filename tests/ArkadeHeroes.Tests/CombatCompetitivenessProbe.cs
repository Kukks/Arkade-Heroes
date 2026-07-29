using ArkadeHeroes.Core;
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

    /// <summary>A gen-0 starter: <see cref="Genome.NewGen0"/> zeroes bytes [16..31], so it is trait-BLANK.</summary>
    private static Genome Gen0(Random rng)
    {
        var e = new byte[32];
        rng.NextBytes(e);
        return Genome.NewGen0(e);
    }

    /// <summary>A hero bred down a full pedigree of <paramref name="gen"/> generations from gen-0 founders.
    /// Traits are EARNED by breeding (a gen-0 expresses none), so this is the only population on which the
    /// genome-derived innate passives can do anything at all.</summary>
    private static Genome Bred(Random rng, int gen)
    {
        if (gen == 0) return Gen0(rng);
        var a = Bred(rng, gen - 1);
        var b = Bred(rng, gen - 1);
        var e = new byte[32];
        rng.NextBytes(e);
        return GeneMixer.Mix(a, b, e);
    }

    /// <summary>Runs the sweep under one config and returns the report plus the two headline percentages.
    /// <paramref name="genome"/> draws from <c>rng</c>, so two Measure calls with the same factory face the
    /// IDENTICAL population — the only difference between them is the config.</summary>
    private static (string Report, double Competitive, double PreDecided) Measure(
        GameConfig? config, string label, Func<Random, Genome> genome)
    {
        var rng = new Random(20260722); // fixed: the report must be reproducible
        var buckets = new int[5];       // [40-60), [60-75), [75-90), [90-98), [98-100]
        var decided = 0;
        var sum = 0.0;
        var traits = 0;

        for (var p = 0; p < Pairs; p++)
        {
            var a = HeroAt(genome(rng), "a", 10);
            var b = HeroAt(genome(rng), "b", 10);
            traits += Traits.InnatePassives(a.Genome).Count + Traits.InnatePassives(b.Genome).Count;

            var aWins = 0;
            for (var s = 0; s < SeedsPerPair; s++)
            {
                var seed = new byte[32];
                rng.NextBytes(seed);
                if (BattleEngine.Fight(a, b, seed, config).WinnerId == "a") aWins++;
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
            COMBAT COMPETITIVENESS ({label}) — {Pairs} random equal-level (10) gen-0 pairs x {SeedsPerPair} seeds
              competitive   40-60%  : {buckets[0],4}  ({competitive:F1}%)
              slight edge   60-75%  : {buckets[1],4}  ({buckets[1] * 100.0 / Pairs:F1}%)
              strong edge   75-90%  : {buckets[2],4}  ({buckets[2] * 100.0 / Pairs:F1}%)
              near-locked   90-98%  : {buckets[3],4}  ({buckets[3] * 100.0 / Pairs:F1}%)
              PRE-DECIDED   98-100% : {buckets[4],4}  ({preDecided:F1}%)
              mean lopsidedness     : {sum / Pairs:F1}%   (50 = pure coin flip)
              mean innate passives  : {traits / (2.0 * Pairs):F2} per hero  (0 ⇒ nothing for a proc to key off)
            """;
        return (report, competitive, preDecided);
    }

    [Fact]
    public void Report_HowOftenTheSeedActuallyDecidesTheFight()
    {
        var (report, competitive, preDecided) = Measure(null, "shipped default, gen-0", Gen0);

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

    [Fact]
    public void Report_WhatFlippingInnateProcsOnWouldCostCompetitiveness()
    {
        // The same sweep with InnateAbilities ON, so the cost of flipping the flag is a number rather than a
        // guess. It must run on a BRED population: the sibling probe above uses gen-0 starters, whose trait
        // bytes Genome.NewGen0 zeroes outright, so flipping the flag on THAT population provably changes
        // nothing (measured: 40.7% → 40.7%). Both sweeps below face the IDENTICAL gen-3 population.
        //
        // MEASURED (innate-v3 rare procs, InnateBonuses.Default):
        //   gen-0  0.00 passives/hero   pre-decided 40.7% → 40.7%   competitive 6.3% → 6.3%
        //   gen-3  0.32 passives/hero   pre-decided 38.7% → 39.7%   competitive 7.7% → 8.7%
        //   gen-5  0.46 passives/hero   pre-decided 38.0% → 37.7%   competitive 9.7% → 9.3%
        //   gen-7  0.60 passives/hero   pre-decided 39.7% → 40.3%   competitive 5.7% → 7.0%
        //
        // The honest reading: rare procs do NOT move the pre-decided share — it sits at ~38-41% with the flag
        // either way, and the ±1% wobble above is noise, not signal. Two reasons, both structural:
        //   1. Trait acquisition is sparse. Traits only arrive by mutation during breeding, so even a gen-7
        //      pedigree averages 0.60 expressed cosmetic traits per hero — most fighters have no proc at all.
        //   2. A proc one side has and the other lacks is a systematic EDGE, not a coin flip. It can change
        //      WHICH side is locked in; it cannot make a 7x stat gap contestable.
        // The pre-decided lever remains StatBlock.StatValue (see the sibling probe), and raising proc rates far
        // enough to overturn lopsided pairs would blow through the [0.53, 0.70] per-passive balance band that
        // InnateAbilitiesTests.BalanceProbe pins. This guard is therefore a RATCHET in the same spirit as its
        // sibling: flipping the flag on must not make competitiveness materially worse.
        var population = (Random r) => Bred(r, 3);
        var off = Measure(null, "bred gen-3, flag off", population);
        var on = Measure(GameConfig.Default with
        {
            Combat = GameConfig.Default.Combat with { InnateAbilities = true },
        }, "bred gen-3, innate procs on", population);

        var both = $"{off.Report}\n\n{on.Report}";
        Assert.True(on.PreDecided <= off.PreDecided + 3.0, $"procs made combat MORE pre-determined\n{both}");
        Assert.True(on.Competitive >= off.Competitive - 3.0, $"procs cost competitive matchups\n{both}");
    }
}
