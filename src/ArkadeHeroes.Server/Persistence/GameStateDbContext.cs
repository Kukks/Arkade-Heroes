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
/// </summary>
public class PersistedPlayer
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required bool StarterClaimed { get; set; }
    public string? LoginPubKeyHex { get; set; }
    public required int StreakCount { get; set; }
    public int? LastClaimDay { get; set; }
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
/// named one). Offers and matches remain absent: offers ARE reconciled against on-chain truth, and a
/// resolved match's replay is a receipt-signed public fact.
/// </summary>
public class GameStateDbContext(DbContextOptions<GameStateDbContext> options) : DbContext(options)
{
    public DbSet<PersistedItemPurchase> ItemPurchases => Set<PersistedItemPurchase>();
    public DbSet<PersistedTournament> Tournaments => Set<PersistedTournament>();
    public DbSet<PersistedPlayer> Players => Set<PersistedPlayer>();
    public DbSet<PersistedFancyFind> FancyFinds => Set<PersistedFancyFind>();
    public DbSet<PersistedHero> Heroes => Set<PersistedHero>();
    public DbSet<PersistedTreasuryFlow> TreasuryFlows => Set<PersistedTreasuryFlow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<PersistedItemPurchase>().HasKey(x => x.InvoiceId);
        modelBuilder.Entity<PersistedTournament>().HasKey(x => x.Id);
        modelBuilder.Entity<PersistedPlayer>().HasKey(x => x.Id);
        modelBuilder.Entity<PersistedFancyFind>().HasKey(x => x.HeroId);
        modelBuilder.Entity<PersistedHero>().HasKey(x => x.Id);
        // Composite so an outflow surrogate can never occupy an invoice id's slot: an inflow insert that
        // collides is the "already counted" no-op, and that must never be able to eat a payout row.
        modelBuilder.Entity<PersistedTreasuryFlow>().HasKey(x => new { x.Direction, x.Id });
    }
}
