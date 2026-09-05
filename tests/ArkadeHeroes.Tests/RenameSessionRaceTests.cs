using ArkadeHeroes.Core.Heroes;
using ArkadeHeroes.Server;
using ArkadeHeroes.Server.Persistence;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
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
    /// Drives the exact interleaving instead of hoping a hammer hits it: the SECOND <c>SaveRenameAsync</c>
    /// — the retarget's, the setup's being the first — announces that it has been entered, then waits for a
    /// delete. So the test can hold the retarget precisely inside its save while it releases the confirm.
    ///
    /// <para>The wait is BOUNDED on purpose. With the per-hero lock in place the confirm cannot run while
    /// the request holds it, so the delete never arrives; an unbounded wait would deadlock the FIXED server
    /// rather than pass on it.</para>
    /// </summary>
    private sealed class RaceProbePersistence(IGameStatePersistence inner) : IGameStatePersistence
    {
        private readonly TaskCompletionSource _deleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _saves;

        /// <summary>Completes once the retarget is parked inside its save.</summary>
        public Task RetargetIsParked => _entered.Task;

        public async Task SaveRenameAsync(RenameSession session, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _saves) >= 2)
            {
                _entered.TrySetResult();
                await Task.WhenAny(_deleted.Task, Task.Delay(TimeSpan.FromSeconds(2), ct));
            }
            await inner.SaveRenameAsync(session, ct);
        }

        public Task DeleteRenameAsync(string heroId, CancellationToken ct = default)
        {
            _deleted.TrySetResult();
            return inner.DeleteRenameAsync(heroId, ct);
        }

        public Task SaveGauntletAsync(GauntletSession session, CancellationToken ct = default) => inner.SaveGauntletAsync(session, ct);
        public Task DeleteGauntletAsync(string gauntletId, CancellationToken ct = default) => inner.DeleteGauntletAsync(gauntletId, ct);
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
    public async Task ARetargetParkedMidSave_CannotResurrectTheSessionAConfirmSpent()
    {
        // Asserted across a RESTART, not on the live dictionary. The retarget writes store.Renames BEFORE
        // its save, so the confirm's TryRemove clears the in-memory copy either way — it is the DURABLE row
        // that gets resurrected, and only a rehydrate can see it. Asserting on the live dictionary passes
        // with and without the fix; that was my first attempt at this test.
        var dbPath = Path.Combine(Path.GetTempPath(), $"arkade-rename-race-{Guid.NewGuid():N}.db");
        try
        {
            string heroId, paidInvoiceId;
            RaceProbePersistence? probe = null;

            using (var first = HostOn(dbPath, p => probe = p))
            {
                var (alice, aliceDto) = await first.RegisterAsync("Race-Rename");
                heroId = (await alice.ClaimStartersAsync())[0].Id;

                // One PAID session — what the retarget branch reuses and what a confirm spends.
                var quote = await alice.Heroes.RequestRenameAsync(heroId, new RenameHeroRequest("First Choice"));
                paidInvoiceId = quote.Fee!.InvoiceId;
                await alice.PayInvoiceAsync(paidInvoiceId);

                var svc = first.Services.GetRequiredService<GameService>();
                var player = first.Services.GetRequiredService<GameStore>().Players[aliceDto.PlayerId];

                var retarget = Task.Run(() => svc.RequestRenameAsync(player, heroId, "Second Choice", CancellationToken.None));
                // Release the confirm only once the retarget is parked mid-save. Without this the winner is
                // whichever task the scheduler picks — the reason the first version of this test passed
                // locally and failed on CI.
                await probe!.RetargetIsParked.WaitAsync(TimeSpan.FromSeconds(10));
                var confirm = Task.Run(async () =>
                {
                    try { await svc.ConfirmRenameAsync(player, heroId, CancellationToken.None); }
                    catch (GameRuleException) { /* losing the race is legitimate; resurrecting is not */ }
                });
                await Task.WhenAll(retarget, confirm);
            }

            using var restarted = HostOn(dbPath, _ => { });
            _ = restarted.CreateClient();

            // A surviving session is only wrong if it still holds the SPENT invoice. A fresh request that
            // opens its own unpaid one is an ordinary second rename, which the player still has to pay for.
            var surviving = restarted.Services.GetRequiredService<GameStore>().Renames;
            if (surviving.TryGetValue(heroId, out var stale))
                Assert.NotEqual(paidInvoiceId, stale.FeeInvoiceId);
        }
        finally
        {
            SqliteTestDb.ReleasePool(dbPath);
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch (IOException) { }
        }
    }

    private static WebApplicationFactory<Program> HostOn(string dbPath, Action<RaceProbePersistence> capture) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Game:StateDbPath", dbPath);
            b.UseSetting("Game:HeroRenameFeeSats", "500");
            b.ConfigureTestServices(s => s.AddSingleton<IGameStatePersistence>(sp =>
            {
                var probe = new RaceProbePersistence(new SqliteGameStatePersistence(
                    sp.GetRequiredService<IDbContextFactory<GameStateDbContext>>()));
                capture(probe);
                return probe;
            }));
        });
}
