using System.Collections.Concurrent;
using System.Threading;
using ArkadeHeroes.Core.Combat;
using ArkadeHeroes.Core.Heroes;

namespace ArkadeHeroes.Server;

public class Player
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Token { get; init; }
    public bool StarterClaimed { get; set; }
    /// <summary>The fee-invoice billed for the starter claim, once requested. Durable on purpose: the
    /// player pays this with real sats BEFORE any hero is minted, so a restart in that window must not
    /// forget what was already paid for — otherwise the next request bills them a second time for heroes
    /// they have already bought.</summary>
    public string? StarterFeeInvoiceId { get; set; }
    /// <summary>The wallet's stable x-only login pubkey (hex), when registered — enables "sign in with your wallet" resume.</summary>
    public string? LoginPubKeyHex { get; set; }

    /// <summary>Daily-loop streak: consecutive UTC days claimed (0 = never). In-memory, like all player state.</summary>
    public int StreakCount { get; set; }
    /// <summary>The last day-index the player claimed the daily reward (null = never) — enforces once/day.</summary>
    public int? LastClaimDay { get; set; }

    /// <summary>The Terms-of-Use version this player explicitly accepted (null = never). Load-bearing:
    /// this game stakes real bitcoin and burns assets permanently, so the record of WHAT the player agreed
    /// to — and when — is the only evidence the disclosure was ever made.</summary>
    public int? TermsAcceptedVersion { get; set; }
    /// <summary>When that acceptance happened, in UTC.</summary>
    public DateTimeOffset? TermsAcceptedAtUtc { get; set; }
}

/// <summary>The first hero ever to express a named Fancy set, and who owned it at that moment — the prize
/// in the discovery race. Immutable once claimed.</summary>
public sealed record FancyDiscovery(string Title, string HeroId, string HeroName, string OwnerId, long UnixSeconds);

/// <summary>A hero's Fancy set and its edition number — "Sovereign #1" is the discoverer, "#7" the seventh
/// ever found. Assigned once, at the moment the hero enters the game, and never renumbered.</summary>
public sealed record FancyEdition(string Title, int Edition);

/// <summary>The complete stamped fact for one Fancy find — a hero's set, its edition, and who found it.
/// The union of what a <see cref="FancyDiscovery"/> and a <see cref="FancyEdition"/> each hold, and exactly
/// what durability persists so "first to breed this set, forever" survives a restart.</summary>
public sealed record FancyFind(string Title, string HeroId, string HeroName, string OwnerId, long UnixSeconds, int Edition);

