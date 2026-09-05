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
/// <param name="AcceptedTermsVersion">
/// The <see cref="Terms"/> version the player explicitly accepted before this registration, recorded
/// against the new player row in the same call so there is no window where a player exists with no
/// acceptance on file. Null means none was offered (the console client, tests) — registration still
/// succeeds, but nothing is recorded.
/// </param>
public record RegisterPlayerRequest(
    string Name, string ArkadeAddress,
    string? LoginPubKeyHex = null, string? NonceHex = null, string? SignatureHex = null,
    int? AcceptedTermsVersion = null);

/// <summary>Explicitly accept the Terms of Use at a stated version — the deliberate act itself.</summary>
public record AcceptTermsRequest(int Version);

/// <summary>What the server has on file for this player's Terms acceptance, and what it currently requires.</summary>
public record TermsAcceptanceDto(
    int? AcceptedVersion, DateTimeOffset? AcceptedAtUtc, int CurrentVersion, bool AcceptanceRequired);

/// <summary>A fresh single-use nonce to sign for wallet login.</summary>
public record LoginChallengeResponse(string NonceHex);

/// <summary>Prove control of a registered login key by signing the challenge's digest (BIP340).</summary>
public record LoginRequest(string LoginPubKeyHex, string NonceHex, string SignatureHex);

/// <summary>A fee/stake invoice: pay this exact treasury address from your own wallet.</summary>
public record FeeInvoiceDto(string InvoiceId, string PayToAddress, long AmountSats, string Memo);

/// <param name="TermsAcceptedVersion">The <see cref="Terms"/> version this player accepted, per the SERVER's
/// record — the source of truth the browser gate reads, so a cleared cache still can't un-ask the question
/// (nor re-ask one already answered). Null = nothing on file.</param>
public record PlayerDto(
    string PlayerId,
    string Name,
    string ArkadeAddress,
    long BalanceSats,
    bool StarterClaimed,
    string? Token = null,
    int? TermsAcceptedVersion = null,
    DateTimeOffset? TermsAcceptedAtUtc = null);

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
    string? FancyTitle = null,
    /// <summary>When this hero may next enter the gauntlet. The server has always enforced this and
    /// persisted it, but never sent it — so a client could only discover a resting hero by pressing Run
    /// and taking the refusal. <see cref="BreedCooldownUntil"/> has always been here; this is its
    /// missing twin. Trailing and defaulted, so an older client still deserializes.</summary>
    DateTimeOffset? GauntletCooldownUntil = null);

public record StarterResponse(IReadOnlyList<HeroDto> Heroes);

/// <summary>
/// Another hero named by a timeline event — a parent, a burned input, an opponent.
///
/// <para><see cref="Destroyed"/> and <see cref="Name"/> are INDEPENDENT, and that separation is the whole
/// point of the pair. A hero burned in a fusion or a death-match has its record erased at the burn site (its
/// on-chain asset is retired, and a rehydrated ghost would be a fightable, listable hero that no longer
/// exists), so its own row can never answer either question. A durable headstone written at the burn site
/// answers both: the hero is destroyed, and it was called this.</para>
///
/// <para>Name is still deliberately NULLABLE and still never a placeholder — a hero destroyed before
/// headstones existed has genuinely lost its name, and inventing one would put a fact on the page that
/// nothing can stand behind. What changed is that a null name no longer MEANS "destroyed": read
/// <see cref="Destroyed"/> for that, or a nameless-but-living hero would render as a grave.</para>
/// </summary>
public record TimelineHeroRefDto(string HeroId, string? Name, bool Destroyed = false);

/// <summary>
/// A hero that no longer exists — everything the arena can still say about one, read off the headstone
/// written at its burn site.
///
/// This is what <c>GET /api/heroes/{id}</c> cannot serve: heroes are HARD DELETED when they die (a
/// death-match loser, a fusion's inputs, both sides of an absorb), so there is no hero row left to return
/// and no <see cref="HeroDto"/> to shape. A destroyed hero's page is built from this instead.
/// </summary>
/// <param name="Reason">"merge-input" | "deathmatch-loser" | "deathmatch-absorb-winner" |
/// "deathmatch-absorb-loser".</param>
/// <param name="SessionId">The merge or death-match that consumed it — replayable at <c>/watch/{id}</c>
/// when it was a death-match.</param>
/// <param name="ReplacedByHeroId">What rose from it, when anything did. Null for a classic death-match
/// loser, which simply ends.</param>
public record HeroTombstoneDto(
    string HeroId, string Name, string OwnerId, int Generation, int Level, string GenomeHex,
    string Reason, string SessionId, string? ReplacedByHeroId, long DestroyedAtUnixSeconds,
    string? ParentAId = null, string? ParentBId = null);

/// <summary>
/// One thing that happened to a hero, in the one shape the page renders.
///
/// Assembled by the server rather than the browser because the sources disagree about what an event even
/// IS: a receipt files the same fight under both fighters and says nothing about which of them is "you",
/// a squad duel's receipt id is <c>{squadId}:{slot}</c> and names a replay that <c>/watch</c> cannot
/// serve, and a burned hero survives only as an id. Deriving the player-facing line once, server-side,
/// keeps those rules in one testable place instead of scattered through Razor.
/// </summary>
public record HeroTimelineEventDto(
    /// <summary>"born" | "bred" | "fused" | "absorbed" | "bred-with" | "burned" | "duel" | "spar"
    /// | "deathmatch" | "gauntlet" | "trials" | "sold" — what happened, for the icon and the grouping.</summary>
    string Kind,
    /// <summary>When, in unix seconds — or 0 when the moment was never recorded (a gen-0 starter has no
    /// birth timestamp anywhere in the system). Zero sorts first and the UI says so rather than printing
    /// the epoch as if it were a real date.</summary>
    long UnixSeconds,
    /// <summary>The player-facing line.</summary>
    string Summary,
    /// <summary>The other heroes this event names, in the order the summary mentions them.</summary>
    IReadOnlyList<TimelineHeroRefDto> Related,
    /// <summary>The id to link at <c>/watch/{id}</c>, when a replay of THIS event can actually be served
    /// there. Null for everything else — a dead link is worse than no link.</summary>
    string? WatchMatchId = null,
    /// <summary>Sats this event moved, when it moved any (what a sale fetched). 0 otherwise.</summary>
    long Sats = 0,
    /// <summary>"won" | "lost" | null — this hero's side of a fight, when the event was one.</summary>
    string? Outcome = null,
    /// <summary>A second line of detail (the XP swing, the buyer), when there is one worth showing.</summary>
    string? Detail = null);

