using Microsoft.EntityFrameworkCore;
using NArk.Storage.EfCore;

namespace ArkadeHeroes.Chain.NArk;

/// <summary>Game-owned key/value row for chain bookkeeping (treasury wallet id, species asset id, player wallet map).</summary>
public class GameChainKv
{
    public required string Key { get; set; }
    public required string Value { get; set; }
}

/// <summary>
/// SQLite context hosting both the NArk SDK entities (VTXOs, contracts,
/// wallets, intents, swaps) and the game's chain bookkeeping table.
/// </summary>
public class GameArkDbContext(DbContextOptions<GameArkDbContext> options) : DbContext(options)
{
    public DbSet<GameChainKv> ChainKv => Set<GameChainKv>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<GameChainKv>().HasKey(x => x.Key);
        // SQLite-backed: ticks storage so ORDER BY DateTimeOffset works (see SDK docs/articles/storage.md).
        modelBuilder.ConfigureArkEntities(o => o.StoreDateTimeOffsetAsTicks = true);
    }
}
