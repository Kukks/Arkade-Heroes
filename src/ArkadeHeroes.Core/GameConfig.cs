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
