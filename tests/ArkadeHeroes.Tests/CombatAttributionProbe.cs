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
///    keep growing — yet even at the level cap it is still decisive. Nor is that an artefact of the naked
///    control: ONE TIER apart, which is the live comparison once nobody fights bare, tier-3 beats tier-2
///    81.6% at level 10 and 66.8% at level 50 (tier-2 over tier-1: 80.6% / 67.8%).
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
///
/// 4. GEAR'S ENDGAME SHARE OF 0.0% IS A STATEMENT ABOUT VARIETY, NOT ABOUT POWER — and the two need
///    different instruments. A tier-3 set is worth 96.5% against its own naked twin and still reads 0.0% on
///    a roster where everyone owns one, because a factor that does not VARY cannot explain variance. So the
///    fix for "gear stopped mattering" is never a bigger number on the set; it is a second answer at the
///    same price. That is what the counter line is (<see cref="CombatShapes"/>), and measuring it needs the
///    TOTAL-effect index in <see cref="GearTotalEffect"/>: a counter is +Edge against one shape and -Edge
///    against another, so its MEAN effect is zero BY DESIGN — "no single best set" and "zero first-order
///    index" are the same sentence — and only an index that counts interactions can see it. Measured on a
///    fully-geared roster: first-order 0.2% → 0.5% (nothing), total-effect 0.0% → 9.3%.
///
/// 5. THE ELEMENT RING IS TOO THIN TO BUILD COUNTERS ON, which is why the counter line keys on build SHAPE
///    instead. The ring is the counter mechanism the game already ships, so it was the obvious candidate;
///    the measurements say no on both axes at once:
///        SURFACE  — elements are near-uniform in a bred pool, so a random pair is ring-NEUTRAL 75.0% of
///                   the time (12.44% attacker-strong / 12.57% attacker-weak / 74.99% neutral over 200 000
///                   bred gen-3 pairs). Gear keyed on the ring would do nothing in three fights out of four.
///        MAGNITUDE — where it DOES fire it is already a switch, not a tilt: genetic twins differing ONLY in
///                   byte[5] hand the ring-advantaged side 87.3% of seeds (89.4% with both in tier-3), which
///                   sits between one extra level (73.4%) and a whole tier-3 set (96.8%). So it cannot be
///                   leaned on harder either — by finding 3, widening it would only raise the pre-decided
///                   rate. Its first-order variance share is 8.9% on the ladder band and 12.1% on a
///                   fully-geared roster, where it is the largest non-entropy differentiator left.
///    Build shape passes both tests the ring fails: every hero has one, so every matchup keys, and the split
///    over a bred pool is 34.5% / 32.7% / 32.9% with the three axes essentially uncorrelated.
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

    /// <summary>Counters + wildcard ON, on top of <see cref="Live"/> — the rules the counter guards measure,
    /// and now also what the server ships: <c>GameOptions.GearCounters</c> defaults true once the hero card
    /// began showing a hero's build shape, which is the visibility the flip was waiting on.
    /// <see cref="GameConfig.Default"/> still keeps both flags off (it is what unstamped replays reconstruct
    /// under), which is why this config is built up here rather than read from Default.</summary>
    private static readonly GameConfig Countered = Live with
    {
        Combat = Live.Combat with { GearCounters = true },
    };

    /// <summary>The gear ladder as a player climbs it: nothing, then each price tier's full three-slot set
    /// (500 / 2 500 / 10 000 sats per item — see <see cref="ItemCatalog"/>). Indices 0-3 are the LADDER and
    /// are what the original guards measure; 4-7 are the ENDGAME SHELF — four different answers at the same
    /// tier-3 price, which is the whole point of the counter line and the only reason a fully-geared roster
    /// can have any gear variance left at all.</summary>
    private static readonly string[][] GearTiers =
    [
        [],
        ["rusty-blade", "padded-vest", "lucky-feather"],
        ["steel-saber", "chain-hauberk", "swift-anklet"],
        ["arkforged-edge", "covenant-plate", "vtxo-charm"],
        ["arkforged-edge", "covenant-plate", "bulwark-ward"],
        ["arkforged-edge", "covenant-plate", "sunder-sigil"],
        ["arkforged-edge", "covenant-plate", "snare-loop"],
        ["arkforged-edge", "covenant-plate", "chaos-prism"],
    ];

    /// <summary>The four tier-3 loadouts a played-out roster chooses between: the plain charm and the three
    /// counters. (The wildcard, index 7, is a different axis and is measured on its own.)</summary>
    private static readonly int[] EndgameShelf = [3, 4, 5, 6];

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
    private static double WinRate(
        Genome ga, int la, int ea, Genome gb, int lb, int eb, int n, Random rng, GameConfig? config = null)
    {
        var cfg = config ?? Live;
        var wins = 0;
        var seed = new byte[32];
        for (var i = 0; i < n; i++)
        {
            rng.NextBytes(seed);
            var straight = rng.Next(2) == 0;
            var ha = Make(ga, straight ? "a" : "b", la, ea);
            var hb = Make(gb, straight ? "b" : "a", lb, eb);
            if (BattleEngine.Fight(ha, hb, seed, cfg).WinnerId == ha.Id) wins++;
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
    private static Split Decompose(
        int n, int seeds, int loLevel, int hiLevel, int[] gearBag, int label, GameConfig? config = null)
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
            ps[i] = WinRate(ga, la, ea, gb, lb, eb, seeds, rng, config);
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
                var p = WinRate(ga, la, ea, gb, lb, eb, seeds, rng, config);
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

    /// <summary>
    /// Gear's TOTAL-effect Sobol index: the share of outcome variance that would disappear if gear stopped
    /// varying — its main effect PLUS every interaction it takes part in.
    ///
    /// This exists because the first-order index in <see cref="Decompose"/> is structurally BLIND to a
    /// counter, and the reason is worth stating plainly, because it inverts the obvious success metric. A
    /// counter is worth +Edge against one shape and -Edge against another, so averaged over a pool split
    /// evenly three ways its mean effect is ZERO — which is precisely the design goal "no single best set".
    /// A first-order index measures exactly that mean. So demanding gear's FIRST-ORDER share recover on a
    /// fully-geared roster is demanding that some set be better on average, i.e. demanding the convergence
    /// back. Measured: at a ±25% edge the counter moves the endgame first-order gear share from 0.2% to
    /// 0.5% — nothing — while moving the total-effect share from 0.1% to 16.5%.
    ///
    /// Jansen's estimator: freeze breeding and level, redraw GEAR twice, and half the mean squared difference
    /// is the numerator. The finite-seed correction is the same idea as <see cref="Decompose"/>'s — two
    /// independent p-hats carry 2·E[p(1-p)]/seeds of pure seed noise between them, so that comes off first.
    /// </summary>
    private static (double Index, double Entropy) GearTotalEffect(
        int n, int seeds, int loLevel, int hiLevel, int[] gearBag, int label, GameConfig config)
    {
        var pool = Pool.Value;
        var ps = new double[n];
        var squaredDiff = new double[n];
        Parallel.For(0, n, i =>
        {
            var rng = Rng(label, i);
            var ga = pool[rng.Next(pool.Length)];
            var gb = pool[rng.Next(pool.Length)];
            var la = rng.Next(loLevel, hiLevel + 1);
            var lb = rng.Next(loLevel, hiLevel + 1);
            var first = WinRate(ga, la, gearBag[rng.Next(gearBag.Length)],
                gb, lb, gearBag[rng.Next(gearBag.Length)], seeds, rng, config);
            var second = WinRate(ga, la, gearBag[rng.Next(gearBag.Length)],
                gb, lb, gearBag[rng.Next(gearBag.Length)], seeds, rng, config);
            ps[i] = first;
            squaredDiff[i] = (first - second) * (first - second);
        });

        var entropyVar = ps.Average(p => p * (1 - p)) * seeds / (seeds - 1.0);
        var total = Math.Max(0, Variance(ps) - entropyVar / seeds) + entropyVar;
        var index = Math.Max(0, squaredDiff.Average() - 2 * entropyVar / seeds) / 2 / total;
        return (index / 1.0, entropyVar / total);
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

    // ── gear as a COLLECTION rather than a convergence ────────────────────────────────────────────

    [Fact]
    public void GearStillDecidesFightsOnAFullyGearedRoster_OnceTheEndgameHasMoreThanOneAnswer()
    {
        // THE guard this feature exists to satisfy, and the one the sibling calibration test above states as
        // a standing warning: on a played-out roster where everybody owns the top tier, gear used to stop
        // mattering — not because it is weak, but because everyone's was identical. The fix is not a bigger
        // number on the set; it is a SECOND ANSWER at the same price.
        //
        // Measured at exactly these sizes: gear's TOTAL-effect share of a fully-geared roster's outcome
        // variance goes 0.0% (one tier-3 set — today) → 9.3% (the four-loadout endgame shelf), and 10.1% for
        // the three counters alone. The first number is a near-tautology and stays put no matter how strong
        // gear is, which is exactly why the instrument here is the total-effect index and not Decompose's
        // first-order one (see GearTotalEffect).
        var single = GearTotalEffect(1200, 48, 8, 12, [3], 39_000, Countered);
        var shelf = GearTotalEffect(1200, 48, 8, 12, EndgameShelf, 40_000, Countered);
        var report = $"one tier-3 set: {single.Index * 100:F1}%   four-loadout shelf: {shelf.Index * 100:F1}%";

        // A roster in ONE set has nothing left to vary — pinned so a later change that quietly gave the plain
        // set a matchup effect (and so re-created a single best answer) is caught here.
        Assert.True(single.Index <= 0.02, $"a single-set endgame should have no gear variance left\n{report}");
        // …and the shelf must put a real share back. This is the success metric for the counter line.
        Assert.True(shelf.Index >= 0.04,
            $"the endgame shelf stopped differentiating — gear has re-converged\n{report}");
    }

    [Fact]
    public void NoEndgameLoadoutIsTheBestBuy()
    {
        // "A collection, not a purchase" made falsifiable. Each tier-3 loadout is played against the SAME
        // opponents; if any of them had a materially better average win rate, the shelf would collapse back
        // into one correct answer and every other charm would be dead stock.
        const int n = 900;
        var pool = Pool.Value;
        var rates = new double[EndgameShelf.Length];
        for (var k = 0; k < EndgameShelf.Length; k++)
        {
            var wins = new double[n];
            var loadout = EndgameShelf[k];
            Parallel.For(0, n, i =>
            {
                // The same (ga, gb, levels, opponent loadout) for every k — only OUR charm changes.
                var rng = Rng(41_000, i);
                var ga = pool[rng.Next(pool.Length)];
                var gb = pool[rng.Next(pool.Length)];
                var la = rng.Next(8, 13);
                var lb = rng.Next(8, 13);
                var ob = EndgameShelf[rng.Next(EndgameShelf.Length)];
                wins[i] = WinRate(ga, la, loadout, gb, lb, ob, 48, rng, Countered);
            });
            rates[k] = wins.Average();
        }
        var report = string.Join("  ", EndgameShelf.Select((l, k) =>
            $"{GearTiers[l][2]} {rates[k] * 100:F1}%"));

        // MEASURED: the four sit within about a point of each other. The band is deliberately wider than the
        // measurement so this is a guard against a DOMINANT pick appearing, not a re-tuning treadmill.
        Assert.True(rates.Max() - rates.Min() <= 0.05,
            $"one endgame loadout is now the best buy regardless of matchup — the shelf has a right answer\n{report}");
        // …and none of them is a trap either: a charm nobody should ever buy is dead treasury income.
        Assert.True(rates.Min() >= 0.45, $"an endgame loadout is a trap pick\n{report}");
    }

    [Fact]
    public void TheWildcardMakesFightsLESSPreDecided_WhichNothingElseInThisGameDoes()
    {
        // Finding 3 in the class summary says every lever tried so far — gear, level, innate procs — makes the
        // pre-decided rate WORSE, because each is an asymmetry uncorrelated with breeding and so stacks on top
        // of it. The wildcard is the first lever that is not an asymmetry at all: it widens the wearer's own
        // damage roll around an UNCHANGED mean, so it buys no edge for either side and can only blur the
        // result. It is the one thing measured here that moves the number the right way.
        //
        // MEASURED, one population, everyone in the same tier-3 loadout so gear contributes no asymmetry:
        //     plain charm  pre-decided 39.4%   competitive 7.0%   entropy share 32.6%
        //     Chaos Prism  pre-decided 36.1%   competitive 8.2%   entropy share 34.9%
        // Small, and it should be: the roll is drawn afresh on every blow, so dozens of exchanges average most
        // of it away long before the health bars run out — the same reason finding 2 gives for the resolver
        // being hypersensitive in the first place. A bigger span buys more, at the cost of a fight that reads
        // as arbitrary; ±35% is where it still reads as a swing rather than a shrug.
        var flat = PreDecided(1200, 96, loadout: 3, label: 43_000);
        var wild = PreDecided(1200, 96, loadout: 7, label: 43_500);
        var report = $"plain charm {flat * 100:F1}% pre-decided, Chaos Prism {wild * 100:F1}%";

        Assert.True(wild < flat, $"the wildcard stopped making fights less pre-decided\n{report}");
    }

    /// <summary>Share of matchups whose winner the seed no longer gets a say in, with BOTH sides in the same
    /// loadout — so the only thing separating the two readings is what that loadout does.</summary>
    private static double PreDecided(int n, int seeds, int loadout, int label)
    {
        var pool = Pool.Value;
        var ps = new double[n];
        Parallel.For(0, n, i =>
        {
            var rng = Rng(label, i);
            var ga = pool[rng.Next(pool.Length)];
            var gb = pool[rng.Next(pool.Length)];
            var la = rng.Next(8, 13);
            var lb = rng.Next(8, 13);
            ps[i] = WinRate(ga, la, loadout, gb, lb, loadout, seeds, rng, Countered);
        });
        return ps.Count(p => p >= 0.98 || p <= 0.02) / (double)n;
    }

    [Fact]
    public void BreedingKeepsItsPrimacyWithCountersOn()
    {
        // The ordering ratchet, re-measured under the counter rules. Counters are an asymmetry UNCORRELATED
        // with breeding, so the risk they carry is the same one gear and level already carry: stacking on top
        // of breeding until breeding is no longer the largest lever. It must survive the flip.
        //
        // MEASURED with counters on, 4000 x 48: breeding 38.5%, gear 12.4%, level 6.7%, entropy 30.3%,
        // interactions 12.1% — against 41.2% / 11.0% / 6.9% / 30.4% / 10.4% for the same band with them off.
        // The counter shows up as INTERACTION (10.4% → 12.1%), which is exactly what it is: a term that
        // depends on gear and breeding jointly and on neither alone.
        var split = Decompose(2500, 48, loLevel: 8, hiLevel: 12, gearBag: [0, 1, 2, 3, 4, 5, 6],
            label: 42_000, config: Countered);
        var report = split.Report("counters on, levels 8-12, gear none/t1/t2/t3 + 3 counters");

        Assert.True(split.Breeding >= 0.30, $"breeding was demoted by the counter line\n{report}");
        Assert.True(split.Breeding > split.Gear + 0.10, $"gear caught up with breeding\n{report}");
        Assert.True(split.Breeding > split.Level + 0.10, $"level caught up with breeding\n{report}");
    }
}
