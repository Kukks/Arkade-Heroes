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
/// SQLite store for the game state a restart must not lose. Scoped, for now, to the classes where losing a
/// row costs a player REAL SATS — an item they paid for but hadn't claimed. Heroes, offers and matches are
/// deliberately absent: those are reconcilable from the chain and the signed receipt chain, so persisting
/// them would duplicate a better source of truth.
/// </summary>
public class GameStateDbContext(DbContextOptions<GameStateDbContext> options) : DbContext(options)
{
    public DbSet<PersistedItemPurchase> ItemPurchases => Set<PersistedItemPurchase>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<PersistedItemPurchase>().HasKey(x => x.InvoiceId);
    }
}
