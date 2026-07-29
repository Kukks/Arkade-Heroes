using ArkadeHeroes.Core;
using ArkadeHeroes.Core.Combat;
using ArkadeHeroes.Core.Equipment;
using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Heroes;

namespace ArkadeHeroes.Tests;

/// <summary>
/// ATTRIBUTES a fight's outcome to the four things that can move it — BREEDING (the genome), GEAR
/// (equipped items), LEVEL, and ENTROPY (the seed) — and pins the intended ordering so a later balance
/// edit that inverts it fails loudly.
///
/// The design decision this guards, in the owner's words: "breeding should be the most important, but gear
/// and leveling should influence." That is a statement about RELATIVE contribution, and until now nothing
/// measured it — <see cref="CombatCompetitivenessProbe"/> measures only how LOPSIDED a matchup is, which
/// says nothing about WHICH factor made it lopsided. The two probes are complements.
///
/// Method. For one fight, Y = 1 if side A wins. Let C be the seed-independent configuration
/// (genomes, levels, gear) and p(C) = P(A wins | C). The law of total variance then splits the outcome
/// EXACTLY, with no modelling assumption:
///     Var(Y) = Var_C(p)  +  E_C[p(1-p)]
///              ^ decided by the heroes   ^ still decided by the seed
/// so the ENTROPY share is E[p(1-p)]/Var(Y), and the rest is split between breeding, gear and level by
/// first-order Sobol indices. Because both sides are drawn from one distribution E[Y] = 1/2, so Var(Y) is
/// identically 1/4 and every share below is directly comparable across runs.
///
/// Two measurement details that the numbers depend on:
///   * Every win rate averages over a COIN-FLIPPED side assignment as well as the seed, so the "a"-slot
///     turn-order tiebreak (<c>BattleEngine.TurnOrder</c>) and the defender's timeout edge cancel exactly.
///     Getting this wrong is what biased the pre-#144 probe.
///   * The population is BRED (gen-3), never <see cref="Genome.NewGen0"/>: NewGen0 clears bytes [16..31],
///     so starters express no traits and would silently zero out the whole genome contribution. See
///     <c>ConfigStampReplayTests.Gen0Starters_ExpressNothing_SoTheyCannotTestThis</c>.
///
/// MEASURED 2026-07-29 on a ladder-like band (levels 8..12, gear uniform over none/tier-1/tier-2/tier-3),
/// 12 000 configurations x 64 seeds, at the constants of the day:
///     breeding 41.6%   entropy 30.5%   gear 12.3%   level 6.7%   interactions 9.0%
/// Of the SEED-INDEPENDENT part alone (69.5% of the total): breeding 60%, gear 18%, level 10%.
/// Breeding is the single largest lever, and gear and level are each a real, measurable share — which is
/// the shipped intent, so these guards are RATCHETS around it rather than a target to move toward.
///
/// Three findings the guards below encode, each of which contradicts an intuition worth naming:
///
/// 1. GEAR AND LEVEL ARE NOT WEAK LEVERS — they are the strongest per unit of investment. Against its own
///    naked twin (same genome, same level) a level-10 hero in the tier-3 set wins 96.8% of seeds; ONE
///    extra level wins 73.4% and +5 levels wins 95.3%. They read as small only because the BREEDING
///    spread they are measured against is larger still. Gear does decay with level — the same tier-3 set
///    is worth 96.8% at level 10 but 82.9% at level 50, because its mods are flat adds against stats that
///    keep growing — yet even at the level cap it is still decisive.
///
/// 2. THE RESOLVER IS HYPERSENSITIVE, and that — not a weak gear/level lever — is what makes a matchup
///    pre-decided. A fight is an attrition race whose damage term is Power*ATK/(DEF_target+25), so the
///    practical power index is ATK*HP*(DEF+25). A 10.7% edge on that index — exactly one level on a
///    median genome — already yields a 73.4% win rate, and a 61% edge (+5 levels) yields 95.3%. Nothing
///    about breeding is needed for a fight to lock: any small asymmetry does it, because the +-10% damage
///    roll averages away over dozens of exchanges long before the health bars do.
///
/// 3. RAISING GEAR OR LEVEL INFLUENCE MAKES THE PRE-DECIDED RATE WORSE, NOT BETTER. They are asymmetries
///    UNCORRELATED with breeding, so they stack on top of it rather than cancelling it. Measured on one
///    bred gen-3 population as the factors are switched on, one at a time
///    (<see cref="CombatCompetitivenessProbe"/>'s pre-decided metric, 600 pairs x 120 seeds):
///        equal level, no gear                35.3% pre-decided
///        equal level, gear U{none..tier-3}   42.8%
///        levels 8..12, gear U{none..tier-3}  47.5%
///        levels 5..25, gear U{none..tier-3}  58.8%
///    (Those four are comparable to EACH OTHER — one harness, one pool, sides randomised. The first row is
///    not a second reading of that probe's own 39.7%, which samples pairs differently and fixes the sides.)
///    So the two goals pull in opposite directions, and the algebra says they must: E[Y] = 1/2 by
///    symmetry, so Var(Y) = 1/4 identically, the entropy share is exactly 4*E[p(1-p)], and fewer
///    pre-decided matchups means a LARGER entropy share, never a smaller one. "Gear and level above
///    entropy" and "fewer pre-decided fights" cannot both be bought from the same 100%. The guards below
///    therefore rank breeding against gear and level only, and leave the entropy share to its own probe.
/// </summary>
public class CombatAttributionProbe
{
    /// <summary>The rules the SERVER actually ships: <c>GameOptions.InnateAbilities</c> is true since #149.
    /// <see cref="GameConfig.Default"/> keeps it false on purpose (it is what unstamped replays reconstruct
    /// under), so measuring on Default would attribute a game nobody plays.</summary>
    private static readonly GameConfig Live = GameConfig.Default with
    {
        Combat = GameConfig.Default.Combat with { InnateAbilities = true },
    };

