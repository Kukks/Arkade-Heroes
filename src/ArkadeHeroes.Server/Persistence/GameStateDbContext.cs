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
/// SQLite store for the game state a restart must not lose. Scoped, for now, to the classes where losing a
/// row costs a player REAL SATS — an item they paid for but hadn't claimed, and a tournament buy-in paid
/// into a bracket that hadn't run. Heroes, offers and matches are deliberately absent: those are
/// reconcilable from the chain and the signed receipt chain, so persisting them would duplicate a better
/// source of truth.
/// </summary>
public class GameStateDbContext(DbContextOptions<GameStateDbContext> options) : DbContext(options)
{
    public DbSet<PersistedItemPurchase> ItemPurchases => Set<PersistedItemPurchase>();
    public DbSet<PersistedTournament> Tournaments => Set<PersistedTournament>();
    public DbSet<PersistedPlayer> Players => Set<PersistedPlayer>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<PersistedItemPurchase>().HasKey(x => x.InvoiceId);
        modelBuilder.Entity<PersistedTournament>().HasKey(x => x.Id);
        modelBuilder.Entity<PersistedPlayer>().HasKey(x => x.Id);
    }
}