/// <summary>A pending PvE gauntlet run (F1): the seed is committed at open; the run resolves once the
/// fee invoice is paid, awarding capped XP + a full-clear item, then rate-limits the hero.</summary>
public class GauntletSession
{
    public required string Id { get; init; }
    public required string PlayerId { get; init; }
    public required string HeroId { get; init; }
    public required byte[] ServerSeed { get; init; }
    public required string CommitmentHex { get; init; }
    public required string FeeInvoiceId { get; init; }
    public required long FeeSats { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool Completed { get; set; }
}

/// <summary>A pending endless-Trials run: the seed is committed at open; the run resolves on reveal (no fee,
/// no reward beyond a score + title), tracking the hero's personal best.</summary>
public class TrialsSession
{
    public required string Id { get; init; }
    public required string PlayerId { get; init; }
    public required string HeroId { get; init; }
    public required byte[] ServerSeed { get; init; }
    public required string CommitmentHex { get; init; }
    /// <summary>This week's ladder rule, PINNED when the run opens so the score and its replay agree even
    /// if the week rolls over before the run resolves (or before a client verifies it).</summary>
    public ArkadeHeroes.Core.Progression.TrialsAffix Affix { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool Completed { get; set; }
}

public class BreedingSession
{
    public required string Id { get; init; }
    public required string PlayerId { get; init; }
    public required string ParentAId { get; init; }
    public required string ParentBId { get; init; }
    public required byte[] ServerSeed { get; init; }
    public required string CommitmentHex { get; init; }
    /// <summary>"invoice" (fee-paid, treasury mint) or "covenant" (escrow deposit, emulator-enforced mint).</summary>
    public string Mode { get; init; } = "invoice";
    /// <summary>Fee invoice the player's own wallet must pay before reveal (invoice mode).</summary>
    public string? FeeInvoiceId { get; init; }
    /// <summary>Breed escrow address the player deposits both parents + fee into (covenant mode).</summary>
    public string? EscrowAddress { get; set; }
    /// <summary>The escalated breeding fee for this session (sats) — paid via the invoice or the breed escrow.</summary>
    public long FeeSats { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool Completed { get; set; }
    public string? ChildHeroId { get; set; }
}

/// <summary>
/// A stud-service breed proposal: one player asks to breed THEIR hero with ANOTHER player's, optionally
/// offering that owner a stud fee in sats. Shaped like <see cref="DeathMatchSession"/> — proposed → accepted
/// → completed — because it needs the same thing that flow needs: a counterparty who has to say yes.
///
/// <para><see cref="Accepted"/> is the whole point. Ordinary breeding requires the caller to own both
/// parents, so consent is implicit; here the second parent belongs to someone else, and until they accept
/// nothing is billed (the invoices below are created at ACCEPT, not at proposal), nothing is owed, and
/// nothing can mint. The proposal is an offer, not an obligation.</para>
///
/// <para>Durable, unlike the other breed sessions, because it is the only one where a fee is owed to
/// ANOTHER PLAYER: losing this row across a restart would strand the proposer's paid sats with nothing left
/// able to name who they were owed to.</para>
/// </summary>
public class StudProposal
{
    public required string Id { get; init; }
    /// <summary>The proposer — pays both fees, and receives the child.</summary>
    public required string ProposerPlayerId { get; init; }
    /// <summary>The stud's owner — consents, and receives the stud fee. Pinned at proposal time.</summary>
    public required string StudOwnerPlayerId { get; init; }
    public required string ProposerHeroId { get; init; }
    public required string StudHeroId { get; init; }
    /// <summary>Committed at PROPOSAL, so the stud's owner consents to a breed whose randomness is already sealed.</summary>
    public required byte[] ServerSeed { get; init; }
    public required string CommitmentHex { get; init; }
    /// <summary>What the proposer offers the stud's owner (sats); 0 = a favour, not a sale.</summary>
    public required long StudFeeSats { get; init; }
    /// <summary>The escalating breed fee, priced at ACCEPT off the parents' combined breed count then.</summary>
    public long BreedFeeSats { get; set; }
    /// <summary>Treasury fee invoice for the breed itself. Null until the stud's owner accepts.</summary>
    public string? BreedFeeInvoiceId { get; set; }
    /// <summary>Treasury invoice for the stud fee, which the reveal then pays OUT to the stud's owner.
    /// Null until accepted, and null thereafter when no stud fee was offered.</summary>
    public string? StudFeeInvoiceId { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    /// <summary>The consent gate: set only by the stud's owner, and only once.</summary>
    public bool Accepted { get; set; }
    public bool Declined { get; set; }
    public bool Completed { get; set; }
    /// <summary>Latched (durably, BEFORE the sat moves) the moment the stud fee is paid out — so a reveal
    /// retried after a crash finishes the breed without paying the stud's owner a second time.</summary>
    public bool StudFeePaid { get; set; }
    public string? ChildHeroId { get; set; }
}

/// <summary>A hero merge (fusion) awaiting its escrow deposit. Base + sacrifice + fee are deposited into the escrow; reveal retires the inputs and mints the fused hero.</summary>
public class MergeSession
{
    public required string Id { get; init; }
    public required string PlayerId { get; init; }
    public required string BaseId { get; init; }
    public required string SacrificeId { get; init; }
    public required byte[] ServerSeed { get; init; }
    public required string CommitmentHex { get; init; }
    /// <summary>"treasury" (rung 1, server-executed) or "covenant" (rung 2, emulator-enforced mint).</summary>
    public string Mode { get; init; } = "treasury";
    /// <summary>Merge escrow address the player deposits base + sacrifice + fee into.</summary>
    public string? EscrowAddress { get; set; }
    /// <summary>The flat merge fee for this session (sats) — paid via the merge escrow.</summary>
    public long FeeSats { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool Completed { get; set; }
    public string? FusedHeroId { get; set; }
}

/// <summary>A death-match awaiting both stakes. Each player deposits their hero into their own escrow; on settle the loser's hero is permanently burned and the winner keeps theirs.</summary>
public class DeathMatchSession
{
    public required string Id { get; init; }
    public required string ChallengerPlayerId { get; init; }
    public required string DefenderPlayerId { get; init; }
    public required string ChallengerHeroId { get; init; }
    public required string DefenderHeroId { get; init; }
    public required byte[] ServerSeed { get; init; }
    public required string CommitmentHex { get; init; }
    /// <summary>The ONE joint escrow both players stake their hero into (covenant-v2).</summary>
    public string? JointEscrowAddress { get; set; }
    /// <summary>Each side's equipped-item snapshot at OPEN — the gear units staked alongside the heroes.</summary>
    public IReadOnlyList<string> ChallengerGearItemIds { get; init; } = [];
    public IReadOnlyList<string> DefenderGearItemIds { get; init; } = [];
    /// <summary>Per-character death-match fee invoices (a level-scaled sats sink); BOTH must be paid before settle.</summary>
    public string? ChallengerFeeInvoiceId { get; set; }
    public string? DefenderFeeInvoiceId { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool Accepted { get; set; }
    public bool Completed { get; set; }
    public string? WinnerHeroId { get; set; }
    /// <summary>Opt-in absorb mode: on a seed-driven roll the winner re-mints absorbing the loser's traits (6-leaf escrow); a failed roll is the classic keep.</summary>
    public bool Absorb { get; init; }
    /// <summary>The species control asset the absorbed hero mints under (absorb mode only).</summary>
    public string SpeciesId { get; init; } = "";

    /// <summary>Fight-time replay data (persisted at settle) so ANY spectator can watch + verify the
    /// death-match later — mirrors MatchSession. Null until settled.</summary>
    public BattleResult? Result { get; set; }
    public string? Nonce { get; set; }
    public string? EntropyHex { get; set; }
    /// <summary>The GameConfigVersion of the rules this was RESOLVED under, recorded at resolve time so the
    /// replay stays honest about its own rules. Null until resolved (and on anything resolved before
    /// stamping existed, which ran on GameConfig.Default).</summary>
    public string? ConfigVersion { get; set; }
    /// <summary>The ContentPackVersion of the gear and dungeons this was RESOLVED under, recorded at the
    /// same moment and for the same reason: item stats are combat inputs, so a replay rebuilt from
    /// different content is a different fight. Null on anything resolved before content stamping existed,
    /// which ran on the gear its binary compiled in.</summary>
    public string? ContentVersion { get; set; }
    public Shared.HeroDto? ChallengerSnapshot { get; set; }
    public Shared.HeroDto? DefenderSnapshot { get; set; }
}

/// <summary>An item purchase awaiting its invoice payment. Status: pending → delivering → claimed (delivery failures return to pending so a paid purchase is always claimable).</summary>
public class ItemPurchase
{
    public required string InvoiceId { get; init; }
    public required string PlayerId { get; init; }
    public required string ItemId { get; init; }
    public string Status { get; set; } = "pending";
    public string? ItemAssetId { get; set; }
    public string? DeliveryTxId { get; set; }
    public object Gate { get; } = new();
}

public class MatchSession
{
    public required string Id { get; init; }
    public required string ChallengerPlayerId { get; init; }
    public required string ChallengerHeroId { get; init; }
    public required string DefenderHeroId { get; init; }
    public required byte[] ServerSeed { get; init; }
    public required string CommitmentHex { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Stake each side escrows with the treasury; 0 = friendly match.</summary>
    public long WagerSats { get; init; }

    /// <summary>"invoice" or "covenant" — how stakes are held and settled.</summary>
    public string Mode { get; init; } = "invoice";

    /// <summary>Covenant mode: PER-PARTY escrow addresses each player stakes into.</summary>
    public string? EscrowChallengerAddress { get; set; }
    public string? EscrowDefenderAddress { get; set; }

    /// <summary>Covenant mode: when each party's timelocked refund unlocks — also the abandonment threshold for expiring a stale match. Null in invoice mode.</summary>
    public long? RefundAfterUnixSeconds { get; set; }

    /// <summary>Stake invoices the players' own wallets must pay (invoice mode; null for friendly matches).</summary>
    public string? ChallengerInvoiceId { get; init; }
    public string? DefenderInvoiceId { get; set; }

    /// <summary>Per-character match-fee invoices (a level-proportional sats sink paid to the treasury); BOTH must be paid before a staked fight. Null for friendly matches.</summary>
    public string? ChallengerFeeInvoiceId { get; set; }
    public string? DefenderFeeInvoiceId { get; set; }

    /// <summary>Owner of the defender hero at open time (must accept wagered matches).</summary>
    public string? DefenderPlayerId { get; set; }

    public string Status { get; set; } = "open"; // open | accepted | resolved
    public BattleResult? Result { get; set; }
    public string? EntropyHex { get; set; }
    /// <summary>The GameConfigVersion of the rules this was RESOLVED under, recorded at resolve time so the
    /// replay stays honest about its own rules. Null until resolved (and on anything resolved before
    /// stamping existed, which ran on GameConfig.Default).</summary>
    public string? ConfigVersion { get; set; }
    /// <summary>The ContentPackVersion of the gear and dungeons this was RESOLVED under, recorded at the
    /// same moment and for the same reason: item stats are combat inputs, so a replay rebuilt from
    /// different content is a different fight. Null on anything resolved before content stamping existed,
    /// which ran on the gear its binary compiled in.</summary>
    public string? ContentVersion { get; set; }
    public string? Nonce { get; set; }

    /// <summary>Fight-time hero snapshots (level + equipment as actually fought) — persisted so ANY
    /// spectator can replay + verify the resolved match later, even after the heroes change. Null until resolved.</summary>
    public Shared.HeroDto? ChallengerSnapshot { get; set; }
    public Shared.HeroDto? DefenderSnapshot { get; set; }
}

/// <summary>
/// A resting item offer in the discovery index. The covenant lives on-chain (the
/// seller deposits the item into the offer address; a buyer fulfils it from their
/// own wallet); this record is the server's searchable listing, reconciled
/// against on-chain truth. Status: pending (created, item not yet deposited) →
/// active (funded, buyable) → closed (sold or reclaimed).
/// </summary>
public class OfferListing
{
    public required string Id { get; init; }
    public required string SellerId { get; init; }
    /// <summary>"item" (a fungible equipment unit) or "hero" (a unique character asset).</summary>
    public string Kind { get; init; } = "item";
    /// <summary>Game item id for item offers; empty for hero offers.</summary>
    public required string ItemId { get; init; }
    /// <summary>Game hero id for hero offers; null for item offers.</summary>
    public string? HeroId { get; init; }
    public required long AskSats { get; init; }
    public required string OfferAddress { get; init; }
    /// <summary>The on-chain asset resting in the offer — the item's shared asset, or the hero's own unique asset.</summary>
    public required string ItemAssetId { get; init; }
    public required long OfferValueSats { get; init; }
    public required long RefundAfterUnixSeconds { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string Status { get; set; } = "pending";
    /// <summary>The marketplace fee this offer's COVENANT routes to the treasury when it sells (0 when
    /// disabled). Nothing is billed at listing — the seller absorbs it out of the ask, so an offer that
    /// never sells costs nothing and a sale cannot skip the cut.</summary>
    public long ListingFeeSats { get; init; }
    /// <summary>Whether the asset is currently observed resting at the offer address, refreshed on each
    /// reconcile. Tracked apart from <see cref="Status"/> because a fee-gated offer stays <c>pending</c>
    /// even after its asset has LEFT the seller's wallet — so <c>pending</c> alone cannot answer "is the
    /// item still held?", which is what the free-to-sell check needs.</summary>
    public bool AssetDeposited { get; set; }
}

/// <summary>A pending/resolved 3v3 squad match: two 3-hero lineups sharing one wager escrow (reused from the
/// duel flow), resolved by a positional best-of-3 relay. Mirrors <see cref="MatchSession"/> with lineups.</summary>
public class SquadMatchSession
{
    public required string Id { get; init; }
    public required string ChallengerPlayerId { get; init; }
    public required IReadOnlyList<string> ChallengerLineup { get; init; }
    public required IReadOnlyList<string> DefenderLineup { get; init; }
    public required byte[] ServerSeed { get; init; }
    public required string CommitmentHex { get; init; }
    public long WagerSats { get; init; }
    public string Mode { get; init; } = "covenant";
    public string? EscrowChallengerAddress { get; set; }
    public string? EscrowDefenderAddress { get; set; }
    public string? ChallengerInvoiceId { get; set; }
    public string? ChallengerFeeInvoiceId { get; set; }
    public string? DefenderInvoiceId { get; set; }
    public string? DefenderFeeInvoiceId { get; set; }
    public long? RefundAfterUnixSeconds { get; set; }
    public string? DefenderPlayerId { get; set; }
    public string Status { get; set; } = "open";
    public SquadResult? Result { get; set; }
    public IReadOnlyList<Shared.HeroDto>? ChallengerSnapshots { get; set; }
    public IReadOnlyList<Shared.HeroDto>? DefenderSnapshots { get; set; }
    public string? Nonce { get; set; }
    public string? EntropyHex { get; set; }
    /// <summary>The GameConfigVersion of the rules this was RESOLVED under, recorded at resolve time so the
    /// replay stays honest about its own rules. Null until resolved (and on anything resolved before
    /// stamping existed, which ran on GameConfig.Default).</summary>
    public string? ConfigVersion { get; set; }
    /// <summary>The ContentPackVersion of the gear and dungeons this was RESOLVED under, recorded at the
    /// same moment and for the same reason: item stats are combat inputs, so a replay rebuilt from
    /// different content is a different fight. Null on anything resolved before content stamping existed,
    /// which ran on the gear its binary compiled in.</summary>
    public string? ContentVersion { get; set; }
}

/// <summary>
/// A completed hero sale: which hero, between whom, and for how much. <paramref name="BuyerId"/> is null
/// when the sale was proven on-chain by a spend the server saw but nobody has since claimed the hero
/// under — see <see cref="Persistence.PersistedHeroSale"/> for why the two proofs know different things.
/// </summary>
public sealed record HeroSale(
    string OfferId, string HeroId, string SellerId, string? BuyerId,
    long AskSats, long ListingFeeSats, long SoldAtUnixSeconds);

/// <summary>A pending hero rename in the unique-name registry: the player pays the treasury fee, then
/// confirms to apply the claimed name. In-memory like the rest; a restart drops it with the fee marker.</summary>
public class RenameSession
{
    public required string HeroId { get; init; }
    public required string NewName { get; init; }
    /// <summary>The fee-invoice to pay; null when no rename fee is charged (then confirm applies immediately).</summary>
    public string? FeeInvoiceId { get; init; }
}

/// <summary>One entrant in a tournament bracket: the player, their hero, and the buy-in invoice they must pay.</summary>
public sealed class TournamentEntrant
{
    public required string PlayerId { get; init; }
    public required string HeroId { get; init; }
    public required string BuyInInvoiceId { get; init; }
}

/// <summary>A buy-in tournament bracket: entrants pay a buy-in into the treasury; once full it runs the pure
/// single-elimination resolver and pays the podium out of the pot minus the house rake. In-memory like the rest.</summary>
public sealed class TournamentSession
{
    public required string Id { get; init; }
    public required string OpenerPlayerId { get; init; }
    public required long BuyInSats { get; init; }
    public required int Size { get; init; }
    public required byte[] ServerSeed { get; init; }
    public required string CommitmentHex { get; init; }
    public List<TournamentEntrant> Entrants { get; } = new();
    public string Status { get; set; } = "open";   // open → full → resolved
    public ArkadeHeroes.Core.Combat.TournamentResult? Result { get; set; }
    /// <summary>Podium prizes paid at resolve (champion first) — surfaced for the Hall of Champions.</summary>
    public IReadOnlyList<long> Prizes { get; set; } = [];
    public string? Nonce { get; set; }
    public string? EntropyHex { get; set; }
    /// <summary>The GameConfigVersion of the rules this was RESOLVED under, recorded at resolve time so the
    /// replay stays honest about its own rules. Null until resolved (and on anything resolved before
    /// stamping existed, which ran on GameConfig.Default).</summary>
    public string? ConfigVersion { get; set; }
    /// <summary>The ContentPackVersion of the gear and dungeons this was RESOLVED under, recorded at the
    /// same moment and for the same reason: item stats are combat inputs, so a replay rebuilt from
    /// different content is a different fight. Null on anything resolved before content stamping existed,
    /// which ran on the gear its binary compiled in.</summary>
    public string? ContentVersion { get; set; }
    /// <summary>Entrant hero snapshots captured the moment the bracket FILLS — the locked fighting state
    /// the resolver runs over and any client re-runs (FairnessAudit.VerifyTournament), even after the
    /// heroes later change. NOT persisted: a restart-rehydrated bracket has none, can't honor its
    /// commitment, and lands in the strand refund instead.</summary>
    public IReadOnlyList<Shared.HeroDto>? EntrantSnapshots { get; set; }
    /// <summary>Domain-tagged hash of the canonical entrant set above, computed at the same fill instant
    /// (FairnessAudit.ComputeEntrantsCommitment) and published on the tournament DTO — so a client can pin
    /// the snapshots independently of the replay and a server can't substitute a genome/level/gear.</summary>
    public string? EntrantsCommitmentHex { get; set; }
}

/// <summary>In-process game state. v1 keeps everything in memory; the chain is the durable layer for heroes.
///
/// The optional <paramref name="persistence"/> is the treasury ledger's durability seam only — everything
/// else in here is saved by <see cref="GameService"/>. It defaults to none so a bare <c>new GameStore()</c>
/// (and an in-memory server, which gets the null implementation) behaves exactly as it always has.</summary>
public class GameStore(Persistence.IGameStatePersistence? persistence = null, ILogger<GameStore>? logger = null)
{
    public ConcurrentDictionary<string, Player> Players { get; } = new();
    public ConcurrentDictionary<string, Player> PlayersByToken { get; } = new();
    public ConcurrentDictionary<string, Hero> Heroes { get; } = new();

