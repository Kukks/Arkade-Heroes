using Microsoft.EntityFrameworkCore;

namespace ArkadeHeroes.Server.Persistence;

/// <summary>
/// A durable item purchase. Deliberately a SEPARATE row type from the in-memory <see cref="ItemPurchase"/>:
/// that one carries a lock object and init-only members that have no business in a schema, and keeping the
/// two apart means a domain tweak doesn't silently become a migration.
/// </summary>
public class PersistedItemPurchase
{
    public required string InvoiceId { get; set; }
    public required string PlayerId { get; set; }
    public required string ItemId { get; set; }
    public required string Status { get; set; }
    public string? ItemAssetId { get; set; }
    public string? DeliveryTxId { get; set; }
}

/// <summary>
/// A durable player identity. The bearer <c>Token</c> is deliberately NOT stored: it's a session
/// credential, not identity. A restart invalidates sessions and the wallet re-authenticates by signing a
/// login challenge (<c>LoginPubKeyHex</c> is the stable handle), which is both safer than keeping
/// credentials at rest and the behaviour players already expect from "sign in with your wallet".
///
/// <c>StarterClaimed</c> and <c>LastClaimDay</c> are the load-bearing fields: losing them would let a
/// returning player re-claim free starter heroes and re-claim the same day's faucet reward.
///
/// <c>TermsAcceptedVersion</c>/<c>TermsAcceptedAtUtc</c> are here because browser-local storage proves
/// nothing — it is one cache clear from gone, and it is the player's own machine. An acceptance of terms
/// that disclose permadeath and real-bitcoin loss is only worth something if it can be produced later.
/// </summary>
public class PersistedPlayer
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required bool StarterClaimed { get; set; }
    /// <summary>The starter claim's fee-invoice. Stored because it is paid before the heroes exist:
    /// losing it across a restart would re-bill a player who has already paid.</summary>
    public string? StarterFeeInvoiceId { get; set; }
    public string? LoginPubKeyHex { get; set; }
    public required int StreakCount { get; set; }
    public int? LastClaimDay { get; set; }
    public int? TermsAcceptedVersion { get; set; }
    public DateTimeOffset? TermsAcceptedAtUtc { get; set; }
}

/// <summary>
/// A durable tournament. <c>Result</c> and <c>Prizes</c> are deliberately NOT stored: a resolved bracket has
/// already paid out, so the only thing worth surviving a restart is an UNRESOLVED bracket whose entrants
/// have paid buy-ins. The resolved rows are kept purely as an audit marker (see the loader).
/// </summary>
public class PersistedTournament
{
    public required string Id { get; set; }
    public required string OpenerPlayerId { get; set; }
    public required long BuyInSats { get; set; }
    public required int Size { get; set; }
    public required byte[] ServerSeed { get; set; }
    public required string CommitmentHex { get; set; }
    public required string Status { get; set; }
    /// <summary>Entrants as JSON. They're a small collection always loaded with their parent, so a child
    /// table would buy nothing but a join.</summary>
    public required string EntrantsJson { get; set; }
}

/// <summary>
/// A durable stud proposal — the cross-owner breed and the one flow where sats are owed to ANOTHER PLAYER.
/// That is why it is here and the other breed sessions are not: the proposer pays the stud fee into the
/// treasury at accept and the reveal pays it out to the stud's owner, so a lost row would leave paid sats in
/// the treasury with nothing left able to name who they belong to.
///
/// <c>Accepted</c> and <c>StudFeePaid</c> are the load-bearing fields. The first is CONSENT: it must not be
/// possible for a restart to promote an un-consented proposal, nor to lose a consent already given and paid
/// against. The second is the once-only payout latch, written BEFORE the sat moves — losing it would let a
/// reveal retried after a crash pay the stud's owner twice out of a treasury that cannot print.
/// </summary>
public class PersistedStudProposal
{
    public required string Id { get; set; }
    public required string ProposerPlayerId { get; set; }
    public required string StudOwnerPlayerId { get; set; }
    public required string ProposerHeroId { get; set; }
    public required string StudHeroId { get; set; }
    public required byte[] ServerSeed { get; set; }
    public required string CommitmentHex { get; set; }
    public required long StudFeeSats { get; set; }
    public required long BreedFeeSats { get; set; }
    public string? BreedFeeInvoiceId { get; set; }
    public string? StudFeeInvoiceId { get; set; }
    public required DateTimeOffset CreatedAt { get; set; }
    public required bool Accepted { get; set; }
    public required bool Declined { get; set; }
    public required bool Completed { get; set; }
    public required bool StudFeePaid { get; set; }
    public string? ChildHeroId { get; set; }
}