    /// <summary>The gear ladder as a player climbs it: nothing, then each price tier's full three-slot set
    /// (500 / 2 500 / 10 000 sats per item — see <see cref="ItemCatalog"/>).</summary>
    private static readonly string[][] GearTiers =
    [
        [],
        ["rusty-blade", "padded-vest", "lucky-feather"],
        ["steel-saber", "chain-hauberk", "swift-anklet"],
        ["arkforged-edge", "covenant-plate", "vtxo-charm"],
    ];

    private const int Pedigree = 3;     // bred generations; gen-0 would be trait-blank
    private const int PoolSize = 3000;

    private static readonly Lazy<Genome[]> Pool = new(() =>
    {
        var rng = new Random(20260729);
        var pool = new Genome[PoolSize];
        for (var i = 0; i < PoolSize; i++) pool[i] = Bred(rng, Pedigree);
        return pool;
    });

    private static Genome Bred(Random rng, int gen)
    {
        var e = new byte[32];
        if (gen == 0)
        {
            rng.NextBytes(e);
            return Genome.NewGen0(e);
        }
        var a = Bred(rng, gen - 1);
        var b = Bred(rng, gen - 1);
        rng.NextBytes(e);
        return GeneMixer.Mix(a, b, e);
    }

    private static Hero Make(Genome g, string id, int level, int tier)
    {
        var h = new Hero { Id = id, OwnerId = "probe", Name = id, Genome = g, Generation = 0, Level = level };
        foreach (var itemId in GearTiers[tier]) h.Equipment.Equip(ItemCatalog.Find(itemId)!);
        return h;
    }

    /// <summary>P(A beats B) over <paramref name="n"/> fights, each with an independent seed AND a
    /// coin-flipped side assignment — so a 50.0% reading means "no edge", not "no edge plus the turn-order
    /// tiebreak that the id ordering hands to whoever is called 'a'".</summary>
    private static double WinRate(Genome ga, int la, int ea, Genome gb, int lb, int eb, int n, Random rng)
    {
        var wins = 0;
        var seed = new byte[32];
        for (var i = 0; i < n; i++)
        {
            rng.NextBytes(seed);
            var straight = rng.Next(2) == 0;
            var ha = Make(ga, straight ? "a" : "b", la, ea);
            var hb = Make(gb, straight ? "b" : "a", lb, eb);
            if (BattleEngine.Fight(ha, hb, seed, Live).WinnerId == ha.Id) wins++;
        }
        return wins / (double)n;
    }