/// <summary>
/// A hero's full provenance, newest first.
///
/// <see cref="Complete"/> is the honest part. Almost every event here is derived from the progression
/// receipt ledger, which lives in memory: a server restart drops it, and the timeline then legitimately
/// begins mid-life. Sales and lineage are durable and survive. Saying so on the wire lets the page admit
/// the gap instead of presenting a truncated history as a whole one.
/// </summary>
public record HeroTimelineDto(
    string HeroId,
    IReadOnlyList<HeroTimelineEventDto> Events,
    bool Complete,
    string? Caveat = null);

/// <summary>What the starter heroes cost, and the invoice to pay before claiming them. <c>Fee</c> is null
/// when the server charges nothing, in which case the claim can be made straight away.</summary>
public record StarterQuoteResponse(long FeeSats, int HeroCount, FeeInvoiceDto? Fee);

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

// ── Stud service (cross-owner breeding: propose → consent → reveal) ────────

/// <summary>Asks the owner of <see cref="StudHeroId"/> to breed it with <see cref="MyHeroId"/>, offering
/// them <see cref="StudFeeSats"/> for the service (0 = a favour). Nothing is billed until they accept.</summary>
public record StudProposeRequest(string MyHeroId, string StudHeroId, long StudFeeSats = 0);

/// <summary>The sealed proposal. No invoice here on purpose — a proposal the counterparty hasn't accepted
/// costs nothing. <see cref="CommitmentHex"/> is the breed's seed, committed before they consent.</summary>
public record StudProposeResponse(
    string ProposalId, string CommitmentHex, string StudHeroId, string StudOwnerPlayerId, long StudFeeSats);

/// <summary>What an accepted proposal bills the PROPOSER — returned by the stud owner's consent (which is
/// what creates these), and re-readable by either party from <c>GET /api/stud/{id}/invoices</c>, since the
/// accept response lands in the stud owner's browser while the sats are the proposer's to send.
/// <see cref="StudFeeInvoice"/> is null when no stud fee was offered; the breed fee is always billed.</summary>
public record StudAcceptResponse(
    string ProposalId, FeeInvoiceDto BreedFeeInvoice, FeeInvoiceDto? StudFeeInvoice, long StudFeeSats);

public record StudRevealRequest(string Nonce);

/// <summary>The child (minted to the proposer) plus the audit trail — verifiable with the same
/// <c>FairnessAudit.VerifyBreeding</c> recompute as any other breed. <see cref="StudFeePaidSats"/> is what
/// actually reached the stud's owner.</summary>
public record StudRevealResponse(
    HeroDto Hero, string ServerSeedHex, string EntropyHex, long StudFeePaidSats,
    ProgressionReceiptDto? Receipt = null);

/// <summary>One stud proposal on the discovery list. Status = proposed (awaiting consent) | accepted (the
/// proposer may pay + reveal) | declined | completed.</summary>
public record StudProposalDto(
    string ProposalId, string ProposerPlayerId, string StudOwnerPlayerId,
    string ProposerHeroId, string StudHeroId, long StudFeeSats, string Status, string? ChildHeroId);

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
    bool Minted = false, int TraitsAbsorbed = 0, string? NewGenomeHex = null, HeroDto? NewHero = null,
    // The rules this death-match was RESOLVED under; "" = pre-stamp, i.e. GameConfig.Default.
    string ConfigVersion = "",
    // The CONTENT (gear + dungeons) this was resolved under (ContentPackVersion). Trailing optional, same
    // discipline as ConfigVersion above: "" means the outcome predates content stamping, so it ran on the
    // gear the binary compiled in. Item stats feed combat, so a replay verified against DIFFERENT content
    // than it was resolved under would disagree with an honest server.
    string ContentVersion = "");

/// <summary>One death-match on the discovery list, derived from its session. Status = open (awaiting the defender's stake) | accepted (ready to settle) | resolved.</summary>
public record DeathMatchDto(
    string DeathMatchId, string ChallengerHeroId, string DefenderHeroId,
    string Status, bool Absorb, string? WinnerHeroId);

/// <summary>
/// Whether a settle would get past its funding gates yet — the same three facts
/// <c>SettleDeathMatchAsync</c> checks before it does anything, asked as a QUESTION rather than found out
/// by attempting.
///
/// <para>It exists because attempting was the client's only way to find out. A deposit takes a while to
/// settle into arkd's indexer, so the browser POSTed the settle on a loop and collected a 400 each time
/// until the chain caught up — fourteen console errors on a run that ended in a perfectly good death-match.
/// Chromium logs a console error for every failed fetch, so a SUCCESSFUL flow looked like a broken one,
/// which is how a team learns to stop reading the console.</para>
///
/// <para>The 400s themselves stay: the settle's own gates are the authority and must refuse an unfunded
/// settle whatever any client believes. This is only how a client learns to stop asking too early.</para>
/// </summary>
public record DeathMatchReadinessDto(
    /// <summary>Both heroes (and their staked gear) sit at the one joint escrow.</summary>
    bool StakesFunded,
    bool ChallengerFeePaid,
    bool DefenderFeePaid,
    /// <summary>The conjunction — a settle attempted now would get past the gates.</summary>
    bool Ready,
    /// <summary>Already resolved, so there is nothing left to settle and never will be. Distinct from
    /// <see cref="Ready"/> being false: one is "wait", the other is "stop".</summary>
    bool Completed);

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
    string ServerSeedHex, string EntropyHex, ProgressionReceiptDto Receipt,
    // The rules this run was RESOLVED under; "" = pre-stamp, i.e. GameConfig.Default.
    string ConfigVersion = "",
    // The CONTENT (gear + dungeons) this was resolved under (ContentPackVersion). Trailing optional, same
    // discipline as ConfigVersion above: "" means the outcome predates content stamping, so it ran on the
    // gear the binary compiled in. Item stats feed combat, so a replay verified against DIFFERENT content
    // than it was resolved under would disagree with an honest server.
    string ContentVersion = "");