    private long _heroesMinted;
    /// <summary>Cumulative heroes minted since this server started — every starter, bred, fused and absorbed
    /// hero passes through the one mint choke point. NOT persisted: a "since boot" churn counter, meant to be
    /// read as a RATE (delta over time) against the burn rate, not as a lifetime total.</summary>
    public long HeroesMinted => Interlocked.Read(ref _heroesMinted);
    public void RecordMint() => Interlocked.Increment(ref _heroesMinted);

    /// <summary>Burns counted DIRECTLY at each burn site, not inferred as (minted − supply).
    ///
    /// The subtraction was correct only while heroes were volatile, so a restart zeroed minted and supply
    /// together. Heroes are durable now, so after a restart minted starts at 0 against a surviving supply of
    /// N and the subtraction clamps to 0 — hiding every real burn until mints exceed the whole population.
    /// The mint half of the gauge kept counting, so the card read "mints, no burns": the alarm state, from
    /// a healthy game. Counting at the source keeps both halves on the same footing (deltas over one uptime)
    /// and drops the old "every hero removal is a burn" assumption — a future non-burn removal simply won't
    /// call this.</summary>
    public long HeroesBurned => Interlocked.Read(ref _heroesBurned);
    public void RecordBurn() => Interlocked.Increment(ref _heroesBurned);
    private long _heroesBurned;

