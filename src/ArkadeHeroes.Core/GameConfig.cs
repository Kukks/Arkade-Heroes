using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Progression;

namespace ArkadeHeroes.Core;

/// <summary>
/// The single source of truth for every tunable game-balance value. The server builds one from
/// <c>GameOptions</c> and threads it through the deterministic methods; the client shares
/// <see cref="Default"/> at COMPILE time, so a client re-verifying a genome/fight/level under
/// Default already matches what the server computed — no runtime config propagation is needed
/// pre-launch. (Per-artifact version pinning becomes necessary only at launch, when a persisted
/// hero corpus must survive a runtime retune; that design is captured in the plan for then.)
///
/// <see cref="Default"/> reproduces today's exact constants (referencing the named consts where
/// they exist, byte-identical by construction), so passing no config behaves exactly as before.
/// The economy values are <c>GameOptions</c>-tunable; the rest are edited here and recompiled.
/// </summary>
public sealed record GameConfig(
    // Verification-critical (genome / rarity / affinity / combat / curve) — client and server
    // share these via Default at compile time, so both recompute identically.
    AbsorbOdds Absorb,          // absorb death-match odds (VerifyAbsorb recomputes under these)
    GeneConfig Gene,            // GeneMixer mutation thresholds (breed genome derivation → VerifyBreeding)
    byte FusionConcentrateThreshold, // Fusion.Fuse concentrate probability (merge genome → VerifyMerge)
    SterilityChances Sterility, // per-tier sterile chance (rarity-derived breeding cap)
    RarityBands Rarity,         // trait tier cutoffs + weights (rarity, fusion pick, sterility tier)
    AffinityBonuses Affinity,   // per-tier combat affinity bonus + cap (BattleEngine → VerifyMatch)
    XpCurve Curve,              // leveling curve + max level (Leveling.Apply → ReplayLevel folds it)
    CombatConfig Combat,        // BattleEngine multipliers + turn cap (fight → VerifyMatch replay)

    // Economy — server-enforced only, not client-verified (GameOptions-tunable at runtime).
    BreedingPolicy Breeding,    // composed: CooldownBaseUnit (breeding cooldown gate)
    long BreedingFeeSats,       // flat breed fee (escalated by BreedFeeDoublingCap)
    long MergeFeeSats,          // flat merge fee
    long MatchFeeBaseSats,      // per-character staked-match fee base
    long MatchFeePerLevel,      // per-character staked-match fee per level
    int BreedFeeDoublingCap,    // breed-fee doubling cap (2^min(breeds, cap))
    int MatchmakingTake)        // suggested-opponents page size
{
    /// <summary>Today's exact constants — shared by client and server at compile time.</summary>
    public static GameConfig Default { get; } = new(
        Absorb: AbsorbOdds.Default,
        Gene: GeneConfig.Default,
        FusionConcentrateThreshold: 217,
        Sterility: SterilityChances.Default,
        Rarity: RarityBands.Default,
        Affinity: AffinityBonuses.Default,
        Curve: XpCurve.Default,
        Combat: CombatConfig.Default,
        Breeding: BreedingPolicy.Default,
        BreedingFeeSats: 1_000,
        MergeFeeSats: 1_000,
        MatchFeeBaseSats: Leveling.MatchFeeBaseSats,
        MatchFeePerLevel: Leveling.MatchFeePerLevel,
        BreedFeeDoublingCap: BreedingPolicy.FeeDoublingCap,
        MatchmakingTake: 10);
}

/// <summary>GeneMixer's mutation thresholds — selector byte ≥ threshold triggers a mutation.</summary>
public sealed record GeneConfig(byte RegionMutationThreshold, byte TraitMutationThreshold)
{
    /// <summary>Region crossover mutation at ≥ 248 (8/256); per-category trait mutation at ≥ 250 (~2.3%).</summary>
    public static GeneConfig Default { get; } = new(248, 250);
}

/// <summary>Per-rarity-tier sterile chance (percent). Common (incl. all gen-0) is always fertile.</summary>
public sealed record SterilityChances(int Legendary, int Epic, int Rare, int Uncommon)
{
    public static SterilityChances Default { get; } = new(50, 30, 15, 5);
}

/// <summary>Trait rarity: gene-value cutoffs per tier + the scoring weight of each tier (a plain 0 gene weighs 0).</summary>
public sealed record RarityBands(
    byte LegendaryCutoff, byte EpicCutoff, byte RareCutoff, byte UncommonCutoff,
    int LegendaryWeight, int EpicWeight, int RareWeight, int UncommonWeight, int CommonWeight)
{
    public static RarityBands Default { get; } = new(255, 253, 241, 206, 50, 20, 8, 3, 1);
}

/// <summary>Per-tier combat affinity bonus and the total cap — an expressed affinity nudges damage, never trumps.</summary>
public sealed record AffinityBonuses(
    double Legendary, double Epic, double Rare, double Uncommon, double Common, double Cap)
{
    public static AffinityBonuses Default { get; } = new(0.030, 0.020, 0.012, 0.006, 0.002, 0.05);
}

/// <summary>The XP-to-next-level curve — XpToNext(level) = Base + Coefficient·level^Exponent — and the level ceiling.</summary>
public sealed record XpCurve(long Base, double Coefficient, double Exponent, int MaxLevel)
{
    public static XpCurve Default { get; } = new(80, 45, 1.35, 50);
}

/// <summary>Combat tuning: the turn cap, element ring multipliers, crit multiplier, and the damage-softening armor constant.</summary>
public sealed record CombatConfig(int MaxTurns, double ElementStrong, double ElementWeak, double CritMultiplier, double ArmorConstant)
{
    public static CombatConfig Default { get; } = new(60, 1.3, 0.75, 1.5, 25.0);
}
