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

/// <summary>A single expressed or carried trait for display.</summary>
public record TraitDto(string Category, int Value, string Tier);

/// <summary>A hero's rarity: visible tier + score, expressed traits, and carried recessives (breeding potential). All recomputable from the genome.</summary>
public record RarityDto(string Tier, int Score, IReadOnlyList<TraitDto> Expressed, IReadOnlyList<TraitDto> CarriedRecessives);

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
    ProvenanceDto? Provenance,
    RarityDto? Rarity = null,
    bool IsSterile = false,
    string? FancyTitle = null);

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

// ── Merge / fusion (two-phase commit–reveal, escrow-funded) ────────────────

/// <summary>Consume base + sacrifice to mint one trait-concentrated hero. Mode "treasury" (rung 1, server-executed) or "covenant".</summary>
public record MergeCommitRequest(string BaseId, string SacrificeId, string Mode = "treasury");

/// <summary>The player deposits base + sacrifice + the fee into <see cref="EscrowAddress"/> before revealing.</summary>
public record MergeCommitResponse(string MergeId, string CommitmentHex, string EscrowAddress, long FeeSats);

public record MergeRevealRequest(string Nonce);

/// <summary><see cref="EntropyHex"/> is the revealed commit–reveal entropy — the client recomputes Fusion.Fuse from it to audit the mint.</summary>
public record MergeRevealResponse(
    HeroDto Hero, string ServerSeedHex, string EntropyHex, ProgressionReceiptDto? Receipt = null);

// ── Death-match (winner-takes-all, both stake a hero) ──────────────────────

/// <summary>Coarse pre-stake favorability from the challenger's view. LevelGap is signed (theirs − mine).</summary>
public record FavorabilityDto(int LevelGap, string Label);

public record DeathMatchOpenRequest(string ChallengerHeroId, string DefenderHeroId, bool Absorb = false);

/// <summary>One required gear deposit for a death-match stake: the item units matching the hero's loadout-at-open (send Amount unit(s) of AssetId to the escrow alongside the hero).</summary>
public record GearStakeDto(string ItemId, string AssetId, int Amount);

/// <summary>The challenger deposits their hero + <see cref="ChallengerGear"/> into <see cref="EscrowAddress"/>; heed <see cref="Favorability"/> — losing BURNS your hero and forfeits your staked gear.</summary>
public record DeathMatchOpenResponse(string DeathMatchId, string CommitmentHex, string EscrowAddress, FavorabilityDto Favorability, IReadOnlyList<GearStakeDto> ChallengerGear, IReadOnlyList<GearStakeDto> DefenderGear, FeeInvoiceDto? FeeInvoice = null);

/// <summary><see cref="DefenderHero"/> is the challenged hero the defender must stake (covenant deposit needs its asset id), alongside <see cref="DefenderGear"/>.</summary>
public record DeathMatchAcceptResponse(string EscrowAddress, HeroDto DefenderHero, IReadOnlyList<GearStakeDto> DefenderGear, FeeInvoiceDto? FeeInvoice = null);

public record DeathMatchSettleRequest(string Nonce);

/// <summary>The fight result + the burned loser + the audit trail. The pre-fight snapshots let the client replay BattleEngine.Fight and verify the winner (the loser's record is gone after settle).</summary>
public record DeathMatchSettleResponse(
    BattleResultDto Result, string WinnerHeroId, string LoserHeroId,
    HeroDto ChallengerSnapshot, HeroDto DefenderSnapshot,
    string ServerSeedHex, string EntropyHex, ProgressionReceiptDto? Receipt,
    // Absorb mode: on a mint the winner's OLD hero is also gone and a NEW absorbed hero is minted.
    bool Minted = false, int TraitsAbsorbed = 0, string? NewGenomeHex = null, HeroDto? NewHero = null);

/// <summary>One death-match on the discovery list, derived from its session. Status = open (awaiting the defender's stake) | accepted (ready to settle) | resolved.</summary>
public record DeathMatchDto(
    string DeathMatchId, string ChallengerHeroId, string DefenderHeroId,
    string Status, bool Absorb, string? WinnerHeroId);

// ── PvE gauntlet (F1): open (commit + fee) → pay → run (5 ghost waves) ──────

public record GauntletOpenRequest(string HeroId);

public record GauntletOpenResponse(string GauntletId, string CommitmentHex, FeeInvoiceDto FeeInvoice);

public record GauntletRunRequest(string Nonce);

/// <summary>One wave's result for display; the client re-derives the ghost + fight from the seed to verify.</summary>
public record GauntletWaveDto(int Wave, int GhostLevel, bool Won, HeroDto Ghost, BattleResultDto Result);