/// <summary>
/// A durable Fancy find: which set a hero expresses, its edition number, and who found it. Append-only and
/// assigned-once — a hero is stamped exactly once and its row is never updated or deleted, so there is no
/// "how does this leave the durable set" question the money rows have. One row per stamped hero is the full
/// source of truth: the per-set count is the max edition, and the discoverer is the edition-#1 row.
/// </summary>
public class PersistedFancyFind
{
    public required string HeroId { get; set; }
    public required string Title { get; set; }
    public required string HeroName { get; set; }
    public required string OwnerId { get; set; }
    public required long UnixSeconds { get; set; }
    public required int Edition { get; set; }
}

/// <summary>
/// A durable hero. Like the purchase row, deliberately a SEPARATE type from the in-memory <see cref="Hero"/>
/// aggregate: that one carries a Genome struct and a live EquipmentLoadout that have no business in a schema.
/// The genome is stored as hex (it IS the hero — every trait derives from it) and the loadout as a JSON array
/// of equipped item ids (the slot is derivable: each catalog item knows its slot, and a loadout holds at most
/// one item per slot). The commit–reveal audit fields ride along so a rehydrated hero stays verifiable.
///
/// IDENTITY fields (owner, name, the immutables) are saved the moment they change — a hero must never
/// vanish or mis-own across a restart. PROGRESSION (level/XP, equipment, cooldowns, breed count) is flushed
/// periodically instead, so a crash loses at most one flush window of grinding, never the hero itself.
/// </summary>
public class PersistedHero
{
    public required string Id { get; set; }
    public required string OwnerId { get; set; }
    public required string Name { get; set; }
    public required string GenomeHex { get; set; }
    public required int Generation { get; set; }
    public string? ParentAId { get; set; }
    public string? ParentBId { get; set; }
    public required int Level { get; set; }
    public required long Xp { get; set; }
    public required int BreedCount { get; set; }
    public DateTimeOffset? BreedCooldownUntil { get; set; }
    public DateTimeOffset? GauntletCooldownUntil { get; set; }
    public required string EquipmentJson { get; set; }
    public string? EntropyHex { get; set; }
    public string? ServerSeedHex { get; set; }
    public string? PlayerNonce { get; set; }
    public string? AssetId { get; set; }
    public string? MintArkTxId { get; set; }
}

