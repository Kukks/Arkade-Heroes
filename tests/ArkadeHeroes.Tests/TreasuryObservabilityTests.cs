using ArkadeHeroes.Chain;
using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Server;
using ArkadeHeroes.Server.Persistence;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArkadeHeroes.Tests;

/// <summary>
/// Two treasury-accounting faults that FAIL SAFE — both under-report, never over-report — but until now
/// failed SILENTLY, which is the part that makes them dangerous: a persistent fault and a quiet trading
/// day produce byte-identical output, so nobody can tell one from the other.
///
/// The first is the item-offer sale detector. It decides SOLD vs RECLAIMED by matching the treasury's
/// fee output against the transaction that spent the offer; if that match ever stops firing, listing fees
/// quietly stop being booked and the totals simply flatten. The second is the durable treasury-flow
/// write, which is deliberately swallowed (throwing there would re-pay a daily claim or re-deliver a paid
/// item — see GameStore.PersistFlowAsync); a database that has stopped accepting writes therefore looks
/// exactly like a database nothing has been written to.
///
/// These gauges cannot diagnose either fault on their own — most unbooked closes really are reclaims —
/// but they make the fault SHAPED: a count climbing against flat booked income is the signal. They are
/// additive-only, and the last test here is the one that pins that: they never gate or alter a decision
/// about money.
/// </summary>
public class TreasuryObservabilityTests
{
    const long Fee = 500;
    const long Ask = 3_000;