/// <summary>One line of the Fancy discovery race. Every catalog title is listed, claimed or not, so players
/// can see what's still up for grabs — <see cref="HeroId"/> is null while a set is undiscovered.
/// <see cref="FoundCount"/> is how many heroes have ever expressed it.</summary>
public record FancyDiscoveryDto(
    string Title, string? HeroId, string? HeroName, string? OwnerId, long? UnixSeconds, int FoundCount);

// ── Endless PvE Trials (cold-start solo leaderboard): open (commit, FREE) → run (endless ghost ladder) ──

public record TrialsOpenRequest(string HeroId);

/// <summary>No fee — Trials is free to enter (it awards no XP/item/sats, so there's nothing to farm).
/// <see cref="Affix"/> is this week's rotating ladder rule, PINNED to the run when it opens so the score
/// and its later replay agree even if the week rolls over in between.</summary>
public record TrialsOpenResponse(string TrialsId, string CommitmentHex, string Affix, string AffixDescription);

public record TrialsRunRequest(string Nonce);

/// <summary>One trials wave for display; the client re-derives the ghost + fight from the seed to verify.</summary>
public record TrialsWaveDto(int Wave, int GhostLevel, bool Won, HeroDto Ghost, BattleResultDto Result);

/// <summary><see cref="HeroSnapshot"/> is the PRE-run hero, so the client replays <c>Trials.Resolve</c> and
/// re-checks the score + <see cref="Title"/> off the deterministic ladder. <see cref="BestScore"/> is the
/// hero's personal best waves-cleared to date (this run included) — the leaderboard basis.</summary>
public record TrialsRunResponse(
    int WavesCleared, IReadOnlyList<TrialsWaveDto> Waves, string? Title, int BestScore, string Affix,
    HeroDto HeroSnapshot, string ServerSeedHex, string EntropyHex, ProgressionReceiptDto Receipt,
    // The rules this run was RESOLVED under; "" = pre-stamp, i.e. GameConfig.Default.
    string ConfigVersion = "",
    // The CONTENT (gear + dungeons) this was resolved under (ContentPackVersion). Trailing optional, same
    // discipline as ConfigVersion above: "" means the outcome predates content stamping, so it ran on the
    // gear the binary compiled in. Item stats feed combat, so a replay verified against DIFFERENT content
    // than it was resolved under would disagree with an honest server.
    string ContentVersion = "");

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
    ProgressionReceiptDto? Receipt = null,
    // The rules this fight was RESOLVED under; "" = pre-stamp, i.e. GameConfig.Default.
    string ConfigVersion = "",
    // The CONTENT (gear + dungeons) this was resolved under (ContentPackVersion). Trailing optional, same
    // discipline as ConfigVersion above: "" means the outcome predates content stamping, so it ran on the
    // gear the binary compiled in. Item stats feed combat, so a replay verified against DIFFERENT content
    // than it was resolved under would disagree with an honest server.
    string ContentVersion = "");

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
    string WinnerHeroId, string CommitmentHex, string ServerSeedHex, string EntropyHex, string Nonce,
    // The rules this match was RESOLVED under (GameConfigVersion). Trailing optional: every match resolved
    // before stamping existed carries "" and verifies under GameConfig.Default, which is what it ran on.
    string ConfigVersion = "",
    // The CONTENT (gear + dungeons) this was resolved under (ContentPackVersion). Trailing optional, same
    // discipline as ConfigVersion above: "" means the outcome predates content stamping, so it ran on the
    // gear the binary compiled in. Item stats feed combat, so a replay verified against DIFFERENT content
    // than it was resolved under would disagree with an honest server.
    string ContentVersion = "");

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
    SquadResultDto Result, string CommitmentHex, string ServerSeedHex, string EntropyHex, string Nonce,
    // The rules this squad match was RESOLVED under; "" = pre-stamp, i.e. GameConfig.Default.
    string ConfigVersion = "",
    // The CONTENT (gear + dungeons) this was resolved under (ContentPackVersion). Trailing optional, same
    // discipline as ConfigVersion above: "" means the outcome predates content stamping, so it ran on the
    // gear the binary compiled in. Item stats feed combat, so a replay verified against DIFFERENT content
    // than it was resolved under would disagree with an honest server.
    string ContentVersion = "");

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
    long PriceSats,
    // Appended (defaulted) so an older client that has never seen these fields still deserializes the shop.
    int MinLevel = 1, string? Counters = null, int VarianceBonus = 0);

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

// ── Bids: buy a hero that is NOT for sale (propose → the owner consents → deliver → settle) ──

/// <summary>Offer a hero's owner <paramref name="BidSats"/> for a hero they have not listed. Nothing is
/// billed by making one — the invoice appears only if the owner accepts.</summary>
public record PlaceBidRequest(string HeroId, long BidSats);

/// <summary>One bid, as everyone sees it. <see cref="Status"/> = proposed (awaiting the owner) | accepted
/// (the bidder may fund it, and the owner may deliver) | declined | withdrawn | settled | refunded.
/// <see cref="ReclaimAfterUnixSeconds"/> is 0 until accepted; past it, an accepted bid that was never
/// delivered against can be unwound by either party and the bidder's sats go home.</summary>
public record BidDto(
    string BidId, string HeroId, string BidderPlayerId, string OwnerPlayerId,
    long BidSats, long FeeSats, string Status, long ReclaimAfterUnixSeconds);