/// <summary><see cref="HeroSnapshot"/> is the PRE-run hero, so the client replays <c>Gauntlet.Resolve</c>
/// and re-checks the awarded XP (level-10 cap) + item via <c>FairnessAudit.VerifyGauntlet</c>.</summary>
public record GauntletRunResponse(
    int WavesCleared, IReadOnlyList<GauntletWaveDto> Waves, long XpAwarded, int NewLevel,
    string? ItemAwarded, string? ItemAssetId, HeroDto HeroSnapshot,
    string ServerSeedHex, string EntropyHex, ProgressionReceiptDto Receipt);

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
    int Damage, bool Crit, int Healed, int TargetHpAfter, string? Note,
    // The skill's status effect ("Focus" / "DefenseBreak" / "None"), so a replay can narrate the beat.
    string? Effect = null);

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

/// <summary>Everything a spectator needs to REPLAY a resolved match in the arena AND verify it was fair —
/// re-derive the fight from the revealed seed via <c>FairnessAudit.VerifyMatch</c>. Served publicly (no auth),
/// so a match is a shareable, trustlessly-watchable artifact.</summary>
public record MatchReplayDto(
    HeroDto ChallengerSnapshot, HeroDto DefenderSnapshot, BattleResultDto Result,
    string WinnerHeroId, string CommitmentHex, string ServerSeedHex, string EntropyHex, string Nonce);

// ── Team 3v3 squad matches: a positional best-of-3 relay of 1v1 duels ──
public record SquadDuelDto(int Slot, HeroDto Challenger, HeroDto Defender, BattleResultDto Result);
public record SquadResultDto(bool ChallengerWon, int ChallengerWins, int DefenderWins, IReadOnlyList<SquadDuelDto> Duels);

public record OpenSquadMatchRequest(
    IReadOnlyList<string> ChallengerLineup, IReadOnlyList<string> DefenderLineup, long WagerSats = 0, string Mode = "covenant");

public record SquadMatchDto(
    string MatchId, IReadOnlyList<string> ChallengerLineup, IReadOnlyList<string> DefenderLineup,
    long WagerSats, string Status, SquadResultDto? Result);

/// <summary>Everything a spectator needs to REPLAY + verify a resolved squad match (FairnessAudit.VerifySquad).</summary>
public record SquadReplayDto(
    IReadOnlyList<HeroDto> ChallengerLineup, IReadOnlyList<HeroDto> DefenderLineup,
    SquadResultDto Result, string CommitmentHex, string ServerSeedHex, string EntropyHex, string Nonce);

public record SquadOpenResponse(string MatchId, string CommitmentHex, long WagerSats, string Status,
    FeeInvoiceDto? StakeInvoice, string? EscrowAddress, long EscrowStakeSats, FeeInvoiceDto? MatchFeeInvoice);
public record SquadAcceptResponse(SquadMatchDto Match, FeeInvoiceDto? StakeInvoice,
    string? EscrowAddress, long EscrowStakeSats, FeeInvoiceDto? MatchFeeInvoice);
public record SquadResolveResponse(SquadResultDto Result, string ServerSeedHex, string EntropyHex,
    long WinnerPayoutSats, IReadOnlyList<ProgressionReceiptDto> Receipts);

// ── XP-weighted matchmaking: suggested opponents ranked by level proximity ──────
public record OpponentSuggestionDto(
    HeroDto Hero, string OwnerPlayerId, int LevelGap, long XpIfYouWin, long XpIfYouLose,
    // F18: the opponent's realized power and how far it is from yours (percent of the stronger).
    int PowerScore = 0, int PowerGapPercent = 0,
    // F2: coarse level-based favorability — "favored" / "even" / "underdog". An "underdog" line with
    // XpIfYouLose == 0 is the "free shot": nothing to lose, a big conserved swing to win.
    string Favor = "even");

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

/// <summary>Claim a custom, globally-unique name for a hero (a treasury sats sink).</summary>
public record RenameHeroRequest(string Name);

/// <summary>The treasury fee to pay before confirming the rename (0 + null when renaming is free).</summary>
public record RenameHeroResponse(long FeeSats, FeeInvoiceDto? Fee);

// ── Tournaments (a buy-in bracket; buy-ins → treasury, prizes → podium minus the house rake) ──
public record OpenTournamentRequest(string HeroId, long BuyInSats, int Size);
public record JoinTournamentRequest(string HeroId);
public record TournamentEntrantDto(string PlayerId, string HeroId);
public record TournamentDto(string Id, string OpenerPlayerId, long BuyInSats, int Size, int Joined, string Status,
    IReadOnlyList<TournamentEntrantDto> Entrants, string? ChampionHeroId, long ChampionPrizeSats = 0);