    // ── Hero durability: the dirty set the periodic flush drains ──
    private readonly ConcurrentDictionary<string, byte> _dirtyHeroes = new();

    /// <summary>Marks a hero's PROGRESSION (level/XP, equipment, cooldowns, breed count) as changed since
    /// the last flush. Called at every progression mutation point; identity events (mint, burn, transfer,
    /// rename) persist inline instead and never pass through here. Cheap and idempotent — a hero mutated
    /// ten times between flushes is still one save.</summary>
    public void MarkHeroDirty(string heroId) => _dirtyHeroes.TryAdd(heroId, 0);

    /// <summary>Drains the dirty set for one flush pass. A mark that lands DURING the drain either makes
    /// this pass's list or stays in the set for the next one — never lost, at worst deferred one interval.</summary>
    public IReadOnlyList<string> DrainDirtyHeroes()
    {
        var drained = new List<string>();
        foreach (var heroId in _dirtyHeroes.Keys)
            if (_dirtyHeroes.TryRemove(heroId, out _))
                drained.Add(heroId);
        return drained;
    }

    public ConcurrentDictionary<string, BreedingSession> Breedings { get; } = new();
    /// <summary>Cross-owner breed proposals awaiting (or holding) the stud owner's consent.</summary>
    public ConcurrentDictionary<string, StudProposal> StudProposals { get; } = new();
    // The Fancy discovery race: who FIRST bred a hero expressing each named Fancy set, plus how many have
    // ever been found. Pure bookkeeping — it never gates or changes an outcome, it just records the race.
    public ConcurrentDictionary<string, FancyDiscovery> FancyDiscoveries { get; } = new();
    public ConcurrentDictionary<string, int> FancyFindCount { get; } = new();
    /// <summary>heroId → the Fancy set it expresses and its EDITION: the Nth hero ever to express that set.
    /// Edition #1 is the discoverer; a low edition stays scarce however many turn up later.</summary>
    public ConcurrentDictionary<string, FancyEdition> FancyEditionByHero { get; } = new();