/// <summary>What an accepted bid bills, and whether the bidder has paid it yet.
/// <see cref="SellerNetSats"/> is what actually reaches the owner — the bid less the marketplace fee, which
/// the seller absorbs exactly as they do on a listing. <see cref="Funded"/> is the fact the OWNER must not
/// deliver the hero without.</summary>
public record BidInvoiceResponse(
    BidDto Bid, FeeInvoiceDto Invoice, bool Funded, long SellerNetSats);

/// <summary>Unwinding an accepted bid. <see cref="RefundedSats"/> is what went back to the bidder — 0 when
/// the bid was accepted but never funded, which strands nothing.</summary>
public record BidRefundResponse(BidDto Bid, long RefundedSats);

/// <summary>Claim a custom, globally-unique name for a hero (a treasury sats sink).</summary>
public record RenameHeroRequest(string Name);

/// <summary>The treasury fee to pay before confirming the rename (0 + null when renaming is free).</summary>
public record RenameHeroResponse(long FeeSats, FeeInvoiceDto? Fee);

// ── Tournaments (a buy-in bracket; buy-ins → treasury, prizes → podium minus the house rake) ──
public record OpenTournamentRequest(string HeroId, long BuyInSats, int Size);
public record JoinTournamentRequest(string HeroId);
public record TournamentEntrantDto(string PlayerId, string HeroId);
/// <summary>EntrantsCommitmentHex (set once the bracket fills) is the FILL-time entrant-set commitment —
/// fetched here, independently of the server-controlled replay, so VerifyTournament can pin the snapshots.</summary>
public record TournamentDto(string Id, string OpenerPlayerId, long BuyInSats, int Size, int Joined, string Status,
    IReadOnlyList<TournamentEntrantDto> Entrants, string? ChampionHeroId, long ChampionPrizeSats = 0,
    string? EntrantsCommitmentHex = null);
/// <summary>A tournament + the buy-in fee-invoice the entrant pays into the treasury (open or join).</summary>
public record TournamentEntryResponse(TournamentDto Tournament, FeeInvoiceDto BuyIn);
public record TournamentMatchDto(int Round, int Index, string AId, string BId, string WinnerId);
/// <summary>A resolved tournament: the final bracket, the revealed seed/entropy (for replay), and the podium prizes.</summary>
public record TournamentResolveResponse(TournamentDto Tournament, IReadOnlyList<TournamentMatchDto> Bracket,
    string ServerSeedHex, string EntropyHex, IReadOnlyList<long> Prizes);
/// <summary>A refunded (unresolvable) tournament: how many entrants got their paid buy-in back, and the sats total.</summary>
public record TournamentRefundResponse(TournamentDto Tournament, int EntrantsRefunded, long RefundedSats);

/// <summary>Everything a spectator needs to REPLAY + verify a resolved tournament bracket
/// (FairnessAudit.VerifyTournament): the entrant snapshots (order-inert — the bracket seeding is drawn from
/// the seed), the final bracket, the champion, and the commit-reveal (commitment + revealed seed + entropy +
/// nonce). EntrantsCommitmentHex echoes the fill-time entrant-set commitment for convenience, but a
/// verifier must take it from the tournament DTO — this whole replay is server-supplied. Mirrors
/// SquadReplayDto.</summary>
public record TournamentReplayDto(
    IReadOnlyList<HeroDto> Entrants, IReadOnlyList<TournamentMatchDto> Bracket, string ChampionHeroId,
    string CommitmentHex, string ServerSeedHex, string EntropyHex, string Nonce,
    string? EntrantsCommitmentHex = null,
    // The rules this bracket was RESOLVED under; "" = pre-stamp, i.e. GameConfig.Default.
    string ConfigVersion = "",
    // The CONTENT (gear + dungeons) this was resolved under (ContentPackVersion). Trailing optional, same
    // discipline as ConfigVersion above: "" means the outcome predates content stamping, so it ran on the
    // gear the binary compiled in. Item stats feed combat, so a replay verified against DIFFERENT content
    // than it was resolved under would disagree with an honest server.
    string ContentVersion = "");

/// <summary>A player's derived accomplishments (from their roster + resolved tournaments) and the badges they've unlocked.</summary>
public record PlayerAchievementsDto(int HeroesOwned, int HeroesBred, int Legendaries, int Fancies, int TournamentsWon,
    IReadOnlyList<string> Badges, IReadOnlyList<string> FancySetsOwned, IReadOnlyDictionary<string, int> TraitAlbum,
    IReadOnlyList<FancyEditionDto> FancyEditions);

/// <summary>One Fancy-set hero the player owns, with its edition — the Nth ever found. Edition 1 is the
/// discoverer, and a low number stays scarce no matter how many turn up later.</summary>
public record FancyEditionDto(string HeroId, string HeroName, string Title, int Edition);

/// <summary>
/// A player's public trophy case — the standing they already see for themselves, made addressable by
/// anyone. A collection game runs on showing off, and until now a name on the leaderboard led nowhere.
/// Deliberately carries ONLY bragging material: no address, balance, token, or daily-claim state, because
/// everything here is readable by the whole arena.
/// </summary>
public record PlayerProfileDto(string PlayerId, string Name,
    SeasonPassProgress SeasonPass, PlayerAchievementsDto Achievements, IReadOnlyList<HeroDto> Notable);