    /// <summary>A per-index PRNG so <c>Parallel.For</c> stays bit-for-bit deterministic: the stream depends
    /// only on (label, index), never on scheduling. The SplitMix64 finalizer decorrelates adjacent indices,
    /// which consecutive <c>new Random(seed + i)</c> would not.</summary>
    private static Random Rng(int label, int index)
    {
        var z = (ulong)(((long)label << 32) | (uint)index) + 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;
        return new Random((int)z);
    }

    private readonly record struct Split(double Breeding, double Gear, double Level, double Entropy, double Interactions)
    {
        public string Report(string label) => $"""
            OUTCOME ATTRIBUTION ({label})
              breeding (genome) : {Breeding * 100,5:F1}%
              gear              : {Gear * 100,5:F1}%
              level             : {Level * 100,5:F1}%
              entropy (seed)    : {Entropy * 100,5:F1}%
              interactions      : {Interactions * 100,5:F1}%
            """;
    }

    /// <summary>Runs <paramref name="n"/> configurations drawn from the given level band and gear bag and
    /// splits the single-fight outcome variance four ways.</summary>
    private static Split Decompose(int n, int seeds, int loLevel, int hiLevel, int[] gearBag, int label)
    {
        var pool = Pool.Value;
        var tiers = gearBag.Distinct().OrderBy(t => t).ToArray();

        var ps = new double[n];
        var levelCell = new int[n];
        var gearCell = new int[n];
        Parallel.For(0, n, i =>
        {
            var rng = Rng(label, i);
            var ga = pool[rng.Next(pool.Length)];
            var gb = pool[rng.Next(pool.Length)];
            var la = rng.Next(loLevel, hiLevel + 1);
            var lb = rng.Next(loLevel, hiLevel + 1);
            var ea = gearBag[rng.Next(gearBag.Length)];
            var eb = gearBag[rng.Next(gearBag.Length)];
            levelCell[i] = (la - loLevel) * (hiLevel - loLevel + 1) + (lb - loLevel);
            gearCell[i] = Array.IndexOf(tiers, ea) * tiers.Length + Array.IndexOf(tiers, eb);
            ps[i] = WinRate(ga, la, ea, gb, lb, eb, seeds, rng);
        });

        // Law of total variance, with the finite-seed bias removed: E[p-hat(1-p-hat)] under-states
        // E[p(1-p)] by exactly a factor (1 - 1/seeds), and Var(p-hat) over-states Var_C(p) by E[p(1-p)]/seeds.
        var entropyVar = ps.Average(p => p * (1 - p)) * seeds / (seeds - 1.0);
        var configVar = Math.Max(0, Variance(ps) - entropyVar / seeds);
        var total = configVar + entropyVar;

        // The genome pair is effectively continuous, so it uses Sobol pick-freeze: hold the pair, redraw
        // level and gear TWICE independently. The two estimates carry independent sampling noise, so their
        // covariance is an unbiased estimate of Var_G(E[p|G]) with no noise floor to subtract.
        var first = new double[n];
        var second = new double[n];
        Parallel.For(0, n, i =>
        {
            var rng = Rng(label + 1, i);
            var ga = pool[rng.Next(pool.Length)];
            var gb = pool[rng.Next(pool.Length)];
            for (var rep = 0; rep < 2; rep++)
            {
                var la = rng.Next(loLevel, hiLevel + 1);
                var lb = rng.Next(loLevel, hiLevel + 1);
                var ea = gearBag[rng.Next(gearBag.Length)];
                var eb = gearBag[rng.Next(gearBag.Length)];
                var p = WinRate(ga, la, ea, gb, lb, eb, seeds, rng);
                if (rep == 0) first[i] = p; else second[i] = p;
            }
        });

        var breeding = Math.Max(0, Covariance(first, second)) / total;
        var level = BetweenCellVariance(ps, levelCell) / total;
        var gear = BetweenCellVariance(ps, gearCell) / total;
        var entropy = entropyVar / total;
        return new Split(breeding, gear, level, entropy, 1 - breeding - gear - level - entropy);
    }

