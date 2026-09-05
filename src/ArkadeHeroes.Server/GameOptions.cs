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

    /// <summary>Flat sats fee to claim a custom, globally-unique hero name — an evergreen identity sink to
    /// the treasury (no power creep, whale-friendly ceiling). 0 = renaming is free.</summary>
    public long HeroRenameFeeSats { get; set; } = 500;

    /// <summary>Tournament house rake: % of the buy-in pot the treasury keeps; the rest splits to the podium
    /// (champion + runner-up). A treasury sink that scales with competitive play.</summary>
    public int TournamentRakePct { get; set; } = 10;

    /// <summary>Base unit for breeding cooldowns. Short by default so regtest play loops stay fast.</summary>
    public TimeSpan BreedingCooldownBaseUnit { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Per-hero cooldown between PvE gauntlet runs (F1) — rate-limits the capped XP faucet.
    /// Short by default so regtest/play loops stay fast; a real deployment raises it (~10 min).</summary>
    public TimeSpan GauntletCooldown { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Fixed 32-byte hex key for signing progression receipts; ephemeral per process when unset.</summary>
    public string? ReceiptKeyHex { get; set; }

    /// <summary>How long after a covenant match opens its escrow refund leaves unlock (player liveness).</summary>
    public TimeSpan WagerEscrowRefundAfter { get; set; } = TimeSpan.FromHours(24);

    /// <summary>How long a FULL bracket waits on an outstanding buy-in before the seat counts as abandoned
    /// and the pot may be refunded. Deliberately NOT <see cref="WagerEscrowRefundAfter"/>, though it starts
    /// at the same value: that one is baked into covenant script addresses as a reclaim timelock, so sharing
    /// it would mean retuning this server policy silently retimed every covenant minted afterwards.</summary>
    public TimeSpan TournamentUnpaidBuyInGrace { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Absorb death-match odds — server game config PUBLISHED on /api/chain/info so the client's
    /// VerifyAbsorb recomputes the same outcome. P(absorb happens) ≈ AbsorbChance/256; each further trait
    /// ≈ AbsorbContinueChance/256 (front-loaded → mostly one trait, rarely a full concentrate).</summary>
    public byte AbsorbChance { get; set; } = 102;        // ≈40%
    public byte AbsorbContinueChance { get; set; } = 90;  // ≈35%

    // ── Economy tunables (HOT: server-enforced, never re-verified). Defaults reference the
    //    Core consts so there is a single literal source; an operator overrides via config. ──
    public long MatchFeeBaseSats { get; set; } = Leveling.MatchFeeBaseSats;      // 500
    public long MatchFeePerLevel { get; set; } = Leveling.MatchFeePerLevel;      // 20
    public int DeathMatchFeeMultiplier { get; set; } = Leveling.DeathMatchFeeMultiplier; // 2× MatchFee
    public int AbsorbFeeMultiplier { get; set; } = Leveling.AbsorbFeeMultiplier;         // 3× MatchFee
    public int BreedFeeDoublingCap { get; set; } = BreedingPolicy.FeeDoublingCap; // 3
    public int MatchmakingTake { get; set; } = 10;

    /// <summary>Ranked-ladder season length in days — the current season is derived from Season.Epoch + this.</summary>
    public int SeasonLengthDays { get; set; } = 14;

    // ── Daily engagement loop tunables (sats faucet: base + per-quest bonus, streak-scaled) ──
    /// <summary>
    /// Whether the daily sats faucet is open at all. OFF by default, because on an open signup it is a
    /// standing invitation: a wallet is free to create and the signed challenge only proves you hold a key
    /// you invented, so nothing stops one person registering a thousand accounts and collecting from each.
    /// At the shipped numbers that is up to 1,000 sats per account per day — real bitcoin, out of a
    /// treasury that cannot inflate to cover it.
    ///
    /// Turn it on once signup costs an attacker something (see the starter-claim gate), and keep
    /// TreasuryReserveFloorSats set, which is the backstop rather than the fix.
    /// </summary>
    public bool DailyRewardEnabled { get; set; } = false;

    public long DailyBaseSats { get; set; } = 50;
    public long DailyQuestBonusSats { get; set; } = 150;
    public int DailyQuestsPerDay { get; set; } = 3;
    public int DailyStreakStepPct { get; set; } = 10;
    public int DailyStreakCapPct { get; set; } = 100;

    /// <summary>Season prize pool: a house-funded base + a slice of each staked match's fees, split 60/30/10.</summary>
    public long SeasonPotBaseSats { get; set; } = 25_000;
    public int SeasonFeeAccrualPct { get; set; } = 20;

    /// <summary>Marketplace fee: flat sats the treasury takes from each completed sale (item or hero) —
    /// treasury capture on secondary trades, the counterweight to the daily + season faucets. LIVE at 1000,
    /// matching the breed and merge fee scale.
    /// It is enforced by the offer's own COVENANT, not billed at listing: a buyer pays the ask, and the
    /// fulfil leaf splits it — seller gets ask − fee, treasury gets fee. So the SELLER absorbs it, listing
    /// costs nothing up front, an offer that never sells is never charged, and neither the seller nor the
    /// server can skip the cut. Raising this reduces what a seller nets at a given ask; it does not deter
    /// listing, because listing is free. An ask at or below this value is refused outright (the seller's
    /// payout would be non-positive and the covenant could not be built).
    /// Booked as inflowByTag {"listing": n} for item AND hero sales alike, once the chain confirms the sale:
    /// only a fulfil pays the treasury in the transaction that spends the offer, so a seller reclaim is
    /// never mistaken for one. A sale the chain cannot confirm stays uncounted — the tally may lag the
    /// treasury's real income, never lead it. Changing this affects only NEW offers — a resting one keeps
    /// the fee baked into the covenant it was created with.</summary>
    public long OfferListingFeeSats { get; set; } = 1_000;

    /// <summary>How many LIVE bids one player may have outstanding across the whole game. Bids are free to
    /// make on purpose — nothing is billed until the hero's owner consents, which is what makes an ignored
    /// bid cost the bidder nothing — and "free" plus "unbounded" is a spam surface: one account could paper
    /// every hero in the arena with bids it never intends to fund, and every owner's inbox with noise. This
    /// is the counterweight. It bounds nothing else: a bid that is settled, refunded, declined or withdrawn
    /// stops counting immediately. 0 (or any non-positive value) disables the cap entirely.</summary>
    public int MaxOpenBidsPerPlayer { get; set; } = 20;

    /// <summary>Faucet governor reserve floor: the daily faucet clamps its payout to (treasury balance − this),
    /// so it can never drain the treasury below this permanent reserve. 0 = no floor (clamp to full balance).
    /// An operator raises it to protect scheduled obligations (e.g. the season pot) from the daily emission drain.</summary>
    public long TreasuryReserveFloorSats { get; set; } = 0;

    /// <summary>Shared secret for the operator console (<c>/api/admin/*</c>), sent on every admin request in
    /// the <c>X-Admin-Token</c> header and compared in constant time (<see cref="AdminGate"/>).
    /// UNSET (the default) means the admin surface is not mapped AT ALL: its routes do not exist and every
    /// one of them 404s. That is the fail-closed direction, and it is the one a deployment gets by omission —
    /// an operator console on a server holding real bitcoin has to be switched ON deliberately.
    /// Set it out of band (<c>Game__AdminToken</c>), never in a committed file. It is never logged, never put
    /// in a URL, and no endpoint ever returns it.</summary>
    public string? AdminToken { get; set; }

    /// <summary>Path to the SQLite file holding state a restart must not lose (currently paid item purchases).
    /// UNSET = no persistence at all: everything stays in memory and is lost on restart, exactly as before.
    /// Set it in a deployment so a player who paid for an item can still claim it after a bounce.</summary>
    public string? StateDbPath { get; set; }

    /// <summary>How often the hero flush persists DIRTY progression (level/XP, equipment, cooldowns, breed
    /// count). Identity events — mint, burn, transfer, rename — persist inline regardless, so a crash loses
    /// at most this window of grinding, never a hero. Only meaningful when <see cref="StateDbPath"/> is set
    /// (the flush service isn't registered otherwise).</summary>
    public TimeSpan HeroFlushInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Opt-in obligation reservation: when true, the daily faucet also holds back the current season pot
    /// (base + accrued) so emission can't drain the sats the upcoming season settlement owes. Default off.</summary>
    public bool ReserveSeasonPot { get; set; } = false;

    /// <summary>Genome-derived innate combat passives (innate-v2): each of a hero's EXPRESSED cosmetic
    /// traits grants one rare, high-impact proc — Aura→ward, Marking→mend, Eyes→true strike, Crest→thorns,
    /// Sigil→brand, Stance→initiative — so rarity and breeding start to matter in the fight rather than only
    /// on the card. LIVE by default.
    /// It is a CONFIG switch rather than a Core constant for two reasons. It can be turned back off without a
    /// code change if the balance band moves. And <see cref="CombatConfig.Default"/> must STAY off regardless:
    /// that constant is what every UNSTAMPED (pre-stamp) replay is reconstructed under, so flipping it there
    /// would silently rewrite what historical outcomes are checked against.
    /// Turning this on is safe for verification precisely because of the stamp: the resulting config hashes to
    /// a <see cref="GameConfigVersion"/> that is NOT the default one, every outcome resolved under it carries
    /// that stamp, and a client resolves the stamp via GET /api/config/{version} before replaying — so a
    /// verifier follows the flip automatically instead of replaying under its own compiled-in default. Only
    /// heroes that EXPRESS a cosmetic trait are affected at all: gen-0 starters come out of Genome.NewGen0
    /// with genes[16..] cleared, express nothing, and fight identically either way.</summary>
    public bool InnateAbilities { get; set; } = true;

    /// <summary>Gear COUNTERS: an item may be worth more or less depending on the opponent's build shape, and
    /// a WILDCARD item may widen its wearer's damage roll (see <see cref="Core.Combat.CombatShapes"/>). This
    /// is what stops a played-out roster converging on one tier-3 set — measured, it lifts gear's total-effect
    /// share of a fully-geared roster's outcome variance from 0.0% to 9.3%.
    /// LIVE by default — and it waited for the one thing that was missing rather than for more balance data:
    /// a counter the player cannot SEE reads as randomness rather than as strategy, so the hero card now shows
    /// a hero's build SHAPE and whether it carries a counter charm (gated on the PUBLISHED
    /// <see cref="Shared.GameConfigDto.GearCounters"/>), which makes counter-picking a decision instead of a
    /// surprise.
    /// It is a CONFIG switch rather than a Core constant for the same two reasons as InnateAbilities above. It
    /// can be turned back off without a code change if the balance band moves. And
    /// <see cref="CombatConfig.Default"/> must STAY off regardless: that constant is what every UNSTAMPED
    /// (pre-stamp) replay is reconstructed under, so flipping it there would silently rewrite what historical
    /// outcomes are checked against.
    /// Turning this on is safe for verification precisely because of the stamp: the resulting config hashes to
    /// a non-default <see cref="GameConfigVersion"/> — the flag AND every <see cref="GearCounterRules"/> knob
    /// are hashed — every outcome resolved under it carries that stamp, and a client resolves the stamp via
    /// GET /api/config/{version} before replaying, so a verifier follows the flip automatically instead of
    /// replaying under its own compiled-in default. Unlike innate, the blindness to watch is GEAR, not
    /// generation: the counter multiplier is a product over the wearer's items, so an UNGEARED hero — or two
    /// heroes wearing the same trinket — fights identically either way. <c>GearCounterFlipSafetyTests</c>
    /// measures exactly that, so the gap is a number rather than a worry.
    /// The level GATE on the item catalog is independent of this switch and is live either way — it is an
    /// equip rule, not a combat rule.</summary>
    public bool GearCounters { get; set; } = true;

    /// <summary>Opt-in server-side Terms enforcement: when true, claiming starter heroes — the first
    /// irreversible step, where assets are minted — is refused until the player's RECORDED acceptance covers
    /// <see cref="Shared.Terms.CurrentVersion"/>. Default OFF, because the browser already gates entry and
    /// every existing API client (the console client, the test suite) predates any terms screen; a deployment
    /// that stakes real bitcoin turns it on so the gate is not merely cosmetic. Acceptance is RECORDED
    /// either way — this switch only decides whether its absence blocks.</summary>
    public bool RequireTermsAcceptance { get; set; } = false;

    /// <summary>Projects these options into the Core <see cref="GameConfig"/> the game logic reads (current version).</summary>
    public GameConfig ToGameConfig() => new(
        Absorb: new AbsorbOdds(AbsorbChance, AbsorbContinueChance),
        Gene: GeneConfig.Default,
        FusionConcentrateThreshold: 217,
        Sterility: SterilityChances.Default,
        Rarity: RarityBands.Default,
        Affinity: AffinityBonuses.Default,
        Curve: XpCurve.Default,
        Combat: CombatConfig.Default with { InnateAbilities = InnateAbilities, GearCounters = GearCounters },
        Breeding: new BreedingPolicy(BreedingCooldownBaseUnit),
        BreedingFeeSats: BreedingFeeSats,
        MergeFeeSats: MergeFeeSats,
        MatchFeeBaseSats: MatchFeeBaseSats,
        MatchFeePerLevel: MatchFeePerLevel,
        DeathMatchFeeMultiplier: DeathMatchFeeMultiplier,
        AbsorbFeeMultiplier: AbsorbFeeMultiplier,
        BreedFeeDoublingCap: BreedFeeDoublingCap,
        MatchmakingTake: MatchmakingTake,
        SeasonLengthDays: SeasonLengthDays,
        DailyRewardEnabled: DailyRewardEnabled,
        DailyBaseSats: DailyBaseSats,
        DailyQuestBonusSats: DailyQuestBonusSats,
        DailyQuestsPerDay: DailyQuestsPerDay,
        DailyStreakStepPct: DailyStreakStepPct,
        DailyStreakCapPct: DailyStreakCapPct,
        SeasonPotBaseSats: SeasonPotBaseSats,
        SeasonFeeAccrualPct: SeasonFeeAccrualPct,
        OfferListingFeeSats: OfferListingFeeSats,
        HeroRenameFeeSats: HeroRenameFeeSats,
        TournamentRakePct: TournamentRakePct,
        TreasuryReserveFloorSats: TreasuryReserveFloorSats,
        ReserveSeasonPot: ReserveSeasonPot);
}