/// <summary>Treasury-health telemetry (economy control plane): the finite, fee-funded pot's current balance, what it
/// has paid out by category ("daily"/"season"/"tournament"/"wager"/"squad"), and fees accrued to season pots. Outflow
/// is the insolvency-risk side; per-source inflow + net-issuance is a follow-up. Read-only observability.
///
/// <see cref="HeroSupply"/>/<see cref="Gen0Supply"/> track the OTHER economy — heroes, the asset with no hard cap.
/// Sats can't be printed, but heroes can be bred without limit, so hero supply is the inflation gauge the sats
/// figures miss. Gen0Supply is the free-starter float (gen-0 heroes only ever come from the starter grant).
///
/// <see cref="HeroesMinted"/>/<see cref="HeroesBurned"/> are the CHURN behind a flat supply: a stable
/// HeroSupply hides whether nothing happened or a thousand mints and burns netted out. Read as a RATE — the
/// mint rate outrunning the burn rate is the exact smoke alarm that fired late for CryptoKitties and Axie.
/// Both are counted at the source — mints at the mint, burns at each burn — rather than one being inferred
/// from the other, so a durable hero surviving a restart cannot mask a burn.
/// Counted since the server started (not persisted), so treat them as deltas over an uptime, not lifetime
/// totals. Burned is derived (minted − supply): a burn is the only thing that removes a hero.
///
/// <see cref="ActiveOfferCount"/>/<see cref="ClosedOfferCount"/> are the market-liquidity gauge: active is
/// resting inventory buyable right now, closed is cleared (fulfilled OR reclaimed — the store doesn't split
/// them). Active climbing while closed stalls is a glut — sellers listing faster than buyers take. That
/// listings-outran-sales cross is the earliest signal CryptoKitties gave, days before its peak. These reflect
/// the LAST-OBSERVED offer status (the health read never forces a chain reconcile), so they can lag truth.
///
/// SCOPE — <see cref="TotalInflowSats"/>/<see cref="TotalOutflowSats"/> and both by-tag maps are DURABLE
/// where the server is configured with a state database: every movement is stored as its own row and the
/// totals are grouped back out of those rows at boot, so they survive a restart. The rows, not the totals,
/// are what is stored — an inflow row is keyed by its invoice id, which doubles as the already-counted
/// marker, so a fee tallied before a restart cannot be tallied again after one. On a server running with no
/// state database (the in-memory default) they remain per-process and a restart still zeroes them. Either
/// way only <see cref="TreasuryBalanceSats"/> is authoritative on SOLVENCY — it is read from the chain, and
/// it is what the treasury HOLDS, where the flows are what it has booked moving in and out.
///
/// TRIPWIRES — the last two are not economy measurements but INSTRUMENT-HEALTH ones: both of the ways the
/// booking above is known to fail are safe (they under-report, never over-report) and silent, and a silent
/// under-report is indistinguishable from a quiet period. Neither number diagnoses a fault by itself; each
/// only gives the fault a shape to be spotted by. Both are per-uptime and never gate anything.</summary>
public record EconomyHealthDto(long TreasuryBalanceSats, long TotalInflowSats, long TotalOutflowSats,
    IReadOnlyDictionary<string, long> InflowByTag, IReadOnlyDictionary<string, long> OutflowByTag, long SeasonAccrualSats,
    long HeroSupply = 0, long Gen0Supply = 0, long HeroesMinted = 0, long HeroesBurned = 0,
    long ActiveOfferCount = 0, long ClosedOfferCount = 0,
    /// <summary>Fee-bearing offers that CLOSED with nothing booked against them — POSSIBLE RECLAIMS, OR a
    /// broken sale detector, and this count cannot tell which. Most will be genuine reclaims (taking an
    /// unsold listing back is free and books nothing, correctly), so a non-zero value is normal and is NOT
    /// on its own a fault. The signal is the TREND: this climbing steadily while booked <c>listing</c>
    /// income stays flat means sales have stopped being attributed — the detector reads the spending
    /// transaction's treasury output to tell a fulfil from a reclaim, and if that ever stops matching, item
    /// fees quietly return to being uncounted. Self-correcting: an offer booked later by any path stops
    /// counting here.</summary>
    long UnbookedClosedFeeOffers = 0,
    /// <summary>Durable treasury-ledger writes that FAILED and were swallowed. The swallow is deliberate
    /// and load-bearing (throwing would re-pay a daily claim or re-deliver a paid item), so this counter is
    /// the only way the failure surfaces as a number. Non-zero means the durable totals have fallen behind
    /// the in-memory ones and a restart will lose the difference; it never means a sat moved wrongly.</summary>
    long LedgerWriteFailures = 0);

/// <summary>
/// The offer address the seller deposits the item unit (+ carrier dust) into
/// from their own wallet, plus the ask and refund window. Once deposited the
/// offer becomes buyable; the covenant pins the seller as payee.
/// </summary>
public record CreateOfferResponse(
    string OfferId, string OfferAddress, string ItemAssetId,
    long AskSats, long OfferValueSats, long RefundAfterUnixSeconds,
    // Marketplace fee (0 = disabled) the offer's COVENANT routes to the treasury when it sells. There is
    // nothing to pay at listing: the seller absorbs it out of the ask, so a buyer pays AskSats and the
    // seller receives AskSats − ListingFeeSats. An offer that never sells is never charged. Display only.
    long ListingFeeSats = 0);