    /// <summary>One-way random-effects ANOVA estimate of Var(E[p | cell]) — (MSB - MSW) / n-bar, floored at
    /// zero. Level and gear have SMALL DISCRETE factor spaces, where this is far lower-variance than
    /// pick-freeze; MSW absorbs both the other factors and the finite-seed noise, so what survives is the
    /// between-cell component only, which is exactly the Sobol first-order numerator.</summary>
    private static double BetweenCellVariance(double[] p, int[] cell)
    {
        var groups = new Dictionary<int, (int Count, double Sum, double SumSq)>();
        for (var i = 0; i < p.Length; i++)
        {
            var g = groups.GetValueOrDefault(cell[i]);
            groups[cell[i]] = (g.Count + 1, g.Sum + p[i], g.SumSq + p[i] * p[i]);
        }
        if (groups.Count < 2) return 0;   // a factor that does not vary has no between-cell variance
        var grand = p.Average();
        var msb = groups.Values.Sum(g => g.Count * Math.Pow(g.Sum / g.Count - grand, 2)) / (groups.Count - 1);
        var msw = groups.Values.Sum(g => g.SumSq - g.Sum * g.Sum / g.Count) / (p.Length - groups.Count);
        // Satterthwaite's n-bar, so unequal cell counts do not skew the component.
        var nbar = (p.Length - groups.Values.Sum(g => (double)g.Count * g.Count) / p.Length) / (groups.Count - 1);
        return Math.Max(0, (msb - msw) / nbar);
    }

    private static double Variance(double[] x)
    {
        var mean = x.Average();
        return x.Sum(v => (v - mean) * (v - mean)) / (x.Length - 1);
    }

    private static double Covariance(double[] a, double[] b)
    {
        var (ma, mb) = (a.Average(), b.Average());
        var sum = 0.0;
        for (var i = 0; i < a.Length; i++) sum += (a[i] - ma) * (b[i] - mb);
        return sum / (a.Length - 1);
    }

    // ── the guards ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BreedingIsTheBiggestLever_AndGearAndLevelBothStillMove()
    {
        // MEASURED at exactly these sizes: breeding 40.6%, gear 11.9%, level 6.4%, entropy 30.2%,
        // interactions 10.9% — the same split as the 12 000 x 64 run in the class summary, to within a point.
        var split = Decompose(4000, 48, loLevel: 8, hiLevel: 12, gearBag: [0, 1, 2, 3], label: 31_000);
        var report = split.Report("bred gen-3, levels 8-12, gear none/t1/t2/t3");

        // A RATCHET on the shipped ordering, not a target. Breeding must stay clearly the largest DESIGN
        // lever (entropy is deliberately NOT in this comparison: it is not something a player invests in,
        // and it moves the opposite way from the pre-decided rate — see the note at the end of the class).
        Assert.True(split.Breeding >= 0.30, $"breeding was demoted\n{report}");
        Assert.True(split.Breeding > split.Gear + 0.10, $"gear caught up with breeding\n{report}");
        Assert.True(split.Breeding > split.Level + 0.10, $"level caught up with breeding\n{report}");
        // ...and both of the player-investment levers must keep a real share. A change that makes gear or
        // level cosmetic breaks the same decision from the other side.
        Assert.True(split.Gear >= 0.06, $"gear stopped mattering\n{report}");
        Assert.True(split.Level >= 0.03, $"level stopped mattering\n{report}");
    }

    [Fact]
    public void TheSplitReadsZeroForAFactorThatDoesNotVary()
    {
        // Calibration. If the estimator invented signal, every guard above would be meaningless, so pin the
        // two cases whose true answer is known exactly: a factor held constant contributes NOTHING.
        var levelsPinned = Decompose(2000, 48, loLevel: 10, hiLevel: 10, gearBag: [0, 1, 2, 3], label: 32_000);
        var gearPinned = Decompose(2000, 48, loLevel: 8, hiLevel: 12, gearBag: [3], label: 33_000);

        Assert.True(levelsPinned.Level <= 0.01,
            $"level scored a share with every hero at level 10\n{levelsPinned.Report("levels all 10")}");
        Assert.True(gearPinned.Gear <= 0.01,
            $"gear scored a share with every hero in the same set\n{gearPinned.Report("gear all tier-3")}");
        // The same reading is the standing warning about the ECONOMY: gear is a one-off purchase that is
        // never lost, so a played-out roster converges on tier-3 and gear's share collapses to this zero —
        // not because it is weak (see the compensation guard below) but because it stops differentiating.
        Assert.True(gearPinned.Breeding > gearPinned.Gear,
            $"breeding lost primacy on a fully geared roster\n{gearPinned.Report("gear all tier-3")}");
    }