/// <summary>
/// A durable marketplace offer. The offer COVENANT's params were always durable (the chain service stores them
/// under <c>offer:{id}</c>), so a restart never destroyed the escrowed asset — but the game-side row linking
/// seller to offer id lived only in memory, and without it nothing could NAME the offer to reclaim it: the
/// market stopped listing it and the seller was never told it existed. Recoverable in principle and
/// undiscoverable in practice is indistinguishable from lost. Hero offers made it sharper still — heroes are
/// durable, so a restart left the game believing a seller owned a hero whose asset was in fact escrowed.
///
/// <c>CreatedAt</c> is stored because it is the market's sort key (and the "just changed hands" strip's); a
/// rehydrated offer defaulting to boot time would silently reshuffle both.
///
/// <c>AssetDeposited</c> is deliberately NOT stored: it is a pure observation of the chain, re-derived on every
/// reconcile, so a stored copy could only ever be a staler answer than the one the chain already gives. It
/// rehydrates false, which is the CONSERVATIVE direction for the only rule that reads it — the free-to-sell
/// check counts an undeposited offer as still reserving its unit, and that check reconciles first anyway.
/// </summary>
public class PersistedOffer
{
    public required string Id { get; set; }
    public required string SellerId { get; set; }
    /// <summary>"item" or "hero" — see <see cref="OfferListing.Kind"/>.</summary>
    public required string Kind { get; set; }
    public required string ItemId { get; set; }
    public string? HeroId { get; set; }
    public required long AskSats { get; set; }
    public required string OfferAddress { get; set; }
    public required string ItemAssetId { get; set; }
    public required long OfferValueSats { get; set; }
    public required long RefundAfterUnixSeconds { get; set; }
    public required DateTimeOffset CreatedAt { get; set; }
    public required string Status { get; set; }
    /// <summary>Load-bearing on the money side: it is the cut the covenant routes to the treasury on a sale,
    /// and the amount <c>ReconcileOfferAsync</c> books when it observes the offer close.</summary>
    public required long ListingFeeSats { get; set; }
}

/// <summary>
/// A hero that CHANGED HANDS, and for how much — the one marketplace fact nothing else could answer.
///
/// The offer row already holds the ask and the seller, so this looks redundant until you follow what
/// happens to it. A sold offer is closed, and <c>LoadIntoAsync</c> filters closed rows out on purpose:
/// they hold no escrowed asset and rehydrating them would only regrow the market's live set forever. So
/// the price a hero fetched survived exactly as long as the process did. Worse, <c>closed</c> conflates
/// SOLD with RECLAIMED — the seller pulling their own listing lands in the same state — so even a
/// surviving offer row cannot say whether a trade happened at all. And the BUYER was never written
/// anywhere: <c>ClaimPurchasedHeroAsync</c> moves the hero to them and keeps no record that it did.
///
/// One row per SALE, keyed by the offer it settled, so this is append-only and once-only by
/// construction: the two places that can prove a sale (the buyer's claim, and reconcile observing the
/// covenant's treasury leg) both write under the same key, and the second is a no-op. It books no sats
/// and gates nothing — losing a row costs a line of history, never money.
///
/// <c>BuyerId</c> is nullable because the two proofs know different things. A claim knows exactly who
/// bought it (the chain was just asked whether THEY hold the asset); reconcile only knows the covenant
/// paid out, not to whom. A later claim fills the null in — the write only ever ADDS what it knows and
/// never overwrites a buyer already recorded.
/// </summary>
public class PersistedHeroSale
{
    /// <summary>The offer this sale settled — the primary key, and so the once-only marker.</summary>
    public required string OfferId { get; set; }
    public required string HeroId { get; set; }
    public required string SellerId { get; set; }
    /// <summary>Null when the sale was proven on-chain but the buyer had not identified themselves.</summary>
    public string? BuyerId { get; set; }
    /// <summary>What the buyer paid — the sticker ask. The seller nets this minus the listing fee.</summary>
    public required long AskSats { get; set; }
    public required long ListingFeeSats { get; set; }
    public required long SoldAtUnixSeconds { get; set; }
}

/// <summary>
/// One treasury movement — a fee captured (<see cref="In"/>) or a payout made (<see cref="Out"/>). The ROWS
/// are the record and the by-tag totals are GROUPED from them at boot, never stored: a stored total can drift
/// from the movements it claims to summarise, a derived one cannot.
///
/// The key is load-bearing on the inflow side. <c>Id</c> is the invoice id, so the primary key IS the
/// "already counted" set — and rehydrating it is the whole point. Persisting the totals ALONE would be worse
/// than not persisting at all: item purchases are durable and re-delivery after a crash is deliberate, so the
/// same invoice legitimately reaches the tally twice across a restart, and a surviving total with a lost
/// dedup set would count it twice. Double-counted INCOME is the unsurvivable direction for a treasury holding
/// real bitcoin — it makes an insolvent treasury read as solvent.
///
/// Outflow has no natural key and is NOT deduped (it never was): those rows are append-only under a surrogate
/// id. The key is composite so that surrogate can never collide with an invoice id and silently swallow a
/// payout as an "already counted" duplicate.
/// </summary>
public class PersistedTreasuryFlow
{
    public const string In = "in";
    public const string Out = "out";