    static WebApplicationFactory<Program> FactoryWithFee(long fee) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.Configure<GameOptions>(o => o.OfferListingFeeSats = fee)));

    static async Task<EconomyHealthDto> HealthAsync(ArkadeHeroesClient c) => await c.Economy.HealthAsync();

    /// <summary>
    /// The tripwire's TRUE case: a seller takes an unsold listing back. Nothing sold, so nothing is
    /// booked — correct — and the offer lands closed holding a fee that was never collected. That is
    /// indistinguishable, from the server's side, from a sale the detector failed to attribute, so it is
    /// counted rather than ignored. This is the legitimate half, and it is expected to be the common one.
    /// </summary>
    [Fact]
    public async Task AReclaimedFeeBearingOffer_CountsAsClosedButUnbooked()
    {
        using var factory = FactoryWithFee(Fee);
        var (seller, _) = await factory.RegisterAsync("Obs-Reclaim");
        await seller.BuyItemAsync("rusty-blade");

        var before = await HealthAsync(seller);
        Assert.Equal(0, before.UnbookedClosedFeeOffers);

        var offer = await seller.Offers.CreateItemAsync(new CreateOfferRequest("rusty-blade", Ask));
        Assert.True(offer.ListingFeeSats > 0, "this test needs a fee-bearing listing to have anything to miss");
        await seller.Dev.FundOfferAsync(new { OfferId = offer.OfferId });
        await seller.Offers.ListAsync();     // reconcile — the deposit makes the listing active
        await seller.Dev.ReclaimOfferAsync(new { OfferId = offer.OfferId });
        await seller.Offers.ListAsync();     // reconcile — where the offer closes

        var after = await HealthAsync(seller);
        Assert.Equal(1, after.UnbookedClosedFeeOffers);
        // …and the reclaim booked nothing, which is the behaviour the counter exists to make visible
        // rather than to change.
        Assert.Equal(before.InflowByTag.GetValueOrDefault("listing"),
            after.InflowByTag.GetValueOrDefault("listing"));
    }

    /// <summary>
    /// The FALSE case, and the one that gives the gauge its meaning: a real sale is attributed and booked,
    /// so it must NOT show up as unbooked. Without this half a steadily climbing count would say nothing —
    /// every closed offer would raise it and the signal would be indistinguishable from ordinary trade.
    /// </summary>
    [Fact]
    public async Task ABookedSale_DoesNotCountAsUnbooked()
    {
        using var factory = FactoryWithFee(Fee);
        var (seller, _) = await factory.RegisterAsync("Obs-Sale-Seller");
        var (buyer, _) = await factory.RegisterAsync("Obs-Sale-Buyer");
        await seller.BuyItemAsync("rusty-blade");

        var offer = await seller.Offers.CreateItemAsync(new CreateOfferRequest("rusty-blade", Ask));
        await seller.Dev.FundOfferAsync(new { OfferId = offer.OfferId });
        await buyer.Offers.ListAsync();
        var bookedBefore = (await HealthAsync(seller)).InflowByTag.GetValueOrDefault("listing");

        await buyer.Dev.FulfillOfferAsync(new { OfferId = offer.OfferId });
        await buyer.Offers.ListAsync();      // reconcile — where the sale is attributed and booked

        var after = await HealthAsync(seller);
        Assert.Equal(bookedBefore + offer.ListingFeeSats, after.InflowByTag.GetValueOrDefault("listing"));
        Assert.Equal(0, after.UnbookedClosedFeeOffers);   // booked ⇒ not unbooked
    }

    /// <summary>
    /// A fee-free listing has nothing to attribute either way — the covenant carries no treasury leg, so a
    /// sale and a reclaim spend identically. Counting those would flood the gauge with closes that could
    /// never have been booked, and the one signal it carries would drown.
    /// </summary>
    [Fact]
    public async Task AFeeFreeOffer_IsNeverCountedAsUnbooked()
    {
        using var factory = FactoryWithFee(0);
        var (seller, _) = await factory.RegisterAsync("Obs-Free-Seller");
        await seller.BuyItemAsync("rusty-blade");

        var offer = await seller.Offers.CreateItemAsync(new CreateOfferRequest("rusty-blade", Ask));
        Assert.Equal(0, offer.ListingFeeSats);
        await seller.Dev.FundOfferAsync(new { OfferId = offer.OfferId });
        await seller.Offers.ListAsync();
        await seller.Dev.ReclaimOfferAsync(new { OfferId = offer.OfferId });
        await seller.Offers.ListAsync();

        var after = await HealthAsync(seller);
        Assert.Equal(1, after.ClosedOfferCount);          // it really did close…
        Assert.Equal(0, after.UnbookedClosedFeeOffers);   // …and it is still not a missed booking
    }

    /// <summary>
    /// The count gives the fault a shape; the LOG line is what names the individual offer an operator then
    /// has to go and look at, so it has to carry the offer id and it has to be at warning — a tripwire that
    /// only moves a number tells you something is wrong without ever telling you where.
    /// </summary>
    [Fact]
    public async Task AnUnbookedClose_IsLoggedAtWarning_NamingTheOffer()
    {
        var sink = new CapturingLoggerProvider();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.ConfigureServices(s => s.Configure<GameOptions>(o => o.OfferListingFeeSats = Fee));
            b.ConfigureLogging(l => l.AddProvider(sink).SetMinimumLevel(LogLevel.Warning));
        });
        var (seller, _) = await factory.RegisterAsync("Obs-Logged");
        await seller.BuyItemAsync("rusty-blade");

        var offer = await seller.Offers.CreateItemAsync(new CreateOfferRequest("rusty-blade", Ask));
        await seller.Dev.FundOfferAsync(new { OfferId = offer.OfferId });
        await seller.Offers.ListAsync();
        await seller.Dev.ReclaimOfferAsync(new { OfferId = offer.OfferId });
        await seller.Offers.ListAsync();

        var warning = Assert.Single(sink.Warnings, w => w.Contains(offer.OfferId));
        Assert.Contains("no sale could be attributed", warning);

        // And it is emitted ONCE, at the transition — not on every list call. A tripwire that re-fires on
        // every poll is one an operator learns to filter out, which is the same as not having it.
        await seller.Offers.ListAsync();
        await seller.Offers.ListAsync();
        Assert.Single(sink.Warnings, w => w.Contains(offer.OfferId));
    }

    /// <summary>
    /// The persistence swallow, made countable. GameStore.PersistFlowAsync catches a failed ledger write on
    /// purpose — a throw there would unwind a daily claim that has already paid, or flip a durably claimed
    /// item purchase back to pending and re-deliver the on-chain asset. But a logged warning nobody greps
    /// is not observability: a database that has silently stopped accepting writes must be visible, not
    /// inferred later from a durable total that drifted away from the in-memory one.
    /// </summary>
    [Fact]
    public async Task AFailedLedgerWrite_IsCounted_AndStillTalliedInMemory()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureTestServices(s => s.AddSingleton<IGameStatePersistence>(new RefusingLedgerPersistence())));
        var store = factory.Services.GetRequiredService<GameStore>();
        Assert.Equal(0, store.LedgerWriteFailures);

        await store.RecordInflowAsync("obs-inv-1", "item", 500);
        await store.RecordOutflowAsync("daily", 900);

        Assert.Equal(2, store.LedgerWriteFailures);           // both writes were refused, and both were seen
        Assert.Equal(500, store.TreasuryInflowByTag["item"]); // the in-memory tally is unharmed…
        Assert.Equal(900, store.TreasuryOutflowByTag["daily"]);
    }

    /// <summary>The same count, on the operator's actual instrument panel rather than only on the store.</summary>
    [Fact]
    public async Task TheLedgerFailureCount_ReachesTheEconomyHealthCard()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureTestServices(s => s.AddSingleton<IGameStatePersistence>(new RefusingLedgerPersistence())));
        var (alice, _) = await factory.RegisterAsync("Obs-Health");
        Assert.Equal(0, (await HealthAsync(alice)).LedgerWriteFailures);

        await alice.BuyItemAsync("rusty-blade");   // a real fee capture, whose ledger row is refused

        Assert.True((await HealthAsync(alice)).LedgerWriteFailures > 0);
    }

    /// <summary>
    /// THE property both gauges have to hold: they are instruments, not valves. A ledger database refusing
    /// every single write must change nothing about what the game pays, charges, or delivers — the counter
    /// climbs and the money path runs exactly as it does with a healthy database. If observability could
    /// ever alter a money decision it would be a liability rather than a diagnostic, and the safe-failure
    /// property this whole design rests on ("a lost row under-reports, and can never double-count") would
    /// stop being true.
    /// </summary>
    [Fact]
    public async Task TheCounters_NeverGateOrAlterAMoneyDecision()
    {
        var healthy = new InMemoryChainService();
        var refused = new InMemoryChainService();
        using var control = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Game:DailyRewardEnabled", "true");
            b.ConfigureTestServices(s => s.AddSingleton<IChainService>(healthy));
        });
        using var broken = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Game:DailyRewardEnabled", "true");
            b.ConfigureTestServices(s =>
            {
                s.AddSingleton<IChainService>(refused);
                s.AddSingleton<IGameStatePersistence>(new RefusingLedgerPersistence());
            });
        });

        // The same money path on both: buy an item (a fee IN), then claim the daily reward (a payout OUT).
        static async Task<(long Spent, long Awarded, long Booked, long Paid)> RunAsync(
            WebApplicationFactory<Program> factory, string name)
        {
            var (player, _) = await factory.RegisterAsync(name);
            await player.ClaimStartersAsync();
            var before = (await player.Players.MeAsync()).BalanceSats;
            await player.BuyItemAsync("rusty-blade");
            var afterBuy = (await player.Players.MeAsync()).BalanceSats;
            var claim = await player.Daily.ClaimAsync();
            var health = await player.Economy.HealthAsync();
            return (before - afterBuy, claim.AwardedSats,
                health.InflowByTag.GetValueOrDefault("item"), health.OutflowByTag.GetValueOrDefault("daily"));
        }

        var expected = await RunAsync(control, "Obs-Control");
        var actual = await RunAsync(broken, "Obs-Broken");

        Assert.Equal(expected, actual);   // sats charged, sats awarded, and both tallies — all identical
        Assert.True(factoryStore(broken).LedgerWriteFailures > 0, "the broken host really did refuse its writes");
        Assert.Equal(0, factoryStore(control).LedgerWriteFailures);

        static GameStore factoryStore(WebApplicationFactory<Program> f) => f.Services.GetRequiredService<GameStore>();
    }

    /// <summary>Collects warning-level log messages so a test can assert on what an operator would see.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _warnings = [];
        public IReadOnlyList<string> Warnings { get { lock (_warnings) return _warnings.ToList(); } }
        public ILogger CreateLogger(string categoryName) => new Sink(_warnings);
        public void Dispose() { }

        private sealed class Sink(List<string> warnings) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel)) return;
                lock (warnings) warnings.Add(formatter(state, exception));
            }
        }
    }

    /// <summary>
    /// Refuses every treasury-ledger write and nothing else — the deterministic stand-in for a state
    /// database that has stopped accepting the ledger's rows while the rest of the server carries on.
    /// Every other save delegates to the no-durability default, so these tests isolate the swallow.
    /// </summary>
    private sealed class RefusingLedgerPersistence : IGameStatePersistence
    {
        private readonly NullGameStatePersistence _inner = new();
        public Task LoadIntoAsync(GameStore store, CancellationToken ct = default) => _inner.LoadIntoAsync(store, ct);
        public Task SaveItemPurchaseAsync(ItemPurchase purchase, CancellationToken ct = default) => _inner.SaveItemPurchaseAsync(purchase, ct);
        public Task SaveTournamentAsync(TournamentSession session, CancellationToken ct = default) => _inner.SaveTournamentAsync(session, ct);
        public Task SavePlayerAsync(Player player, CancellationToken ct = default) => _inner.SavePlayerAsync(player, ct);
        public Task SaveFancyFindAsync(FancyFind find, CancellationToken ct = default) => _inner.SaveFancyFindAsync(find, ct);
        public Task SaveHeroAsync(ArkadeHeroes.Core.Heroes.Hero hero, CancellationToken ct = default) => _inner.SaveHeroAsync(hero, ct);
        public Task SaveHeroProgressionAsync(ArkadeHeroes.Core.Heroes.Hero hero, CancellationToken ct = default) => _inner.SaveHeroProgressionAsync(hero, ct);
        public Task DeleteHeroAsync(string heroId, CancellationToken ct = default) => _inner.DeleteHeroAsync(heroId, ct);
        public Task SaveOfferAsync(OfferListing offer, CancellationToken ct = default) => _inner.SaveOfferAsync(offer, ct);
        public Task SaveStudProposalAsync(StudProposal proposal, CancellationToken ct = default) => _inner.SaveStudProposalAsync(proposal, ct);
        public Task SaveRenameAsync(RenameSession session, CancellationToken ct = default) => _inner.SaveRenameAsync(session, ct);
        public Task DeleteRenameAsync(string heroId, CancellationToken ct = default) => _inner.DeleteRenameAsync(heroId, ct);
        public Task SaveHeroSaleAsync(HeroSale sale, CancellationToken ct = default) => _inner.SaveHeroSaleAsync(sale, ct);
        public Task SaveHeroTombstoneAsync(HeroTombstone stone, CancellationToken ct = default) => _inner.SaveHeroTombstoneAsync(stone, ct);
        public Task SaveHeroBidAsync(HeroBid bid, CancellationToken ct = default) => _inner.SaveHeroBidAsync(bid, ct);
        public Task SaveTreasuryFlowAsync(string id, string direction, string tag, long sats, CancellationToken ct = default)
            => throw new InvalidOperationException("the ledger database is refusing writes");
    }
}
