using ArkadeHeroes.Core;
using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Progression;

namespace ArkadeHeroes.Server;

public class GameOptions
{
    public const string SectionName = "Game";

    public long BreedingFeeSats { get; set; } = 1_000;

    /// <summary>Flat sats fee to merge two heroes into one — a sats sink on top of the hero burn.</summary>
    public long MergeFeeSats { get; set; } = 1_000;

    /// <summary>Base unit for breeding cooldowns. Short by default so regtest play loops stay fast.</summary>
    public TimeSpan BreedingCooldownBaseUnit { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Fixed 32-byte hex key for signing progression receipts; ephemeral per process when unset.</summary>
    public string? ReceiptKeyHex { get; set; }

    /// <summary>How long after a covenant match opens its escrow refund leaves unlock (player liveness).</summary>
    public TimeSpan WagerEscrowRefundAfter { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Absorb death-match odds — server game config PUBLISHED on /api/chain/info so the client's
    /// VerifyAbsorb recomputes the same outcome. P(absorb happens) ≈ AbsorbChance/256; each further trait
    /// ≈ AbsorbContinueChance/256 (front-loaded → mostly one trait, rarely a full concentrate).</summary>
    public byte AbsorbChance { get; set; } = 102;        // ≈40%
    public byte AbsorbContinueChance { get; set; } = 90;  // ≈35%

    // ── Economy tunables (HOT: server-enforced, never re-verified). Defaults reference the
    //    Core consts so there is a single literal source; an operator overrides via config. ──
    public long MatchFeeBaseSats { get; set; } = Leveling.MatchFeeBaseSats;      // 500
    public long MatchFeePerLevel { get; set; } = Leveling.MatchFeePerLevel;      // 20
    public int BreedFeeDoublingCap { get; set; } = BreedingPolicy.FeeDoublingCap; // 3
    public int MatchmakingTake { get; set; } = 10;

    /// <summary>Projects these options into the Core <see cref="GameConfig"/> the game logic reads (current version).</summary>
    public GameConfig ToGameConfig() => new(
        Absorb: new AbsorbOdds(AbsorbChance, AbsorbContinueChance),
        Gene: GeneConfig.Default,
        FusionConcentrateThreshold: 217,
        Sterility: SterilityChances.Default,
        Rarity: RarityBands.Default,
        Affinity: AffinityBonuses.Default,
        Curve: XpCurve.Default,
        Combat: CombatConfig.Default,
        Breeding: new BreedingPolicy(BreedingCooldownBaseUnit),
        BreedingFeeSats: BreedingFeeSats,
        MergeFeeSats: MergeFeeSats,
        MatchFeeBaseSats: MatchFeeBaseSats,
        MatchFeePerLevel: MatchFeePerLevel,
        BreedFeeDoublingCap: BreedFeeDoublingCap,
        MatchmakingTake: MatchmakingTake);
}