/// <summary>
/// One of this player's covenant escrows that may still hold their assets with no path forward — a
/// listing whose fee never cleared or that rests unsold, a breed/merge deposit that was never
/// revealed, or a stake left in an abandoned duel or death-match. Each is recovered by spending the
/// covenant's own timelocked reclaim leaf once
/// <see cref="ReclaimAfterUnixSeconds"/> passes (CHAIN time, not wall clock). The client rebuilds the
/// contract from the public escrow params, so this list is a CONVENIENCE FOR DISCOVERY, never a
/// permission: a player who knows the id can always reclaim without the server agreeing it is stuck.
/// </summary>
public record ReclaimableDto(
    /// <summary>"offer" | "breed" | "merge" | "wager" | "deathmatch" — which covenant, and so which reclaim
    /// flow recovers it — or "bid", the one kind that is NOT a covenant: an accepted bid's sats rest in the
    /// treasury under an invoice, and <c>POST /api/bids/{id}/refund</c> recovers them rather than a reclaim
    /// leaf. It is listed here anyway because it answers the same question the page exists for, and a player
    /// should not have to know which mechanism is holding their sats in order to find them.</summary>
    string Kind,
    string Id,
    /// <summary>A player-facing line naming what is escrowed and why it has no way forward.</summary>
    string Summary,
    long ReclaimAfterUnixSeconds);

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
/// <see cref="ArkadeHeroes.Core.GameConfig"/> — carries the HOT (economy) values for display plus the
/// running server's <see cref="Version"/>. The verification-critical values are NOT here: a client that
/// needs those asks for a specific stamped version via <c>GET /api/config/{version}</c> and gets a
/// <see cref="GameRulesDto"/>, so it replays under the rules a given match ran on rather than under
/// whatever this server happens to run now.
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
    int TournamentRakePct,
    // Whether innate-v2 combat passives are live (default off, same discipline as the CombatConfig flag).
    // The frontend reads this to decide whether to surface a hero's granted passives; with it off there is
    // no player-visible change. An older server that omits it deserializes to false — the safe default.
    bool InnateAbilities,
    // The server's CURRENT rules version — the stamp every outcome it resolves from now on carries.
    // Trailing optional: an older server that omits it deserializes to "" and clients treat that as
    // "unstamped" (GameConfig.Default), exactly as they treat a pre-stamp replay.
    string Version = "",
    // Whether gear COUNTERS are live — same job as InnateAbilities above. The frontend reads it to decide
    // whether to surface a hero's build SHAPE and the counter line at all, because a counter the player
    // cannot see reads as randomness rather than as strategy. Appended (defaulted) so an older server that
    // omits it deserializes to false — which is exactly the rules such a server resolves under.
    bool GearCounters = false,
    // Whether this server pays a daily reward at all. The frontend reads it to decide whether to render
    // the daily surface: the faucet ships closed, and a Claim button that can only ever answer "not
    // available on this server" reads as a broken game rather than an unswitched-on feature. Appended
    // (defaulted false) so an older server that omits it deserializes to hidden — the safe direction,
    // since showing a faucet that does not exist is the failure worth avoiding.
    bool DailyRewardEnabled = false,
    /// <summary>What a starter claim costs in total — the breed fee at zero prior breeds, once per hero
    /// minted. Published so the UI can price the claim before the player commits to it.</summary>
    long StarterClaimFeeSats = 0)
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
        c.TournamentRakePct,
        c.Combat.InnateAbilities,
        ArkadeHeroes.Core.GameConfigVersion.Compute(c),
        c.Combat.GearCounters,
        c.DailyRewardEnabled,
        ArkadeHeroes.Core.Genetics.StarterPolicy.ClaimFeeSats(c));
}