    /// <summary>Claim a Fancy set for its FIRST finder and stamp this hero's edition number. Called exactly
    /// once per hero (from the single point where a hero enters the store), and guarded so a repeat can't
    /// mint a second edition. TryAdd on the discovery means the first finder can never be displaced — later
    /// finds only take the next edition.</summary>
    /// <returns>The stamped fact if this hero was newly recorded, or <c>null</c> if it was already stamped
    /// (the re-number guard) — the caller persists a non-null result so the find survives a restart.</returns>
    public FancyFind? RecordFancyFind(string title, string heroId, string heroName, string ownerId, long unixSeconds)
    {
        if (FancyEditionByHero.ContainsKey(heroId)) return null;   // already stamped — never re-number a hero
        var edition = FancyFindCount.AddOrUpdate(title, 1, (_, n) => n + 1);
        FancyEditionByHero[heroId] = new FancyEdition(title, edition);
        FancyDiscoveries.TryAdd(title, new FancyDiscovery(title, heroId, heroName, ownerId, unixSeconds));
        return new FancyFind(title, heroId, heroName, ownerId, unixSeconds, edition);
    }

    /// <summary>Rehydrate one persisted Fancy find at boot. The edition is taken EXACTLY as stored (never
    /// re-derived), the per-title count advances so the next LIVE find takes the right number — without this
    /// a restart resets the count and the next hero of a set is minted as a second "#1" — and the edition-#1
    /// row restores the set's original discoverer. Idempotent per hero.</summary>
    public void LoadFancyFind(FancyFind find)
    {
        FancyEditionByHero[find.HeroId] = new FancyEdition(find.Title, find.Edition);
        FancyFindCount.AddOrUpdate(find.Title, find.Edition, (_, n) => Math.Max(n, find.Edition));
        if (find.Edition == 1)
            FancyDiscoveries[find.Title] = new FancyDiscovery(find.Title, find.HeroId, find.HeroName, find.OwnerId, find.UnixSeconds);
    }