    /// <summary>Pairs of bred heroes screened at level 10 with no gear, keyed by how lopsided breeding alone
    /// makes them. Shared by the two compensation guards so the screen is paid for once.</summary>
    private static readonly Lazy<(Genome Favourite, Genome Underdog, double P0)[]> Screened = new(() =>
    {
        const int n = 4000;
        var pool = Pool.Value;
        var seen = new (Genome, Genome, double)[n];
        Parallel.For(0, n, i =>
        {
            var rng = Rng(34_000, i);
            var ga = pool[rng.Next(pool.Length)];
            var gb = pool[rng.Next(pool.Length)];
            var p = WinRate(ga, 10, 0, gb, 10, 0, 64, rng);
            seen[i] = p >= 0.5 ? (ga, gb, p) : (gb, ga, 1 - p);
        });
        return seen;
    });

    private static double TakesTheMatchup(
        (Genome Favourite, Genome Underdog, double P0)[] band, int underdogLevel, int underdogTier, int label)
    {
        var won = new int[band.Length];
        Parallel.For(0, band.Length, i =>
        {
            var rng = Rng(label, i);
            var favourite = WinRate(band[i].Favourite, 10, 0, band[i].Underdog, underdogLevel, underdogTier, 96, rng);
            won[i] = favourite < 0.5 ? 1 : 0;
        });
        return won.Sum() * 100.0 / band.Length;
    }

    [Fact]
    public void AGearedOrHigherLevelUnderdogCanTakeAModeratelyBetterBredMatchup()
    {
        // "Moderately better bred" made concrete: breeding alone hands the favourite 75-90% of the seeds.
        var band = Screened.Value.Where(x => x.P0 is >= 0.75 and < 0.90).Take(300).ToArray();
        Assert.True(band.Length >= 200, $"the screen found only {band.Length} moderate pairs");

        // MEASURED: over 300 such pairs (mean p0 82.6%), the underdog takes the matchup in 99.7% of them
        // with a tier-3 set and 90.0% of them at +5 levels. Investment already clears this bar comfortably.
        var geared = TakesTheMatchup(band, underdogLevel: 10, underdogTier: 3, label: 35_000);
        var levelled = TakesTheMatchup(band, underdogLevel: 15, underdogTier: 0, label: 36_000);
        var report = $"underdog with tier-3 gear takes {geared:F1}%, underdog at +5 levels takes {levelled:F1}% "
                   + $"of {band.Length} pairs the favourite wins {band.Average(x => x.P0) * 100:F1}% of naked at equal level";

        // The design intent, from the other direction: investment must be able to overturn a moderate
        // breeding deficit, or gear and levels are decoration.
        Assert.True(geared >= 85.0, $"gear can no longer overturn a moderate breeding edge\n{report}");
        Assert.True(levelled >= 70.0, $"levels can no longer overturn a moderate breeding edge\n{report}");
    }

    [Fact]
    public void NeitherGearNorLevelsOverturnADecisiveBreedingAdvantage()
    {
        // The ceiling that keeps the ordering honest: against a favourite breeding has ALREADY decided
        // (>=98% of seeds), buying the best set or out-levelling by five must NOT routinely take the fight.
        // Without this, "gear and level should influence" quietly becomes "gear and level decide".
        var band = Screened.Value.Where(x => x.P0 >= 0.98).Take(300).ToArray();
        Assert.True(band.Length >= 200, $"the screen found only {band.Length} decided pairs");

        // MEASURED: over 300 such pairs (mean p0 99.6%), a tier-3 set takes 27.3% of them and +5 levels
        // takes 8.0%. Breeding still has the last word where it has spoken decisively, which is the point.
        var geared = TakesTheMatchup(band, underdogLevel: 10, underdogTier: 3, label: 37_000);
        var levelled = TakesTheMatchup(band, underdogLevel: 15, underdogTier: 0, label: 38_000);
        var report = $"underdog with tier-3 gear takes {geared:F1}%, underdog at +5 levels takes {levelled:F1}% "
                   + $"of {band.Length} pairs the favourite wins {band.Average(x => x.P0) * 100:F1}% of naked at equal level";

        Assert.True(geared <= 45.0, $"gear now trumps a decided breeding advantage\n{report}");
        Assert.True(levelled <= 25.0, $"levels now trump a decided breeding advantage\n{report}");
    }
}
