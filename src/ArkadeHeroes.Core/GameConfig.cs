using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Progression;

namespace ArkadeHeroes.Core;

/// <summary>
/// The single source of truth for every tunable game-balance value — server-owned,
/// published, and (for the verification-critical subset) client-propagated.
///
/// Fields split into two MUTABILITY CLASSES with different propagation rules:
///
/// • <b>HOT</b> (economy: fees, cooldowns, matchmaking) — server-enforced once and never
///   re-verified by a client, so they are read LIVE from the current config and may change
///   at any time with no retroactive effect.
///
/// • <b>PINNED</b> (verification-critical: genome derivation, rarity/affinity, combat, the
///   leveling curve — grown into this record as those methods are threaded) — clients
///   recompute these against immutable on-chain assets and signed receipts, so they must
///   NOT be read live. Each re-verifiable artifact stamps the <see cref="Version"/> it was
///   created under, and verification resolves the config for that stamped version. That
///   keeps an old artifact verifiable under its own config forever, regardless of retunes.
///
/// <see cref="Default"/> reproduces today's exact compile-time constants (referencing the
/// named consts where they exist, so those fields are byte-identical by construction), so a
/// caller that passes no config — or the pre-version-registry state where only Version 0
/// exists — behaves exactly as before this record existed.
/// </summary>
public sealed record GameConfig(
    // Identifies the PINNED subset ONLY: bumped when a pinned field changes, not a hot one.
    // Stamped into every re-verified artifact so verification resolves the right config.
    int Version,

    // ── PINNED (verification-critical; stamped per-artifact + resolved by version) ──
    AbsorbOdds Absorb,          // absorb death-match odds (VerifyAbsorb recomputes under these)
    GeneConfig Gene,            // GeneMixer mutation thresholds (breed genome derivation → VerifyBreeding)
    byte FusionConcentrateThreshold, // Fusion.Fuse concentrate probability (merge genome → VerifyMerge)
    SterilityChances Sterility, // per-tier sterile chance (rarity-derived breeding cap)
    RarityBands Rarity,         // trait tier cutoffs + weights (rarity, fusion pick, sterility tier)
    AffinityBonuses Affinity,   // per-tier combat affinity bonus + cap (BattleEngine → VerifyMatch)
    XpCurve Curve,              // leveling curve + max level (Leveling.Apply → ReplayLevel folds it)

    // ── HOT (economy — read live, never stamped) ──
    BreedingPolicy Breeding,    // composed: CooldownBaseUnit (breeding cooldown gate)
    long BreedingFeeSats,       // flat breed fee (escalated by BreedFeeDoublingCap)
    long MergeFeeSats,          // flat merge fee
    long MatchFeeBaseSats,      // per-character staked-match fee base
    long MatchFeePerLevel,      // per-character staked-match fee per level
    int BreedFeeDoublingCap,    // breed-fee doubling cap (2^min(breeds, cap))
    int MatchmakingTake)        // suggested-opponents page size
{
    /// <summary>Today's exact constants — the pre-config behavior and the seed of the version registry (Version 0).</summary>
    public static GameConfig Default { get; } = new(
        Version: 0,
        Absorb: AbsorbOdds.Default,
        Gene: GeneConfig.Default,
        FusionConcentrateThreshold: 217,
        Sterility: SterilityChances.Default,
        Rarity: RarityBands.Default,
        Affinity: AffinityBonuses.Default,
        Curve: XpCurve.Default,
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
