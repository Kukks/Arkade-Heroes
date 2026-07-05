namespace ArkadeHeroes.Shared;

// ── Players ────────────────────────────────────────────────────────────────

/// <summary>
/// Registration binds the player to THEIR wallet's address — keys never leave the
/// client. <see cref="LoginPubKeyHex"/> (optional) is the wallet's stable login
/// key, registered so the player can later resume by signing a challenge with it
/// ("sign in with your wallet" after a restore). When it is supplied, the wallet
/// must prove possession: sign a fresh <c>/api/players/login-challenge</c> nonce
/// and pass it here (<see cref="NonceHex"/> + <see cref="SignatureHex"/>), so a
/// login key you don't control cannot be registered against your player.
/// </summary>
public record RegisterPlayerRequest(
    string Name, string ArkadeAddress,
    string? LoginPubKeyHex = null, string? NonceHex = null, string? SignatureHex = null);

/// <summary>A fresh single-use nonce to sign for wallet login.</summary>
public record LoginChallengeResponse(string NonceHex);

/// <summary>Prove control of a registered login key by signing the challenge's digest (BIP340).</summary>
public record LoginRequest(string LoginPubKeyHex, string NonceHex, string SignatureHex);

/// <summary>A fee/stake invoice: pay this exact treasury address from your own wallet.</summary>
public record FeeInvoiceDto(string InvoiceId, string PayToAddress, long AmountSats, string Memo);

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

/// <summary>Mode "invoice" (fee invoice, treasury mint) or "covenant" (parents+fee escrow deposit, emulator-enforced mint).</summary>
public record BreedCommitRequest(string ParentAId, string ParentBId, string Mode = "invoice");

/// <summary>In covenant mode <see cref="Invoice"/> is null and <see cref="EscrowAddress"/>/<see cref="EscrowFeeSats"/> are set.</summary>
public record BreedCommitResponse(
    string BreedingId, string CommitmentHex, FeeInvoiceDto? Invoice,
    string? EscrowAddress = null, long EscrowFeeSats = 0);

public record BreedRevealRequest(string Nonce);

public record BreedRevealResponse(
    HeroDto Hero, string ServerSeedHex, string EntropyHex, string FeePaymentRef,
    ProgressionReceiptDto? Receipt = null);

// ── Matches (two-phase commit–reveal, optional wager escrow) ───────────────

/// <summary>Mode "invoice" (server-observed stakes, treasury payout) or "covenant" (emulator-enforced escrow settlement).</summary>
public record OpenMatchRequest(string ChallengerHeroId, string DefenderHeroId, long WagerSats = 0, string Mode = "invoice");

public record OpenMatchResponse(
    string MatchId, string CommitmentHex, long WagerSats, string Status,
    FeeInvoiceDto? StakeInvoice = null,
    // Covenant mode: both players stake by paying this escrow address directly.
    string? EscrowAddress = null,
    long EscrowStakeSats = 0,
    // The challenger's per-character match fee (level-proportional treasury sink),
    // paid before the fight in both modes; null for friendly matches.
    FeeInvoiceDto? MatchFeeInvoice = null);

public record AcceptMatchResponse(MatchDto Match, FeeInvoiceDto? StakeInvoice, string? EscrowAddress = null, long EscrowStakeSats = 0,
    // The defender's per-character match fee, paid before the fight in both modes.
    FeeInvoiceDto? MatchFeeInvoice = null);

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
    long WinnerPayoutSats = 0,
    // Signed, player-held progression fact for this match.
    ProgressionReceiptDto? Receipt = null);

public record MatchDto(
    string MatchId,
    string ChallengerHeroId,
    string DefenderHeroId,
    string Status,
    string CommitmentHex,
    BattleResultDto? Result,
    long WagerSats = 0,
    string? DefenderPlayerId = null);

// ── Hero transfer (client-signed spend; server verifies + confirms) ────────

public record TransferRequest(string ToPlayerId);

public record TransferResponse(HeroDto Hero);

// ── Items / equipment ──────────────────────────────────────────────────────

public record ItemDto(
    string Id, string Name, string Slot,
    int MaxHp, int Attack, int Magic, int Defense, int Speed, int CritPercent,
    long PriceSats);

public record ItemInvoiceResponse(FeeInvoiceDto Invoice);

public record ClaimItemRequest(string InvoiceId);

public record ClaimItemResponse(string ItemAssetId, string ArkTxId, ulong UnitsHeld);

public record EquipRequest(string ItemId);

public record EquipResponse(HeroDto Hero);

public record UnequipRequest(string Slot);

// ── Marketplace: resting item offers (covenant-enforced, buyer-funded) ─────

/// <summary>List one spare unit of an item for sale at a fixed ask (sats).</summary>
public record CreateOfferRequest(string ItemId, long AskSats);

/// <summary>List one of your heroes for sale at a fixed ask (sats).</summary>
public record CreateHeroOfferRequest(string HeroId, long AskSats);

/// <summary>
/// The offer address the seller deposits the item unit (+ carrier dust) into
/// from their own wallet, plus the ask and refund window. Once deposited the
/// offer becomes buyable; the covenant pins the seller as payee.
/// </summary>
public record CreateOfferResponse(
    string OfferId, string OfferAddress, string ItemAssetId,
    long AskSats, long OfferValueSats, long RefundAfterUnixSeconds);

/// <summary>
/// A resting offer in the discovery index (an item unit or a hero). Status:
/// pending → active → closed. <see cref="ItemName"/> carries the display name for
/// either kind (the item name, or the hero name for <c>Kind == "hero"</c>).
/// </summary>
public record OfferDto(
    string OfferId, string SellerId, string ItemId, string ItemName,
    long AskSats, string OfferAddress, string ItemAssetId,
    long OfferValueSats, long RefundAfterUnixSeconds, string Status,
    string Kind = "item", string? HeroId = null);

// ── Chain / misc ───────────────────────────────────────────────────────────

public record ChainInfoDto(
    string Mode, string Network, string TreasuryAddress, string? SpeciesAssetId,
    string? EmulatorSignerKey = null,
    string? GameSignerKey = null,
    string? EmulatorUri = null,
    string? EsploraApiUri = null);

public record ErrorResponse(string Error);
