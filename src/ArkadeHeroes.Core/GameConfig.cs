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

    // ── PINNED (composed; pinning wired as later tasks stamp/resolve it) ──
    AbsorbOdds Absorb,          // absorb death-match odds (VerifyAbsorb recomputes under these)

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
        Breeding: BreedingPolicy.Default,
        BreedingFeeSats: 1_000,
        MergeFeeSats: 1_000,
        MatchFeeBaseSats: Leveling.MatchFeeBaseSats,
        MatchFeePerLevel: Leveling.MatchFeePerLevel,
        BreedFeeDoublingCap: BreedingPolicy.FeeDoublingCap,
        MatchmakingTake: 10);
}