/// <summary>A tournament + the buy-in fee-invoice the entrant pays into the treasury (open or join).</summary>
public record TournamentEntryResponse(TournamentDto Tournament, FeeInvoiceDto BuyIn);
public record TournamentMatchDto(int Round, int Index, string AId, string BId, string WinnerId);
/// <summary>A resolved tournament: the final bracket, the revealed seed/entropy (for replay), and the podium prizes.</summary>
public record TournamentResolveResponse(TournamentDto Tournament, IReadOnlyList<TournamentMatchDto> Bracket,
    string ServerSeedHex, string EntropyHex, IReadOnlyList<long> Prizes);

/// <summary>A player's derived accomplishments (from their roster + resolved tournaments) and the badges they've unlocked.</summary>
public record PlayerAchievementsDto(int HeroesOwned, int HeroesBred, int Legendaries, int Fancies, int TournamentsWon,
    IReadOnlyList<string> Badges, IReadOnlyList<string> FancySetsOwned);

/// <summary>Treasury-health telemetry (economy control plane): the finite, fee-funded pot's current balance, what it
/// has paid out by category ("daily"/"season"/"tournament"/"wager"/"squad"), and fees accrued to season pots. Outflow
/// is the insolvency-risk side; per-source inflow + net-issuance is a follow-up. Read-only observability.</summary>
public record EconomyHealthDto(long TreasuryBalanceSats, long TotalInflowSats, long TotalOutflowSats,
    IReadOnlyDictionary<string, long> InflowByTag, IReadOnlyDictionary<string, long> OutflowByTag, long SeasonAccrualSats);

/// <summary>
/// The offer address the seller deposits the item unit (+ carrier dust) into
/// from their own wallet, plus the ask and refund window. Once deposited the
/// offer becomes buyable; the covenant pins the seller as payee.
/// </summary>
public record CreateOfferResponse(
    string OfferId, string OfferAddress, string ItemAssetId,
    long AskSats, long OfferValueSats, long RefundAfterUnixSeconds,
    // Marketplace listing fee (0 = disabled). When set, the seller pays this treasury
    // fee-invoice; the offer stays pending (not buyable) until it clears.
    long ListingFeeSats = 0, FeeInvoiceDto? ListingFee = null);

/// <summary>
/// A resting offer in the discovery index (an item unit or a hero). Status:
/// pending → active → closed. <see cref="ItemName"/> carries the display name for
/// either kind (the item name, or the hero name for <c>Kind == "hero"</c>).
/// </summary>
public record OfferDto(
    string OfferId, string SellerId, string ItemId, string ItemName,
    long AskSats, string OfferAddress, string ItemAssetId,
    long OfferValueSats, long RefundAfterUnixSeconds, string Status,
    string Kind = "item", string? HeroId = null, string? RarityTier = null);

// ── Chain / misc ───────────────────────────────────────────────────────────

/// <summary>
/// The published game-balance config (current version). Flat wire mirror of
/// <see cref="ArkadeHeroes.Core.GameConfig"/> — carries the HOT (economy) values for
/// display plus the current <see cref="Version"/>; the PINNED subset grows here as those
/// paths are threaded, and clients resolve a stamped version via GET /api/config/{version}.
/// </summary>
public record GameConfigDto(
    byte AbsorbChance,
    byte AbsorbContinueChance,
    long BreedingCooldownBaseSeconds,
    long BreedingFeeSats,
    long MergeFeeSats,
    long MatchFeeBaseSats,
    long MatchFeePerLevel,
    int BreedFeeDoublingCap,
    int MatchmakingTake,
    long OfferListingFeeSats,
    long HeroRenameFeeSats,
    int TournamentRakePct)
{
    public static GameConfigDto From(ArkadeHeroes.Core.GameConfig c) => new(
        c.Absorb.AbsorbChance,
        c.Absorb.ContinueChance,
        (long)c.Breeding.CooldownBaseUnit.TotalSeconds,
        c.BreedingFeeSats,
        c.MergeFeeSats,
        c.MatchFeeBaseSats,
        c.MatchFeePerLevel,
        c.BreedFeeDoublingCap,
        c.MatchmakingTake,
        c.OfferListingFeeSats,
        c.HeroRenameFeeSats,
        c.TournamentRakePct);
}

public record ChainInfoDto(
    string Mode, string Network, string TreasuryAddress, string? SpeciesAssetId,
    string? EmulatorSignerKey = null,
    string? GameSignerKey = null,
    string? EmulatorUri = null,
    string? EsploraApiUri = null,
    byte AbsorbChance = 102,
    byte AbsorbContinueChance = 90,
    GameConfigDto? Config = null);

public record ErrorResponse(string Error);
