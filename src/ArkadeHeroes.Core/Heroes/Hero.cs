using ArkadeHeroes.Core.Equipment;
using ArkadeHeroes.Core.Genetics;

namespace ArkadeHeroes.Core.Heroes;

/// <summary>
/// The hero aggregate. On-chain, a hero is an Arkade asset (amount 1) whose
/// genesis metadata commits the genome, generation, and lineage; this class is
/// the server-side working copy of that state plus mutable progression
/// (level, XP, equipment) which stays server-authoritative in v1.
/// </summary>
public class Hero
{
    /// <summary>Canonical id. Once minted on-chain this is the Arkade asset id.</summary>
    public required string Id { get; init; }

    public required string OwnerId { get; set; }

    public required string Name { get; set; }

    public required Genome Genome { get; init; }

    public int Generation { get; init; }

    public string? ParentAId { get; init; }
    public string? ParentBId { get; init; }

    public int Level { get; set; } = 1;
    public long Xp { get; set; }

    public int BreedCount { get; set; }
    public DateTimeOffset? BreedCooldownUntil { get; set; }

    /// <summary>When this hero can next enter the PvE gauntlet (F1) — rate-limits the XP faucet.</summary>
    public DateTimeOffset? GauntletCooldownUntil { get; set; }

    public EquipmentLoadout Equipment { get; } = new();

    /// <summary>Commit–reveal audit trail for this hero's genome derivation (breeding) or genesis entropy.</summary>
    public string? EntropyHex { get; init; }
    public string? ServerSeedHex { get; init; }
    public string? PlayerNonce { get; init; }

    /// <summary>On-chain anchors, set once the hero is minted as an Arkade asset.</summary>
    public string? AssetId { get; set; }
    public string? MintArkTxId { get; set; }

    public bool IsOnBreedCooldown(DateTimeOffset now) =>
        BreedCooldownUntil is { } until && until > now;
}