    public ConcurrentDictionary<string, GauntletSession> Gauntlets { get; } = new();
    public ConcurrentDictionary<string, TrialsSession> Trials { get; } = new();
    /// <summary>Each hero's best endless-Trials waves-cleared to date — the personal-best leaderboard basis.</summary>
    public ConcurrentDictionary<string, int> TrialsBestByHero { get; } = new();

    public ConcurrentDictionary<string, MergeSession> Merges { get; } = new();

    public ConcurrentDictionary<string, DeathMatchSession> DeathMatches { get; } = new();
    public ConcurrentDictionary<string, MatchSession> Matches { get; } = new();
    public ConcurrentDictionary<string, SquadMatchSession> SquadMatches { get; } = new();
    public ConcurrentDictionary<string, ItemPurchase> ItemPurchases { get; } = new();
    public ConcurrentDictionary<string, OfferListing> Offers { get; } = new();

    /// <summary>Completed hero sales, keyed by the offer that settled each one — the marketplace history a
    /// closed offer cannot keep (closed rows are filtered out at boot, and <c>closed</c> cannot tell a sale
    /// from a seller's reclaim anyway). Read by the hero timeline; gates nothing.</summary>
    public ConcurrentDictionary<string, HeroSale> HeroSales { get; } = new();

    /// <summary>
    /// Records a sale once, and only ever ADDS to what is known about it.
    ///
    /// Two independent paths prove the same sale — the buyer claiming the hero (which knows WHO bought it,
    /// having just asked the chain whether they hold the asset) and reconcile observing the covenant's
    /// treasury leg (which knows a sale happened but not to whom) — and either can run first. So a repeat
    /// is a no-op EXCEPT when it carries a buyer the stored row is missing, which is the one case where the
    /// second write is worth more than the first. A buyer already recorded is never overwritten, and the
    /// price is never rewritten by anyone.
    /// </summary>
    /// <returns>The row to persist when this call changed anything, or null when it was a pure repeat.</returns>
    public HeroSale? RecordHeroSale(HeroSale sale)
    {
        var stored = HeroSales.AddOrUpdate(sale.OfferId, sale,
            (_, prev) => prev.BuyerId is null && sale.BuyerId is not null
                ? prev with { BuyerId = sale.BuyerId }
                : prev);

        // The insert landed — this call created the row.
        if (ReferenceEquals(stored, sale)) return stored;
        // Otherwise a row was already there. Worth a write only if the buyer now on it is the one THIS
        // call carried; a caller that knew no buyer, or found one already recorded, learned nothing.
        if (sale.BuyerId is not null && stored.BuyerId == sale.BuyerId) return stored;
        return null;
    }

