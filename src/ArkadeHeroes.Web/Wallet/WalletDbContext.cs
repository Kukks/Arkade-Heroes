using Microsoft.EntityFrameworkCore;
using NArk.Storage.EfCore;

namespace ArkadeHeroes.Web.Wallet;

/// <summary>
/// The in-browser wallet's EF Core store (SQLite via Bit.Besql → browser storage). Holds the
/// player's keys, VTXOs, contracts, intents — everything the non-custodial wallet needs, all in the tab.
/// </summary>
public class WalletDbContext(DbContextOptions<WalletDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // SQLite-backed: opt into ticks-based DateTimeOffset so ORDER BY on those columns works.
        modelBuilder.ConfigureArkEntities(o => o.StoreDateTimeOffsetAsTicks = true);
        modelBuilder.ConfigureArkPaymentEntities(o => o.StoreDateTimeOffsetAsTicks = true);
        modelBuilder.ConfigureArkExitEntities(o => o.StoreDateTimeOffsetAsTicks = true);
    }
}
