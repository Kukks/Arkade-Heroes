using ArkadeHeroes.Core.Heroes;
using ArkadeHeroes.Server;
using ArkadeHeroes.Server.Persistence;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace ArkadeHeroes.Tests;

/// <summary>
/// A rename request and a rename confirm, racing on the same hero. The request's retarget branch reads the
/// paid session, and the confirm spends and deletes it — so if the request's write lands afterwards it
/// RESURRECTS a session whose invoice is paid and already spent. One fee, two applied renames.
///
/// <para>The in-memory half of that race predates the durable session; making the session durable is what
/// turned it from a process-lifetime hole into a permanent one, since the resurrected row now survives a
/// restart. Both halves are closed by the same per-hero lock.</para>
/// </summary>
public class RenameSessionRaceTests
{
    /// <summary>
    /// Holds <c>SaveRenameAsync</c> until a delete has been seen, which is precisely the interleaving that
    /// resurrects a spent session — deterministic, rather than hoping a hammer hits the window.
    ///
    /// <para>The wait is BOUNDED on purpose. With the per-hero lock in place the confirm cannot run while
    /// the request holds it, so the delete never arrives and an unbounded wait would deadlock the fixed
    /// server rather than pass on it.</para>
    /// </summary>
    private sealed class DeleteThenSavePersistence(IGameStatePersistence inner) : IGameStatePersistence
    {
        private readonly TaskCompletionSource _deleted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task SaveRenameAsync(RenameSession session, CancellationToken ct = default)
        {
            await Task.WhenAny(_deleted.Task, Task.Delay(TimeSpan.FromSeconds(2), ct));
            await inner.SaveRenameAsync(session, ct);
        }

        public Task DeleteRenameAsync(string heroId, CancellationToken ct = default)
        {
            _deleted.TrySetResult();
            return inner.DeleteRenameAsync(heroId, ct);
        }

        public Task LoadIntoAsync(GameStore store, CancellationToken ct = default) => inner.LoadIntoAsync(store, ct);
        public Task SaveItemPurchaseAsync(ItemPurchase purchase, CancellationToken ct = default) => inner.SaveItemPurchaseAsync(purchase, ct);
        public Task SaveTournamentAsync(TournamentSession session, CancellationToken ct = default) => inner.SaveTournamentAsync(session, ct);
        public Task SavePlayerAsync(Player player, CancellationToken ct = default) => inner.SavePlayerAsync(player, ct);
        public Task SaveFancyFindAsync(FancyFind find, CancellationToken ct = default) => inner.SaveFancyFindAsync(find, ct);
        public Task SaveHeroAsync(Hero hero, CancellationToken ct = default) => inner.SaveHeroAsync(hero, ct);
        public Task SaveHeroProgressionAsync(Hero hero, CancellationToken ct = default) => inner.SaveHeroProgressionAsync(hero, ct);
        public Task DeleteHeroAsync(string heroId, CancellationToken ct = default) => inner.DeleteHeroAsync(heroId, ct);
        public Task SaveOfferAsync(OfferListing offer, CancellationToken ct = default) => inner.SaveOfferAsync(offer, ct);
        public Task SaveStudProposalAsync(StudProposal proposal, CancellationToken ct = default) => inner.SaveStudProposalAsync(proposal, ct);
        public Task SaveHeroSaleAsync(HeroSale sale, CancellationToken ct = default) => inner.SaveHeroSaleAsync(sale, ct);
        public Task SaveHeroTombstoneAsync(HeroTombstone stone, CancellationToken ct = default) => inner.SaveHeroTombstoneAsync(stone, ct);
        public Task SaveHeroBidAsync(HeroBid bid, CancellationToken ct = default) => inner.SaveHeroBidAsync(bid, ct);
        public Task SaveTreasuryFlowAsync(string id, string direction, string tag, long sats, CancellationToken ct = default)
            => inner.SaveTreasuryFlowAsync(id, direction, tag, sats, ct);
    }

    [Fact]
    public async Task ARetargetRacingAConfirm_CannotResurrectTheSpentSession()
    {
        // Asserted across a RESTART, not on the live dictionary. The retarget writes store.Renames BEFORE
        // its blocked save, so the confirm's TryRemove clears the in-memory copy either way — it is the
        // DURABLE row that gets resurrected, and only a rehydrate can see it. Measuring the live
        // dictionary passes with and without the fix, which is worth stating: it was my first attempt.
        var dbPath = Path.Combine(Path.GetTempPath(), $"arkade-rename-race-{Guid.NewGuid():N}.db");
        try
        {
            string heroId;
            using (var first = HostOn(dbPath))
            {
                var (alice, aliceDto) = await first.RegisterAsync("Race-Rename");
                heroId = (await alice.ClaimStartersAsync())[0].Id;

                // One PAID session — what the retarget branch reuses and what a confirm spends.
                var quote = await alice.Heroes.RequestRenameAsync(heroId, new RenameHeroRequest("First Choice"));
                await alice.PayInvoiceAsync(quote.Fee!.InvoiceId);

                var svc = first.Services.GetRequiredService<GameService>();
                var player = first.Services.GetRequiredService<GameStore>().Players[aliceDto.PlayerId];

                var retarget = Task.Run(() => svc.RequestRenameAsync(player, heroId, "Second Choice", CancellationToken.None));
                var confirm = Task.Run(async () =>
                {
                    try { await svc.ConfirmRenameAsync(player, heroId, CancellationToken.None); }
                    catch (GameRuleException) { /* losing the race is legitimate; resurrecting is not */ }
                });
                await Task.WhenAll(retarget, confirm);
            }

            using var restarted = HostOn(dbPath);
            _ = restarted.CreateClient();
            Assert.Empty(restarted.Services.GetRequiredService<GameStore>().Renames);
        }
        finally
        {
            SqliteTestDb.ReleasePool(dbPath);
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch (IOException) { }
        }
    }

    private static WebApplicationFactory<Program> HostOn(string dbPath) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Game:StateDbPath", dbPath);
            b.UseSetting("Game:HeroRenameFeeSats", "500");
            b.ConfigureTestServices(s => s.AddSingleton<IGameStatePersistence>(sp =>
                new DeleteThenSavePersistence(new SqliteGameStatePersistence(
                    sp.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<GameStateDbContext>>()))));
        });
}
