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
    int DeathMatchFeeMultiplier, // death-match fee = this × MatchFee (classic permadeath)
    int AbsorbFeeMultiplier,     // death-match fee = this × MatchFee (absorb mode, costs more)
    int BreedFeeDoublingCap,    // breed-fee doubling cap (2^min(breeds, cap))
    int MatchmakingTake,        // suggested-opponents page size
    int SeasonLengthDays = 14,  // ranked-ladder season length (days); the current season is time-derived
    // ── Daily engagement loop (sats faucet: base + per-quest bonus, streak-scaled) ──
    long DailyBaseSats = 50,          // paid just for claiming (login hook / sybil floor)
    long DailyQuestBonusSats = 150,   // per completed daily quest
    int DailyQuestsPerDay = 3,        // quests offered per day (rotated from the catalog)
    int DailyStreakStepPct = 10,      // multiplier added per consecutive day
    int DailyStreakCapPct = 100,      // multiplier cap (day 11+ = ×2)
    // ── Season prize pool (house-funded pot: base + a slice of staked-match fees, split 60/30/10) ──
    long SeasonPotBaseSats = 25_000,  // guaranteed base pot each season (treasury-funded)
    int SeasonFeeAccrualPct = 20,     // % of each staked match's fees added to the season pot
    // ── Marketplace listing fee (treasury capture on secondary trades) ──
    long OfferListingFeeSats = 0,     // flat sats the seller pays the treasury to list an offer; 0 = disabled
    // ── Unique-name registry (treasury sats sink: claim a custom hero name) ──
    long HeroRenameFeeSats = 0,       // flat sats to claim a unique hero name; 0 = free
    // ── Tournaments (house rake on the buy-in pot → treasury) ──
    int TournamentRakePct = 10,       // % of the pot the house keeps; the rest splits to the podium
    // ── Faucet governor reserve floor (a permanent treasury reserve the daily faucet must never drain below) ──
    long TreasuryReserveFloorSats = 0) // 0 = no floor (daily clamps to the full balance, as before)
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
        DeathMatchFeeMultiplier: Leveling.DeathMatchFeeMultiplier,
        AbsorbFeeMultiplier: Leveling.AbsorbFeeMultiplier,
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

/// <summary>How a fighter picks its move each turn. <see cref="Greedy"/> always maxes expected damage
/// (the original v1 behaviour); <see cref="Tactical"/> also opens with a buff, softens a durable target
/// with a debuff, and heals when hurt — so status skills are actually worth casting. Both are fully
/// deterministic (pure functions of fighter state), so replays still verify.</summary>
public enum CombatSelectionPolicy { Greedy, Tactical }

/// <summary>
/// Combat tuning. Everything the deterministic battle depends on lives here, so a fight is a pure
/// function of (heroes, seed, this config): the turn cap, element ring + crit + armor constants, the
/// level at which each extra skill is learned, the status-effect magnitudes + stack cap, and the
/// move-selection policy. Client and server share <see cref="Default"/> at compile time, so both
/// resolve identically; versioning a live corpus later means pinning the config that produced each
/// match (see <see cref="GameConfig"/>).
/// </summary>
public sealed record CombatConfig(
    int MaxTurns, double ElementStrong, double ElementWeak, double CritMultiplier, double ArmorConstant,
    // Skill unlock gating — the level at which a hero learns its gene-A skill, gene-B skill, and Elemental Burst.
    int GeneSkillALevel, int GeneSkillBLevel, int BurstLevel,
    // Status-effect magnitudes: per-stack Focus (attack/magic up) and DefenseBreak (defense down)
    // fractions, the shared stack cap, and the DrainHalf heal as a fraction of the damage dealt.
    double FocusPerStack, double DefenseBreakPerStack, int MaxEffectStacks, double DrainFraction,
    // Move selection: the policy, and (Tactical only) the HP% at/below which a hero prefers a drain skill.
    CombatSelectionPolicy SelectionPolicy, int HealHpThresholdPercent,
    // F5: when true, move selection weights the element multiplier (type advantage) into the
    // expected-damage score — the true EV term the resolver already applies. DEFAULT FALSE, so
    // Default stays byte-identical and every existing replay verifies unchanged; the flip is a
    // separate coordinated client+server release. Optional param → no positional ctor breaks.
    bool ElementAwareSelection = false,
    // Genome-derived innate abilities: when true, a hero's EXPRESSED COSMETIC traits (the six
    // non-affinity categories, otherwise combat-inert) grant a small capped damage nudge via
    // Traits.InnateModifier — so rarity/breeding start to matter in the fight, not just on the card.
    // DEFAULT FALSE (same discipline as ElementAwareSelection): Default stays byte-identical and every
    // existing replay verifies unchanged; flipping it on is a coordinated client+server release.
    bool InnateAbilities = false)
{
    public static CombatConfig Default { get; } = new(
        MaxTurns: 60, ElementStrong: 1.3, ElementWeak: 0.75, CritMultiplier: 1.5, ArmorConstant: 25.0,
        // gene-A from level 1 so every hero has a second move immediately (no more Strike-only starters);
        // gene-B at 6 and Elemental Burst at 9 keep progression milestones.
        GeneSkillALevel: 1, GeneSkillBLevel: 6, BurstLevel: 9,
        FocusPerStack: 0.12, DefenseBreakPerStack: 0.12, MaxEffectStacks: 3, DrainFraction: 0.5,
        SelectionPolicy: CombatSelectionPolicy.Tactical, HealHpThresholdPercent: 45,
        ElementAwareSelection: false,
        InnateAbilities: false);
}