/// <summary>
/// The VERIFICATION-CRITICAL rules of one <see cref="ArkadeHeroes.Core.GameConfig"/>, served by
/// <c>GET /api/config/{version}</c> so a client holding a stamped replay can replay it under the rules it was
/// actually resolved under. Flat by design: every value a deterministic replay reads is present, so
/// <see cref="ToGameConfig"/> rebuilds a config whose <see cref="ArkadeHeroes.Core.GameConfigVersion"/> is the
/// <see cref="Version"/> that was asked for — and the client MUST recompute and check that, which is what
/// makes the fetch trustless: a server that serves rules other than the ones it stamped is caught by the
/// client's own recompute, not trusted on its word.
///
/// The economy values are absent on purpose (they are not part of the version id and no replay reads them);
/// <see cref="ToGameConfig"/> therefore overlays these rules onto <c>GameConfig.Default</c>'s economy, which
/// the replay never touches.
/// </summary>
public record GameRulesDto(
    string Version,
    // Absorb odds (VerifyAbsorb)
    byte AbsorbChance, byte AbsorbContinueChance,
    // GeneMixer thresholds (VerifyBreeding) + Fusion threshold (VerifyMerge)
    byte GeneRegionMutationThreshold, byte GeneTraitMutationThreshold, byte FusionConcentrateThreshold,
    // Sterility (rarity-derived breeding cap)
    int SterileLegendary, int SterileEpic, int SterileRare, int SterileUncommon,
    // Rarity bands: tier cutoffs + weights
    byte RarityLegendaryCutoff, byte RarityEpicCutoff, byte RarityRareCutoff, byte RarityUncommonCutoff,
    int RarityLegendaryWeight, int RarityEpicWeight, int RarityRareWeight, int RarityUncommonWeight,
    int RarityCommonWeight,
    // Affinity bonuses per tier + cap
    double AffinityLegendary, double AffinityEpic, double AffinityRare, double AffinityUncommon,
    double AffinityCommon, double AffinityCap,
    // XP curve
    long CurveBase, double CurveCoefficient, double CurveExponent, int CurveMaxLevel,
    // Combat
    int MaxTurns, double ElementStrong, double ElementWeak, double CritMultiplier, double ArmorConstant,
    int GeneSkillALevel, int GeneSkillBLevel, int BurstLevel,
    double FocusPerStack, double DefenseBreakPerStack, int MaxEffectStacks, double DrainFraction,
    string SelectionPolicy, int HealHpThresholdPercent,
    bool ElementAwareSelection, bool InnateAbilities, bool SquadSynergy,
    // Innate-v2 proc knobs (always sent resolved — a null Innate means InnateBonuses.Default)
    double ShieldChance, double Ward, double RegenChance, double Mend, double TrueStrikeChance,
    double ThornsChance, double Reflect, double BrandChance, double Tick, int BrandTurns,
    double InitiativeChance,
    // Gear counters. Trailing optional so an older server that omits them deserializes to the flag OFF and
    // the default knobs — which is exactly the rules such a server was resolving under. Always sent
    // RESOLVED (a null Counters means GearCounterRules.Default), same rule as the innate knobs above.
    bool GearCounters = false,
    double CounterEdge = 0.20, double ShapeOffenseShare = 0.305,
    double ShapeBulkShare = 0.359, double ShapeTempoShare = 0.336)
{
    public static GameRulesDto From(ArkadeHeroes.Core.GameConfig c)
    {
        var i = c.Combat.InnateOrDefault;
        var gc = c.Combat.CountersOrDefault;
        return new GameRulesDto(
            ArkadeHeroes.Core.GameConfigVersion.Compute(c),
            c.Absorb.AbsorbChance, c.Absorb.ContinueChance,
            c.Gene.RegionMutationThreshold, c.Gene.TraitMutationThreshold, c.FusionConcentrateThreshold,
            c.Sterility.Legendary, c.Sterility.Epic, c.Sterility.Rare, c.Sterility.Uncommon,
            c.Rarity.LegendaryCutoff, c.Rarity.EpicCutoff, c.Rarity.RareCutoff, c.Rarity.UncommonCutoff,
            c.Rarity.LegendaryWeight, c.Rarity.EpicWeight, c.Rarity.RareWeight, c.Rarity.UncommonWeight,
            c.Rarity.CommonWeight,
            c.Affinity.Legendary, c.Affinity.Epic, c.Affinity.Rare, c.Affinity.Uncommon,
            c.Affinity.Common, c.Affinity.Cap,
            c.Curve.Base, c.Curve.Coefficient, c.Curve.Exponent, c.Curve.MaxLevel,
            c.Combat.MaxTurns, c.Combat.ElementStrong, c.Combat.ElementWeak, c.Combat.CritMultiplier,
            c.Combat.ArmorConstant,
            c.Combat.GeneSkillALevel, c.Combat.GeneSkillBLevel, c.Combat.BurstLevel,
            c.Combat.FocusPerStack, c.Combat.DefenseBreakPerStack, c.Combat.MaxEffectStacks,
            c.Combat.DrainFraction,
            c.Combat.SelectionPolicy.ToString(), c.Combat.HealHpThresholdPercent,
            c.Combat.ElementAwareSelection, c.Combat.InnateAbilities, c.Combat.SquadSynergy,
            i.ShieldChance, i.Ward, i.RegenChance, i.Mend, i.TrueStrikeChance,
            i.ThornsChance, i.Reflect, i.BrandChance, i.Tick, i.BrandTurns, i.InitiativeChance,
            c.Combat.GearCounters, gc.Edge, gc.OffenseShare, gc.BulkShare, gc.TempoShare);
    }

    /// <summary>
    /// Rebuilds the config these rules describe, overlaid on <c>GameConfig.Default</c>'s (replay-inert)
    /// economy. Returns null if <see cref="SelectionPolicy"/> is not a policy this client knows — an
    /// EXPLICIT failure rather than a quiet substitution, since a wrong policy would replay a different fight.
    /// </summary>
    public ArkadeHeroes.Core.GameConfig? ToGameConfig()
    {
        if (!Enum.TryParse<ArkadeHeroes.Core.CombatSelectionPolicy>(SelectionPolicy, out var policy))
            return null;
        var d = ArkadeHeroes.Core.GameConfig.Default;
        return d with
        {
            Absorb = new ArkadeHeroes.Core.Genetics.AbsorbOdds(AbsorbChance, AbsorbContinueChance),
            Gene = new ArkadeHeroes.Core.GeneConfig(GeneRegionMutationThreshold, GeneTraitMutationThreshold),
            FusionConcentrateThreshold = FusionConcentrateThreshold,
            Sterility = new ArkadeHeroes.Core.SterilityChances(
                SterileLegendary, SterileEpic, SterileRare, SterileUncommon),
            Rarity = new ArkadeHeroes.Core.RarityBands(
                RarityLegendaryCutoff, RarityEpicCutoff, RarityRareCutoff, RarityUncommonCutoff,
                RarityLegendaryWeight, RarityEpicWeight, RarityRareWeight, RarityUncommonWeight,
                RarityCommonWeight),
            Affinity = new ArkadeHeroes.Core.AffinityBonuses(
                AffinityLegendary, AffinityEpic, AffinityRare, AffinityUncommon, AffinityCommon, AffinityCap),
            Curve = new ArkadeHeroes.Core.XpCurve(CurveBase, CurveCoefficient, CurveExponent, CurveMaxLevel),
            Combat = new ArkadeHeroes.Core.CombatConfig(
                MaxTurns, ElementStrong, ElementWeak, CritMultiplier, ArmorConstant,
                GeneSkillALevel, GeneSkillBLevel, BurstLevel,
                FocusPerStack, DefenseBreakPerStack, MaxEffectStacks, DrainFraction,
                policy, HealHpThresholdPercent,
                ElementAwareSelection, InnateAbilities, SquadSynergy,
                new ArkadeHeroes.Core.InnateBonuses(
                    ShieldChance, Ward, RegenChance, Mend, TrueStrikeChance,
                    ThornsChance, Reflect, BrandChance, Tick, BrandTurns, InitiativeChance),
                GearCounters,
                new ArkadeHeroes.Core.GearCounterRules(
                    CounterEdge, ShapeOffenseShare, ShapeBulkShare, ShapeTempoShare)),
        };
    }
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

// ── Operator console (/api/admin/*) ────────────────────────────────────────
// One authenticated surface over what the server ALREADY knows. Every number below is composed from live
// state at read time — none of it adds a counter to a money path, and the read never reconciles, settles
// or pays. The management actions are the deliberate exception and are listed on AdminOverviewDto.

/// <summary>The contract both halves of the admin surface must agree on. The secret travels in a HEADER,
/// never a query string — a URL lands in browser history, proxy logs and Referer headers, and this one
/// secret is the whole of the console's authentication.</summary>
public static class AdminApiContract
{
    public const string TokenHeader = "X-Admin-Token";
}

/// <summary>One slice of the hero population — a generation ("0", "1", …) or a rarity tier — and how many
/// heroes are in it. Derived from the live roster's genomes at read time, never tracked.</summary>
public record SupplyBucketDto(string Key, long Count);

/// <summary>How many of one flow's sessions are still in play vs how many this server has seen at all.
/// <paramref name="Open"/> counts the NON-terminal ones (unsettled, unresolved, unrevealed) — the backlog
/// an operator would look at first. Both are point-in-time counts over in-memory sessions, so a restart
/// resets <paramref name="Total"/> for every flow whose sessions aren't persisted.</summary>
public record FlowCountsDto(string Flow, long Open, long Total);

/// <summary>Player-population and activity counts, all read off state the server already keeps.
/// There is NO registration timestamp on a player, so "new players per day" cannot be answered here —
/// what activity is available is the daily loop's own markers: today's claims, and live streaks.</summary>
public record AdminPlayersDto(long Registered, long WithHeroes, long ClaimedDailyToday, long WithLiveStreak);

/// <summary>Market state. The three status counts are LAST-OBSERVED — the admin read deliberately does not
/// reconcile offers against the chain, because reconciling books listing income, and an analytics read must
/// not write to the treasury ledger. <paramref name="ListingFeesCapturedSats"/> is the booked
/// <c>listing</c> inflow; <paramref name="RestingAskSats"/> is what the resting inventory is asking for.</summary>
public record AdminMarketDto(long PendingOffers, long ActiveOffers, long ClosedOffers,
    long ListingFeesCapturedSats, long RestingAskSats);

/// <summary>A bracket as the operator console sees it. <paramref name="HasEntrantSnapshots"/> is the exact
/// fact the strand-refund gate reads for a FULL bracket: fill-time snapshots are never persisted, so a
/// bracket that came back <c>full</c> without them can never resolve and only a refund clears it.</summary>
public record AdminTournamentDto(string Id, string Status, long BuyInSats, int Size, int Entrants,
    bool HasEntrantSnapshots);

/// <summary>Everything the operator console shows, in one authenticated read.
///
/// PURE OBSERVATION. <see cref="Economy"/> is the same read the public treasury card uses;
/// <see cref="Season"/> is the current window PROJECTED without the lazy settle the player-facing season
/// board triggers, so opening this page can never move a sat. The tripwires that say "something has
/// silently broken" — <c>Economy.UnbookedClosedFeeOffers</c> and <c>Economy.LedgerWriteFailures</c> — live
/// on the economy read and are surfaced first in the UI.</summary>
public record AdminOverviewDto(
    long GeneratedAtUnix,
    EconomyHealthDto Economy,
    AdminPlayersDto Players,
    IReadOnlyList<SupplyBucketDto> HeroesByGeneration,
    IReadOnlyList<SupplyBucketDto> HeroesByRarity,
    AdminMarketDto Market,
    IReadOnlyList<FlowCountsDto> Flows,
    SeasonLeaderboardDto Season,
    IReadOnlyList<AdminTournamentDto> Tournaments);

/// <summary>What one management action did, in a line an operator can read back. The server logs the same
/// fact — every admin action is logged with what was done and to what.</summary>
public record AdminActionResultDto(string Action, string Detail);

/// <summary>
/// One entry from the server's append-only audit log — a single state-changing action, exactly as it was
/// written. Immutable at the storage layer (the table refuses UPDATE and DELETE), so what is served here is
/// what was recorded at the time and can never have been edited since.
/// </summary>
/// <param name="Sequence">The monotonic position in the log. Also the paging cursor.</param>
/// <param name="AtUnixSeconds">When the action happened, UTC.</param>
/// <param name="ActorPlayerId">Who caused it, or null for the server itself (a lazy settle, an operator action).</param>
/// <param name="EventType">What happened, e.g. <c>deathmatch.settled</c>, <c>treasury.outflow</c>.</param>
/// <param name="SubjectIds">Every id it touched — heroes, sessions, offers, players.</param>
/// <param name="PayloadJson">The specifics as raw JSON: amounts in sats, counterparty, outcome.</param>
public record AuditEventDto(
    long Sequence, long AtUnixSeconds, string? ActorPlayerId, string EventType,
    IReadOnlyList<string> SubjectIds, string PayloadJson);

/// <summary>
/// One page of the audit log, plus the operator-facing health of the log itself.
///
/// <paramref name="NextAfter"/> is the cursor for the next page — feed it back as <c>after</c>. It is the
/// last sequence in <paramref name="Events"/>, or the <c>after</c> that was asked for when the page is
/// empty, so polling for new events is a stable loop rather than a re-read of history.
///
/// <paramref name="WriteFailures"/> is the number that matters when it is not zero: log writes are
/// best-effort by design (failing them would abort settled money paths), so this is how a log that has
/// gone deaf surfaces at all. Any non-zero value means history is incomplete from here on.
/// </summary>
public record AuditPageDto(
    IReadOnlyList<AuditEventDto> Events, long NextAfter, long WriteFailures);

/// <summary>
/// The authored CONTENT of one pack, served by <c>GET /api/content/{version}</c> so a client holding an
/// outcome stamped with an unfamiliar content version can rebuild the gear and dungeons it was actually
/// resolved under instead of assuming its own compiled-in pack.
///
/// It carries the AUTHORED JSON VERBATIM rather than a flattened mirror of the schema, and that is the
/// point. <see cref="GameRulesDto"/> has to be kept in step with <c>GameConfig</c> by hand — a field
/// forgotten there would serve a verifier rules that hash to something else, which is why it needs its own
/// round-trip test. Here the wire form IS the source form, so the round trip cannot drift by construction:
/// the client feeds these two strings back through the same <c>ContentPackLoader.Parse</c> the server used,
/// recomputes <c>ContentPackVersion</c>, and REFUSES the answer unless it reproduces the version it asked
/// for. A server cannot serve content other than the content it stamped.
/// </summary>
public record ContentPackDto(string Version, string ItemsJson, string DungeonsJson);
