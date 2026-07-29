using ArkadeHeroes.Core.Equipment;
using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Heroes;

namespace ArkadeHeroes.Core.Combat;

/// <summary>What a hero IS, as one of three build shapes — the key a counter item keys off.</summary>
public enum CombatShape
{
    /// <summary>Hits hardest: Attack/Magic lead the build.</summary>
    Offense = 0,

    /// <summary>Hardest to kill: Defense/MaxHp lead the build.</summary>
    Bulk = 1,

    /// <summary>Acts first and crits most: Speed/Crit lead the build.</summary>
    Tempo = 2,
}

/// <summary>
/// The COUNTER system: gear whose worth depends on WHO you are fighting, so the endgame has no single best
/// loadout and a played-out roster keeps a reason to own several sets.
///
/// The problem it exists to solve, measured by <c>CombatAttributionProbe</c>: gear is a one-off permanent
/// purchase, so a mature economy converges on the one tier-3 set and gear stops DIFFERENTIATING — its
/// variance share on a fully-geared roster is 0.0%. Not weak (a tier-3 set still beats its own naked twin
/// 96.5% of the time); uniform. A counter turns the tier-3 decision from "buy the set" into "bring the right
/// set", which is a COLLECTION rather than a purchase. Measured, that lifts gear's TOTAL-effect share of a
/// fully-geared roster's outcome variance from 0.0% to 9.3%, and it does it WITHOUT making gear stronger:
/// the naked-twin figure is unmoved at 96.4% and tier-3-over-tier-2 at 82.7% (from 82.4%).
///
/// TWO MEASUREMENTS DECIDED THE DESIGN, and both contradicted the obvious first guess:
///
/// 1. THE ELEMENT RING IS THE WRONG KEY, even though it is the counter mechanism the game already has.
///    Its SURFACE is too thin: the eight elements are near-uniform in a bred population, so a random pair is
///    ring-NEUTRAL 75.0% of the time (measured: 12.44% attacker-strong, 12.57% attacker-weak, 74.99%
///    neutral over 200 000 bred gen-3 pairs). Gear keyed on the ring would therefore be inert in three
///    fights out of four. And where it DOES fire it is already near-decisive rather than a tilt: genetic
///    twins differing ONLY in the element byte hand the ring-advantaged side 87.3% of seeds — between one
///    extra level (73.4%) and a whole tier-3 set (96.8%). So the ring cannot be leaned on harder either;
///    it is already a switch, and widening it would only raise the pre-decided rate.
///
/// 2. SHAPE HAS FULL SURFACE AND SPLITS EVENLY. Every hero has one, so every matchup keys, and over a bred
///    gen-3 pool at level 10 the split is 34.3% Offense / 32.8% Bulk / 33.0% Tempo with the three axes
///    essentially uncorrelated (r = -0.00, 0.03, 0.17). That is a real rock-paper-scissors surface.
///
/// The classifier reads the hero's UNEQUIPPED build on purpose. Folding gear in looks tempting — counter the
/// armour they are actually wearing — but it collapses the surface it depends on: measured, putting the pool
/// in the tier-3 set makes 54.5% of it read Bulk and shrinks Tempo to 9.7%, so one counter would dominate and
/// the endgame would re-converge on it. Reading the naked build keeps the split near even at every level and
/// every tier, keeps the answer PRINTABLE ON THE HERO CARD, and makes shape a thing you BREED for rather
/// than a thing you buy — which is the ordering the game is balanced around.
///
/// Everything here is a pure function of (genome, level, config), so a replay recomputes it identically and
/// <c>FairnessAudit</c> still verifies. Gated by <see cref="CombatConfig.GearCounters"/> (default OFF).
/// </summary>
public static class CombatShapes
{
    /// <summary>The hero's shape — the axis furthest above its PAR share of the build (see
    /// <see cref="GearCounterRules"/>). Equipment is deliberately NOT read; see the class note.</summary>
    public static CombatShape Of(Genome genome, int level, GameConfig? config = null)
        => Of(StatBlock.ComputeFor(genome, level), (config ?? GameConfig.Default).Combat.CountersOrDefault);

