namespace ArkadeHeroes.Shared;

// ── Players ────────────────────────────────────────────────────────────────

public record RegisterPlayerRequest(string Name);

public record PlayerDto(
    string PlayerId,
    string Name,
    string ArkadeAddress,
    long BalanceSats,
    bool StarterClaimed,
    string? Token = null);

// ── Heroes ─────────────────────────────────────────────────────────────────

public record StatsDto(
    int MaxHp, int Attack, int Magic, int Defense,
    int Speed, int Luck, int CritPercent, int DodgePercent);

public record SkillDto(
    string Id, string Name, int Power, int Accuracy,
    string Scaling, string? Element, int CooldownTurns, string Effect);

/// <summary>Commit–reveal audit trail: enough to re-derive the genome/match seed client-side.</summary>
public record ProvenanceDto(
    string? CommitmentHex, string? ServerSeedHex, string? PlayerNonce, string? EntropyHex);

public record HeroDto(
    string Id,
    string Name,
    string OwnerId,
    string GenomeHex,
    int Generation,
    string Element,
    int Level,
    long Xp,
    long XpToNext,
    StatsDto Stats,
    IReadOnlyList<SkillDto> Skills,
    IReadOnlyDictionary<string, string> Equipment,
    int BreedCount,
    DateTimeOffset? BreedCooldownUntil,
    string? ParentAId,
    string? ParentBId,
    string? AssetId,
    string? MintArkTxId,
    ProvenanceDto? Provenance);

public record StarterResponse(IReadOnlyList<HeroDto> Heroes);

// ── Breeding (two-phase commit–reveal) ─────────────────────────────────────

public record BreedCommitRequest(string ParentAId, string ParentBId);

public record BreedCommitResponse(string BreedingId, string CommitmentHex, long FeeSats);

public record BreedRevealRequest(string Nonce);

public record BreedRevealResponse(
    HeroDto Hero, string ServerSeedHex, string EntropyHex, string FeePaymentRef);

// ── Matches (two-phase commit–reveal, optional wager escrow) ───────────────

public record OpenMatchRequest(string ChallengerHeroId, string DefenderHeroId, long WagerSats = 0);

public record OpenMatchResponse(string MatchId, string CommitmentHex, long WagerSats, string Status);

public record FightRequest(string Nonce);

public record BattleEventDto(
    int Turn, string ActorId, string TargetId, string Kind, string SkillId,
    int Damage, bool Crit, int Healed, int TargetHpAfter, string? Note);

public record BattleResultDto(
    string WinnerId, string LoserId, int Turns,
    IReadOnlyList<BattleEventDto> Events, int WinnerRemainingHp, int WinnerMaxHp);

public record FightResponse(
    BattleResultDto Result,
    string ServerSeedHex,
    string EntropyHex,
    long ChallengerXpAward,
    long DefenderXpAward,
    HeroDto ChallengerHero,
    HeroDto DefenderHero,
    // Pre-fight snapshots: exactly what the battle engine saw, so a client can
    // rebuild both heroes and replay the fight to verify the outcome.
    HeroDto ChallengerSnapshot,
    HeroDto DefenderSnapshot,
    // Wager settlement: 0 for friendly matches.
    long WagerSats = 0,
    long WinnerPayoutSats = 0);

public record MatchDto(
    string MatchId,
    string ChallengerHeroId,
    string DefenderHeroId,
    string Status,
    string CommitmentHex,
    BattleResultDto? Result,
    long WagerSats = 0,
    string? DefenderPlayerId = null);

// ── Hero transfer ──────────────────────────────────────────────────────────

public record TransferRequest(string ToPlayerId);

public record TransferResponse(HeroDto Hero, string ArkTxId);

// ── Items / equipment ──────────────────────────────────────────────────────

public record ItemDto(
    string Id, string Name, string Slot,
    int MaxHp, int Attack, int Magic, int Defense, int Speed, int CritPercent,
    long PriceSats);

public record BuyItemResponse(string ItemAssetId, string ArkTxId, long BalanceSats, ulong UnitsHeld);

public record EquipRequest(string ItemId);

public record EquipResponse(HeroDto Hero);

public record UnequipRequest(string Slot);

// ── Chain / misc ───────────────────────────────────────────────────────────

public record ChainInfoDto(
    string Mode, string Network, string TreasuryAddress, string? SpeciesAssetId,
    string? EmulatorSignerKey = null);

public record ErrorResponse(string Error);