    /// <summary>Inflow: the invoice id, which is what makes this row the dedup marker. Outflow: a surrogate.</summary>
    public required string Id { get; set; }
    /// <summary><see cref="In"/> or <see cref="Out"/>.</summary>
    public required string Direction { get; set; }
    public required string Tag { get; set; }
    public required long Sats { get; set; }
}

/// <summary>
/// SQLite store for the game state a restart must not lose. Most rows are here because losing one costs a
/// player REAL SATS — an item they paid for but hadn't claimed, a tournament buy-in paid into a bracket that
/// hadn't run. Fancy finds are the exception: they cost no sats, but they carry an irreplaceable scarcity
/// claim ("first to breed this set, ever"), and losing the per-set count would let a restart mint a SECOND
/// "#1" of a set — so the promise that #1 is forever needs disk, not just RAM. Heroes are here because the
/// "reconcilable from the chain" story never materialized — IChainService can't enumerate a player's heroes
/// back, so without a row a restart lost every character players own (and stranded every open bracket that
/// named one). Offers joined them once it was clear that "reconciled against on-chain truth" is not the same
/// as durable: reconcile can only re-check an offer it still has the ID of, so a lost row left the escrowed
/// asset on-chain with nothing left able to name it. Matches remain absent — a resolved match's replay is a
/// receipt-signed public fact.
/// </summary>
public class GameStateDbContext(DbContextOptions<GameStateDbContext> options) : DbContext(options)
{
    public DbSet<PersistedItemPurchase> ItemPurchases => Set<PersistedItemPurchase>();
    public DbSet<PersistedTournament> Tournaments => Set<PersistedTournament>();
    public DbSet<PersistedPlayer> Players => Set<PersistedPlayer>();
    public DbSet<PersistedFancyFind> FancyFinds => Set<PersistedFancyFind>();
    public DbSet<PersistedHero> Heroes => Set<PersistedHero>();
    public DbSet<PersistedOffer> Offers => Set<PersistedOffer>();
    public DbSet<PersistedStudProposal> StudProposals => Set<PersistedStudProposal>();
    public DbSet<PersistedHeroSale> HeroSales => Set<PersistedHeroSale>();
    public DbSet<PersistedTreasuryFlow> TreasuryFlows => Set<PersistedTreasuryFlow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<PersistedItemPurchase>().HasKey(x => x.InvoiceId);
        modelBuilder.Entity<PersistedTournament>().HasKey(x => x.Id);
        modelBuilder.Entity<PersistedPlayer>().HasKey(x => x.Id);
        modelBuilder.Entity<PersistedFancyFind>().HasKey(x => x.HeroId);
        modelBuilder.Entity<PersistedHero>().HasKey(x => x.Id);
        modelBuilder.Entity<PersistedOffer>().HasKey(x => x.Id);
        modelBuilder.Entity<PersistedStudProposal>().HasKey(x => x.Id);
        // Keyed by the OFFER, not a surrogate: one offer settles at most one sale, so the key IS the
        // "already recorded" set and the second prover of the same sale writes nothing.
        modelBuilder.Entity<PersistedHeroSale>().HasKey(x => x.OfferId);
        // Composite so an outflow surrogate can never occupy an invoice id's slot: an inflow insert that
        // collides is the "already counted" no-op, and that must never be able to eat a payout row.
        modelBuilder.Entity<PersistedTreasuryFlow>().HasKey(x => new { x.Direction, x.Id });
    }
}