    /// <summary>Rehydrate one durable sale at boot, exactly as stored.</summary>
    public void LoadHeroSale(HeroSale sale) => HeroSales[sale.OfferId] = sale;

    /// <summary>
    /// Heroes that were RESTORED from disk at boot rather than minted by this process.
    ///
    /// The receipt ledger is in-memory, so it begins at boot: for any hero in this set the timeline is a
    /// partial history however complete it looks, and the page says so. A hero minted during this process
    /// cannot have events older than the process, so its absence here is a real completeness guarantee.
    /// </summary>
    public ConcurrentDictionary<string, byte> RehydratedHeroes { get; } = new();

    public ConcurrentDictionary<string, RenameSession> Renames { get; } = new();
    public ConcurrentDictionary<string, TournamentSession> Tournaments { get; } = new();
    /// <summary>Serializes tournament join + resolve so a bracket can't be double-filled or double-paid.</summary>
    public SemaphoreSlim TournamentLock { get; } = new(1, 1);

    /// <summary>Outstanding single-use login nonces (hex) → issued time, for wallet-signature login.</summary>
    public ConcurrentDictionary<string, DateTimeOffset> LoginNonces { get; } = new();

    /// <summary>Server-side receipt cache, keyed by hero id (receipts are signed public facts; players hold their own copies).</summary>
    public ConcurrentDictionary<string, List<Shared.ProgressionReceiptDto>> ReceiptsByHero { get; } = new();

    // ── Season prize pool (in-memory, like the rest; a restart drops the marker + the winner-defining
    //    receipts together, which is what makes an in-memory settled-marker safe against double-pay) ──
    public int LastSettledSeason { get; set; }                                   // 0 = none settled yet
    public Shared.SeasonSettlementDto? LastSettlement { get; set; }              // snapshot of the most recent settled season
    public ConcurrentDictionary<int, long> SeasonFeeAccrual { get; } = new();    // seasonNumber → accrued sats

    // Economy telemetry: treasury OUTFLOW tallied by category ("daily"/"season"/"tournament"/"wager"/"squad").
    // Pure observability — recorded once per SUCCESSFUL payout; never gates or changes any behavior.
    public ConcurrentDictionary<string, long> TreasuryOutflowByTag { get; } = new();
    public async Task RecordOutflowAsync(string tag, long sats, CancellationToken ct = default)
    {
        TreasuryOutflowByTag.AddOrUpdate(tag, sats, (_, prev) => prev + sats);
        // Append-only under a surrogate id: a payout has no natural key and has never been deduped, and
        // giving it one now would silently drop the second of two identical legitimate payouts.
        await PersistFlowAsync(Guid.NewGuid().ToString("N"), Persistence.PersistedTreasuryFlow.Out, tag, sats, ct);
    }