    /// <summary>Scale-free by construction: each axis is scored as its SHARE of the three-axis total, so
    /// doubling every stat leaves the shape alone and the classification barely drifts across levels 1..50.
    /// Ties fall Offense → Bulk → Tempo, so the result is total and deterministic.</summary>
    public static CombatShape Of(StatBlock s, GearCounterRules rules)
    {
        double offense = Math.Max(s.Attack, s.Magic);
        double bulk = s.Defense + s.MaxHp / 8.0;
        double tempo = s.Speed + s.CritPercent;
        var sum = offense + bulk + tempo;   // > 0 always: MaxHp alone floors at 30

        var o = offense / sum / rules.OffenseShare;
        var b = bulk / sum / rules.BulkShare;
        var t = tempo / sum / rules.TempoShare;
        return o >= b && o >= t ? CombatShape.Offense : b >= t ? CombatShape.Bulk : CombatShape.Tempo;
    }

    /// <summary>The shape a counter for <paramref name="countered"/> is itself WEAK to — one step round the
    /// Offense → Bulk → Tempo → Offense cycle. The cycle is what stops any one counter being the best pick:
    /// against a pool split ~evenly three ways, every counter is strong in about a third of matchups and
    /// weak in about a third, so its AVERAGE worth is zero and only the matchup decides.</summary>
    public static CombatShape WeakTo(CombatShape countered) => (CombatShape)(((int)countered + 1) % 3);

    /// <summary>
    /// The damage multiplier <paramref name="gear"/> earns against an <paramref name="opponent"/> of this
    /// shape: 1 + Edge where the gear counters it, 1 - Edge where <see cref="WeakTo"/> says it is answered,
    /// and exactly 1.0 otherwise (and always exactly 1.0 with the flag off, so a replay under
    /// <see cref="CombatConfig.Default"/> is untouched).
    ///
    /// Counters ADD across slots, which is why <see cref="ItemCatalog"/> puts them on the trinket alone: at
    /// the shipped Edge of 0.20 a single counter is a TILT — measured on the same pairs at level 10, the right
    /// charm wins 60.9%, the plain charm 50.3% and the ANSWERED charm 37.3%, a 23.6-point swing that still
    /// loses four times in ten — whereas three stacked slots would be ±60% and a switch. The floor keeps the
    /// product positive if a later catalog does stack them.
    /// </summary>
    public static double Multiplier(IReadOnlyList<Item> gear, CombatShape opponent, GameConfig config)
    {
        if (!config.Combat.GearCounters) return 1.0;
        var edge = config.Combat.CountersOrDefault.Edge;
        var multiplier = 1.0;
        foreach (var item in gear)
        {
            if (item.Counters is not { } counters) continue;
            if (counters == opponent) multiplier += edge;
            else if (WeakTo(counters) == opponent) multiplier -= edge;
        }
        return Math.Max(0.1, multiplier);
    }

    /// <summary>How wide the wearer's own damage roll swings, in whole percent either side of 1.0 — the
    /// engine's stock ±10% plus every equipped WILDCARD item's <see cref="Item.VarianceBonus"/>.
    ///
    /// This is the SYMMETRIC, mean-preserving variance lever, and it is the opposite trade from a counter: it
    /// buys no edge at all, it only makes the fight less certain. That is worth paying for as the underdog
    /// and worth avoiding as the favourite, which is a real choice rather than a bigger number. It is also
    /// the only lever measured in <c>CombatAttributionProbe</c> that lowers the PRE-DECIDED rate rather than
    /// raising it (39.4% → 36.1% with both sides wearing it), because it is the only one that is not an
    /// asymmetry: gear, levels and innate procs all stack on top of breeding, whereas this blurs. With no
    /// wildcard equipped — or the flag off — this is exactly 10, so the engine draws <c>Next(21)</c> exactly
    /// as it always has and no existing replay can shift by a single roll.</summary>
    public static int VarianceSpan(IReadOnlyList<Item> gear, GameConfig config)
    {
        if (!config.Combat.GearCounters) return CombatConfig.BaseVarianceSpan;
        var span = CombatConfig.BaseVarianceSpan;
        foreach (var item in gear) span += item.VarianceBonus;
        return Math.Clamp(span, 0, 90);   // 90 keeps the low end of the roll above zero damage
    }
}