    // Treasury INFLOW (fee captures) tallied by category. Deduped by invoice id, so a record call inside a
    // reconcile loop (e.g. the offer listing-fee latch) can never double-count. Pure observability.
    public ConcurrentDictionary<string, long> TreasuryInflowByTag { get; } = new();
    private readonly ConcurrentDictionary<string, byte> _talliedInflowInvoices = new();
    public async Task RecordInflowAsync(string invoiceId, string tag, long sats, CancellationToken ct = default)
    {
        if (!_talliedInflowInvoices.TryAdd(invoiceId, 0)) return;
        TreasuryInflowByTag.AddOrUpdate(tag, sats, (_, prev) => prev + sats);
        await PersistFlowAsync(invoiceId, Persistence.PersistedTreasuryFlow.In, tag, sats, ct);
    }

    /// <summary>Whether this invoice's fee has already been counted — the same already-counted set the
    /// inflow dedup reads, exposed so a caller can ask "was anything ever booked for this?" without
    /// re-deriving it from the by-tag totals (which group many invoices under one tag and cannot answer
    /// per-invoice). A pure read: it never records, and never changes what the dedup will do.</summary>
    public bool WasInflowTallied(string invoiceId) => _talliedInflowInvoices.ContainsKey(invoiceId);

    /// <summary>Rehydrate one durable treasury movement at boot, folding it into the by-tag total it belongs
    /// to. An INFLOW row also restores its invoice id to the already-counted set — the half that makes the
    /// totals safe to keep. Without it a durable total plus a re-delivered purchase (item purchases persist,
    /// and re-delivery after a crash is deliberate) would tally the same fee twice, and a treasury that
    /// over-reports its income reads as solvent when it is not.</summary>
    public void LoadTreasuryFlow(string id, string direction, string tag, long sats)
    {
        if (direction != Persistence.PersistedTreasuryFlow.In)
        {
            TreasuryOutflowByTag.AddOrUpdate(tag, sats, (_, prev) => prev + sats);
            return;
        }
        if (_talliedInflowInvoices.TryAdd(id, 0))
            TreasuryInflowByTag.AddOrUpdate(tag, sats, (_, prev) => prev + sats);
    }

    /// <summary>
    /// Writes the durable row behind a tally — and DELIBERATELY swallows a write failure, because these
    /// tallies sit on money paths that have already moved sats and whose catch blocks unwind in-memory state.
    /// A throw out of the daily claim's tally would restore <c>LastClaimDay</c> in memory over a durable
    /// consume and let the same day be paid twice; a throw out of the item claim's would flip a durably
    /// claimed purchase back to pending and re-deliver the asset. Telemetry must not be able to cause either.
    /// A lost row under-reports income until the next one lands, which is the survivable direction — and it
    /// cannot cause a double count, since the dedup marker is that same missing row.
    /// </summary>
    private async Task PersistFlowAsync(string id, string direction, string tag, long sats, CancellationToken ct)
    {
        if (persistence is null) return;
        try
        {
            await persistence.SaveTreasuryFlowAsync(id, direction, tag, sats, ct);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _ledgerWriteFailures);
            logger?.LogWarning(ex, "Treasury {Direction} of {Sats} sat tagged {Tag} was tallied but not persisted; "
                                   + "the durable total will under-report it after a restart.", direction, sats, tag);
        }
    }

    private long _ledgerWriteFailures;
    /// <summary>How many durable treasury-flow writes have been swallowed since this server started. The
    /// swallow above is deliberate and cannot be removed — throwing there would unwind a daily claim that
    /// has already paid, or re-deliver a durably claimed item — so the failure has no other way to surface.
    /// A warning nobody greps is not observability: this is the number that separates "a database that
    /// stopped accepting the ledger's rows" from "a quiet period", which are otherwise identical from the
    /// outside. NOT persisted, for the obvious reason that persistence is the thing that is failing.
    /// Any non-zero value means the durable totals are now behind the in-memory ones and a restart will
    /// lose the difference; it never means a sat moved wrongly.</summary>
    public long LedgerWriteFailures => Interlocked.Read(ref _ledgerWriteFailures);

    public readonly SemaphoreSlim SettleLock = new(1, 1);                        // serialize settlement

    // ── Per-key async mutexes: the money-path once-only guards ──
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _keyedLocks = new();

    /// <summary>
    /// Serializes one keyed flow (a player's daily/starter claim, one session's reveal/resolve):
    /// guard-check → chain effect → guard-set must be one atomic step, and the chain calls await,
    /// so this is the awaitable analogue of <see cref="ItemPurchase.Gate"/>. Dispose the returned
    /// handle to release. Semaphores accrue one per key and are never removed — keys are player and
    /// session ids, which this in-memory store already retains for the process lifetime anyway.
    /// </summary>
    public async Task<IDisposable> LockAsync(string key, CancellationToken ct = default)
    {
        var gate = _keyedLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        return new KeyedLockReleaser(gate);
    }

    private sealed class KeyedLockReleaser(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;
        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}
