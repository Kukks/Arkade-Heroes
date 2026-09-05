using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Server;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ArkadeHeroes.Tests;

/// <summary>
/// Bids over the real HTTP surface: buying a hero that is NOT for sale, which the marketplace could not do
/// (it only ever ran owner → market). The subject of nearly every test here is the CONSENT GATE — the
/// owner's acceptance — because that gate is the only thing standing between a bid and taking a stranger's
/// hero. Each is written so that removing the gate makes it fail: they assert not just the refusal but that
/// the refusal MOVED NO HERO and MOVED NO SATS.
///
/// The second subject is the money, in both directions: the owner is paid exactly once for a hero they
/// actually delivered, and the bidder gets every sat back for one they never received.
/// </summary>
public class HeroBidTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public HeroBidTests(WebApplicationFactory<Program> factory) => _factory = factory;

    /// <summary>A host whose bid reclaim window is already open, so the refund path is reachable in a test
    /// without waiting a day. The window is what protects a funded bidder from an owner who never delivers;
    /// its DURATION is an operator knob, and nothing here depends on the default value.</summary>
    private static WebApplicationFactory<Program> OpenWindow(WebApplicationFactory<Program> f) =>
        f.WithWebHostBuilder(b =>
        {
            b.UseSetting("Game:WagerEscrowRefundAfter", "00:00:00");
            // Exact sat arithmetic in the refund tests, so the starter claim must not move balances first
            // (WithFreeStarters' lever, restated here because WithWebHostBuilder does not compose with it).
            b.UseSetting("Game:BreedingFeeSats", "0");
        });

    /// <summary>The owner's half of a delivery: send the hero asset to the bidder's registered address from
    /// the owner's own wallet. Non-custodial — the server only ever VERIFIES this happened.</summary>
    private static async Task DeliverAsync(ArkadeHeroesClient owner, string heroId, string toPlayerId) =>
        await owner.TransferAssetAsync((await owner.Heroes.GetAsync(heroId)).AssetId!, toPlayerId);

    // ── (a) No consent → no hero, no sats ──────────────────────────────────

    [Fact]
    public async Task WithoutConsent_TheHeroCannotBeTaken_AndNothingIsBilled()
    {
        var (alice, alicePlayer) = await _factory.RegisterAsync("Bid-NoConsent-A");
        var (bob, bobPlayer) = await _factory.RegisterAsync("Bid-NoConsent-B");
        await alice.ClaimStartersAsync();
        var theirs = (await bob.ClaimStartersAsync())[0].Id;

        var bid = await alice.Bids.PlaceAsync(new PlaceBidRequest(theirs, 9_000));
        Assert.Equal("proposed", bid.Status);

        // Bob has not accepted. Alice cannot even find out what she'd owe…
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => alice.Bids.InvoiceAsync(bid.BidId));
        // …and the settle is refused outright.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => alice.Bids.SettleAsync(bid.BidId));

        // THE assertion the gate exists for: the hero did not move. A refusal that still transferred
        // would be no refusal.
        Assert.Equal(bobPlayer.PlayerId, (await alice.Heroes.GetAsync(theirs)).OwnerId);
        Assert.DoesNotContain(await alice.Heroes.MineAsync(), h => h.Id == theirs);
        Assert.NotEqual(alicePlayer.PlayerId, (await alice.Heroes.GetAsync(theirs)).OwnerId);
    }

    [Fact]
    public async Task ConsentFlagIsTheGate_SettleRefusedWithoutIt_EvenFullyPaidAndDelivered()
    {
        // Normally consent and billing arrive together — accepting is what creates the invoice — so a
        // refusal could be the missing money or the missing hero rather than the missing agreement. This
        // separates all three: pay everything, deliver the hero, then take the acceptance away on the row
        // and watch the sale stop dead. It is the one test that pins the CONSENT FLAG itself as the gate,
        // and it fails the moment that check is removed. Reaching into the store is deliberate for exactly
        // that reason.
        using var factory = _factory.WithFreeStarters();
        var (alice, alicePlayer) = await factory.RegisterAsync("Bid-Flag-A");
        var (bob, bobPlayer) = await factory.RegisterAsync("Bid-Flag-B");
        await alice.ClaimStartersAsync();
        var theirs = (await bob.ClaimStartersAsync())[0].Id;
        const long bidSats = 12_000;
        var bobStart = (await bob.Players.MeAsync()).BalanceSats;
        var store = factory.Services.GetRequiredService<GameStore>();

        var bid = await alice.Bids.PlaceAsync(new PlaceBidRequest(theirs, bidSats));
        var accepted = await bob.Bids.AcceptAsync(bid.BidId);
        await alice.PayInvoiceAsync(accepted.Invoice.InvoiceId);
        await DeliverAsync(bob, theirs, alicePlayer.PlayerId);

        store.HeroBids[bid.BidId].Accepted = false;   // consent withdrawn; money and hero untouched
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => alice.Bids.SettleAsync(bid.BidId));
        Assert.Equal(bobPlayer.PlayerId, (await alice.Heroes.GetAsync(theirs)).OwnerId);   // no transfer
        Assert.Equal(bobStart, (await bob.Players.MeAsync()).BalanceSats);                 // no payout

        // Put the consent back and the same call closes — so the refusal above was the flag, nothing else.
        store.HeroBids[bid.BidId].Accepted = true;
        var hero = await alice.Bids.SettleAsync(bid.BidId);
        Assert.Equal(alicePlayer.PlayerId, hero.OwnerId);
        Assert.Equal(bobStart + bidSats - accepted.Bid.FeeSats, (await bob.Players.MeAsync()).BalanceSats);
    }

    [Fact]
    public async Task OnlyTheHerosOwnerCanConsent()
    {
        var (alice, _) = await _factory.RegisterAsync("Bid-Consent-A");
        var (bob, bobPlayer) = await _factory.RegisterAsync("Bid-Consent-B");
        var (mallory, _) = await _factory.RegisterAsync("Bid-Consent-M");
        await alice.ClaimStartersAsync();
        var theirs = (await bob.ClaimStartersAsync())[0].Id;
        await mallory.ClaimStartersAsync();

        var bid = await alice.Bids.PlaceAsync(new PlaceBidRequest(theirs, 7_000));

        // The bidder cannot consent on the owner's behalf — the whole gate would be decorative.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => alice.Bids.AcceptAsync(bid.BidId));
        // Nor can an unrelated third party.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => mallory.Bids.AcceptAsync(bid.BidId));

        // Both refusals left the bid un-accepted, so the settle is still shut and the hero never moved.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => alice.Bids.SettleAsync(bid.BidId));
        Assert.Equal(bobPlayer.PlayerId, (await alice.Heroes.GetAsync(theirs)).OwnerId);
    }

    [Fact]
    public async Task DeliveringWithoutConsent_StillCannotTakeTheHero()
    {
        // The nastiest shape of the attack: the bidder somehow gets the ASSET (here, the owner sends it by
        // mistake) on an un-accepted bid. The chain now shows them holding it — so a settle gated only on
        // "does the chain show the bidder holding this" would hand over the game-side hero for free. The
        // consent flag has to be checked BEFORE the chain, and this fails if it isn't.
        using var factory = _factory.WithFreeStarters();
        var (alice, alicePlayer) = await factory.RegisterAsync("Bid-Deliver-A");
        var (bob, bobPlayer) = await factory.RegisterAsync("Bid-Deliver-B");
        await alice.ClaimStartersAsync();
        var theirs = (await bob.ClaimStartersAsync())[0].Id;
        var bobStart = (await bob.Players.MeAsync()).BalanceSats;

        var bid = await alice.Bids.PlaceAsync(new PlaceBidRequest(theirs, 5_500));
        await DeliverAsync(bob, theirs, alicePlayer.PlayerId);   // asset moved; NOTHING was agreed

        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => alice.Bids.SettleAsync(bid.BidId));
        Assert.Equal(bobPlayer.PlayerId, (await alice.Heroes.GetAsync(theirs)).OwnerId);
        Assert.Equal(bobStart, (await bob.Players.MeAsync()).BalanceSats);
    }

    [Fact]
    public async Task DeclinedBid_NeverSettles_AndBilledNothing()
    {
        using var factory = _factory.WithFreeStarters();
        var (alice, _) = await factory.RegisterAsync("Bid-Decline-A");
        var (bob, bobPlayer) = await factory.RegisterAsync("Bid-Decline-B");
        await alice.ClaimStartersAsync();
        var theirs = (await bob.ClaimStartersAsync())[0].Id;
        var start = ArkadeHeroes.Chain.InMemoryChainService.FaucetSats;

        var bid = await alice.Bids.PlaceAsync(new PlaceBidRequest(theirs, 4_000));
        var declined = await bob.Bids.DeclineAsync(bid.BidId);
        Assert.Equal("declined", declined.Status);

        // A refusal is final: it can't be walked back into an acceptance, and it never settles.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => bob.Bids.AcceptAsync(bid.BidId));
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => alice.Bids.SettleAsync(bid.BidId));
        Assert.Equal(bobPlayer.PlayerId, (await alice.Heroes.GetAsync(theirs)).OwnerId);

        // THE point of billing at consent rather than at the bid: a declined offer costs the bidder
        // nothing, to the sat. There was never an invoice to pay.
        Assert.Equal(start, (await alice.Players.MeAsync()).BalanceSats);
    }

    // ── (b) With consent → it proceeds, and the money lands ────────────────

    [Fact]
    public async Task WithConsent_TheHeroMoves_AndTheOwnerIsPaidTheBidLessTheFee()
    {
        // Exact sat arithmetic below, so the starter claim must not move the balances first.
        using var factory = _factory.WithFreeStarters();
        var (alice, alicePlayer) = await factory.RegisterAsync("Bid-Go-A");
        var (bob, bobPlayer) = await factory.RegisterAsync("Bid-Go-B");
        await alice.ClaimStartersAsync();
        var theirs = (await bob.ClaimStartersAsync())[0].Id;
        const long bidSats = 20_000;
        var start = ArkadeHeroes.Chain.InMemoryChainService.FaucetSats;

        var bid = await alice.Bids.PlaceAsync(new PlaceBidRequest(theirs, bidSats));

        // Bidding alone moves nothing: an offer is not a payment.
        Assert.Equal(start, (await alice.Players.MeAsync()).BalanceSats);
        Assert.Equal(start, (await bob.Players.MeAsync()).BalanceSats);

        // Nor does consenting — the owner is never asked to fund anything.
        var accepted = await bob.Bids.AcceptAsync(bid.BidId);
        Assert.Equal(bidSats, accepted.Invoice.AmountSats);
        Assert.Equal(bidSats - accepted.Bid.FeeSats, accepted.SellerNetSats);
        Assert.Equal(start, (await bob.Players.MeAsync()).BalanceSats);

        // Paying moves the sats to the TREASURY; the owner is still unpaid, because they haven't delivered.
        await alice.PayInvoiceAsync(accepted.Invoice.InvoiceId);
        Assert.Equal(start, (await bob.Players.MeAsync()).BalanceSats);
        Assert.True((await alice.Bids.InvoiceAsync(bid.BidId)).Funded);
        // …and it cannot be closed on the money alone, because the hero hasn't moved.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => alice.Bids.SettleAsync(bid.BidId));
        Assert.Equal(bobPlayer.PlayerId, (await alice.Heroes.GetAsync(theirs)).OwnerId);

        await DeliverAsync(bob, theirs, alicePlayer.PlayerId);
        var hero = await alice.Bids.SettleAsync(bid.BidId);

        // The hero is the BIDDER's, game-side and on-chain.
        Assert.Equal(alicePlayer.PlayerId, hero.OwnerId);
        Assert.Contains(await alice.Heroes.MineAsync(), h => h.Id == theirs);
        Assert.DoesNotContain(await bob.Heroes.MineAsync(), h => h.Id == theirs);

        // And the sats landed, to the sat, in both directions.
        Assert.Equal(start + bidSats - accepted.Bid.FeeSats, (await bob.Players.MeAsync()).BalanceSats);
        Assert.Equal(start - bidSats, (await alice.Players.MeAsync()).BalanceSats);
        Assert.Equal("settled", (await alice.Bids.ListAsync()).Single(b => b.BidId == bid.BidId).Status);
    }

    [Fact]
    public async Task EitherPartyCanSettle_SoNeitherCanHoldTheOtherHostage()
    {
        // A one-sided trigger would let whoever holds it sit on the other's money or hero indefinitely.
        using var factory = _factory.WithFreeStarters();
        var (alice, alicePlayer) = await factory.RegisterAsync("Bid-Either-A");
        var (bob, _) = await factory.RegisterAsync("Bid-Either-B");
        var (mallory, _) = await factory.RegisterAsync("Bid-Either-M");
        await alice.ClaimStartersAsync();
        var theirs = (await bob.ClaimStartersAsync())[0].Id;
        await mallory.ClaimStartersAsync();

        var bid = await alice.Bids.PlaceAsync(new PlaceBidRequest(theirs, 8_800));
        var accepted = await bob.Bids.AcceptAsync(bid.BidId);
        await alice.PayInvoiceAsync(accepted.Invoice.InvoiceId);
        await DeliverAsync(bob, theirs, alicePlayer.PlayerId);

        // A stranger cannot close someone else's trade…
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => mallory.Bids.SettleAsync(bid.BidId));
        // …but the OWNER can, even though it is the bidder who receives the hero.
        var hero = await bob.Bids.SettleAsync(bid.BidId);
        Assert.Equal(alicePlayer.PlayerId, hero.OwnerId);
    }

    // ── (c) One acceptance cannot be replayed to pay twice ─────────────────

    [Fact]
    public async Task OneAcceptancePaysTheOwnerExactlyOnce()
    {
        using var factory = _factory.WithFreeStarters();
        var (alice, alicePlayer) = await factory.RegisterAsync("Bid-Replay-A");
        var (bob, _) = await factory.RegisterAsync("Bid-Replay-B");
        await alice.ClaimStartersAsync();
        var theirs = (await bob.ClaimStartersAsync())[0].Id;
        const long bidSats = 15_000;
        var start = ArkadeHeroes.Chain.InMemoryChainService.FaucetSats;

        var bid = await alice.Bids.PlaceAsync(new PlaceBidRequest(theirs, bidSats));
        var accepted = await bob.Bids.AcceptAsync(bid.BidId);
        await alice.PayInvoiceAsync(accepted.Invoice.InvoiceId);
        await DeliverAsync(bob, theirs, alicePlayer.PlayerId);
        await alice.Bids.SettleAsync(bid.BidId);
        var paidOnce = (await bob.Players.MeAsync()).BalanceSats;
        Assert.Equal(start + bidSats - accepted.Bid.FeeSats, paidOnce);

        // Replaying the SAME consent is refused — from EITHER side, since either may settle.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => alice.Bids.SettleAsync(bid.BidId));
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => bob.Bids.SettleAsync(bid.BidId));
        // Re-accepting to re-arm the gate is refused too.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => bob.Bids.AcceptAsync(bid.BidId));

        // Exactly ONE payout — a replayed one would drain a treasury that cannot print.
        Assert.Equal(paidOnce, (await bob.Players.MeAsync()).BalanceSats);
    }

    [Fact]
    public async Task ConcurrentSettlesOfOneBid_PayTheOwnerOnlyOnce()
    {
        using var factory = _factory.WithFreeStarters();
        var (alice, alicePlayer) = await factory.RegisterAsync("Bid-Race-A");
        var (bob, _) = await factory.RegisterAsync("Bid-Race-B");
        await alice.ClaimStartersAsync();
        var theirs = (await bob.ClaimStartersAsync())[0].Id;
        const long bidSats = 11_000;
        var start = ArkadeHeroes.Chain.InMemoryChainService.FaucetSats;

        var bid = await alice.Bids.PlaceAsync(new PlaceBidRequest(theirs, bidSats));
        var accepted = await bob.Bids.AcceptAsync(bid.BidId);
        await alice.PayInvoiceAsync(accepted.Invoice.InvoiceId);
        await DeliverAsync(bob, theirs, alicePlayer.PlayerId);

        // Four settles in flight at once — both parties, each double-clicking.
        var clients = new[] { alice, bob, alice, bob };
        var results = await Task.WhenAll(clients.Select(async c =>
        {
            try { return (await c.Bids.SettleAsync(bid.BidId)).Id; }
            catch (ArkadeHeroesApiException) { return null; }
        }));

        Assert.Single(results, id => id is not null);
        Assert.Equal(start + bidSats - accepted.Bid.FeeSats, (await bob.Players.MeAsync()).BalanceSats);
    }

    [Fact]
    public async Task SettleIsRefusedUntilTheBidIsPaid()
    {
        using var factory = _factory.WithFreeStarters();
        var (alice, alicePlayer) = await factory.RegisterAsync("Bid-Unpaid-A");
        var (bob, bobPlayer) = await factory.RegisterAsync("Bid-Unpaid-B");
        await alice.ClaimStartersAsync();
        var theirs = (await bob.ClaimStartersAsync())[0].Id;
        var bobStart = (await bob.Players.MeAsync()).BalanceSats;

        var bid = await alice.Bids.PlaceAsync(new PlaceBidRequest(theirs, 6_500));
        var accepted = await bob.Bids.AcceptAsync(bid.BidId);
        Assert.False((await bob.Bids.InvoiceAsync(bid.BidId)).Funded);

        // The hero is delivered but the bid is unpaid — the owner acted on trust and must not lose it.
        await DeliverAsync(bob, theirs, alicePlayer.PlayerId);
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => alice.Bids.SettleAsync(bid.BidId));
        Assert.Equal(bobPlayer.PlayerId, (await alice.Heroes.GetAsync(theirs)).OwnerId);
        Assert.Equal(bobStart, (await bob.Players.MeAsync()).BalanceSats);

        // Paid → it closes.
        await alice.PayInvoiceAsync(accepted.Invoice.InvoiceId);
        var hero = await alice.Bids.SettleAsync(bid.BidId);
        Assert.Equal(alicePlayer.PlayerId, hero.OwnerId);
    }

    [Fact]
    public async Task AnUnpaidBidCannotSettleOutOfOtherPeoplesMoney()
    {
        // The payment gate, isolated. The sibling test above proves an unpaid settle is REFUSED — but on an
        // empty treasury the refusal can come from the payout simply having nothing to send, which is a
        // balance accident rather than a rule. Load the treasury with unrelated income first and that
        // accident disappears: without the paid check, an unpaid bid would transfer the hero and pay its
        // owner out of OTHER players' fees, and the treasury would be short with nothing to show for it.
        using var factory = _factory.WithFreeStarters();
        var (alice, alicePlayer) = await factory.RegisterAsync("Bid-OPM-A");
        var (bob, bobPlayer) = await factory.RegisterAsync("Bid-OPM-B");
        var (funder, _) = await factory.RegisterAsync("Bid-OPM-Funder");
        await alice.ClaimStartersAsync();
        var theirs = (await bob.ClaimStartersAsync())[0].Id;
        await funder.ClaimStartersAsync();
        const long bidSats = 6_500;

        var bid = await alice.Bids.PlaceAsync(new PlaceBidRequest(theirs, bidSats));
        var accepted = await bob.Bids.AcceptAsync(bid.BidId);

        // Unrelated income, from a player with nothing to do with this trade.
        var shop = await funder.Items.ShopAsync();
        var payout = bidSats - accepted.Bid.FeeSats;
        while ((await funder.Economy.HealthAsync()).TreasuryBalanceSats < payout * 2)
            await funder.BuyItemAsync(shop.OrderByDescending(i => i.PriceSats).First().Id);
        var treasuryBefore = (await funder.Economy.HealthAsync()).TreasuryBalanceSats;
        Assert.True(treasuryBefore >= payout, "the treasury must be able to cover the payout, or the gate is untested");

        // The hero is delivered and the owner consented — ONLY the missing payment stands in the way.
        await DeliverAsync(bob, theirs, alicePlayer.PlayerId);
        var bobStart = (await bob.Players.MeAsync()).BalanceSats;
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => alice.Bids.SettleAsync(bid.BidId));

        // Nothing moved: not the hero, not the owner's balance, and not a sat of anyone else's money.
        Assert.Equal(bobPlayer.PlayerId, (await alice.Heroes.GetAsync(theirs)).OwnerId);
        Assert.Equal(bobStart, (await bob.Players.MeAsync()).BalanceSats);
        Assert.Equal(treasuryBefore, (await funder.Economy.HealthAsync()).TreasuryBalanceSats);
    }

    // ── (d) The bidder's money always comes home ───────────────────────────

    [Fact]
    public async Task DeclinedBid_ReturnsEverySat_BecauseNothingWasEverTaken()
    {
        // The strongest form of "a declined bid costs nothing": there is no refund to run, because there
        // was no charge. This asserts the balance is UNMOVED end to end, not merely restored.
        using var factory = _factory.WithFreeStarters();
        var (alice, _) = await factory.RegisterAsync("Bid-Refund-Declined-A");
        var (bob, _) = await factory.RegisterAsync("Bid-Refund-Declined-B");
        await alice.ClaimStartersAsync();
        var theirs = (await bob.ClaimStartersAsync())[0].Id;
        var start = ArkadeHeroes.Chain.InMemoryChainService.FaucetSats;

        var bid = await alice.Bids.PlaceAsync(new PlaceBidRequest(theirs, 30_000));
        Assert.Equal(start, (await alice.Players.MeAsync()).BalanceSats);
        await bob.Bids.DeclineAsync(bid.BidId);
        Assert.Equal(start, (await alice.Players.MeAsync()).BalanceSats);

        // …and there is nothing to unwind, because nothing was ever billed.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => alice.Bids.RefundAsync(bid.BidId));
        Assert.Equal(start, (await alice.Players.MeAsync()).BalanceSats);
    }

    [Fact]
    public async Task AcceptedButNeverDelivered_TheBidderGetsEverySatBack()
    {
        // THE test the reclaim window exists for. The owner says yes, takes the money's word for it, and
        // never sends the hero. Past the window the bidder unwinds and is made whole — otherwise "accept
        // and go quiet" would be a way to take a stranger's sats and keep the hero.
        using var factory = OpenWindow(_factory);
        var (alice, _) = await factory.RegisterAsync("Bid-Strand-A");
        var (bob, bobPlayer) = await factory.RegisterAsync("Bid-Strand-B");
        await alice.ClaimStartersAsync();
        var theirs = (await bob.ClaimStartersAsync())[0].Id;
        const long bidSats = 25_000;
        var start = ArkadeHeroes.Chain.InMemoryChainService.FaucetSats;
        var bobStart = (await bob.Players.MeAsync()).BalanceSats;

        var bid = await alice.Bids.PlaceAsync(new PlaceBidRequest(theirs, bidSats));
        var accepted = await bob.Bids.AcceptAsync(bid.BidId);
        await alice.PayInvoiceAsync(accepted.Invoice.InvoiceId);
        Assert.Equal(start - bidSats, (await alice.Players.MeAsync()).BalanceSats);   // sats are in escrow

        var refund = await alice.Bids.RefundAsync(bid.BidId);
        Assert.Equal(bidSats, refund.RefundedSats);
        Assert.Equal("refunded", refund.Bid.Status);

        // WHOLE — every sat, including the marketplace fee, because no sale happened to take a cut of.
        Assert.Equal(start, (await alice.Players.MeAsync()).BalanceSats);
        // The owner was paid nothing, and still has the hero they never sent.
        Assert.Equal(bobStart, (await bob.Players.MeAsync()).BalanceSats);
        Assert.Equal(bobPlayer.PlayerId, (await alice.Heroes.GetAsync(theirs)).OwnerId);
        // …and the unwound bid is terminal: it can never later settle.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => alice.Bids.SettleAsync(bid.BidId));
    }

    [Fact]
    public async Task OneUnwindRefundsExactlyOnce()
    {
        // The mirror of the settle's replay guard, on the money that flows the other way.
        using var factory = OpenWindow(_factory);
        var (alice, _) = await factory.RegisterAsync("Bid-Refund-Once-A");
        var (bob, _) = await factory.RegisterAsync("Bid-Refund-Once-B");
        await alice.ClaimStartersAsync();
        var theirs = (await bob.ClaimStartersAsync())[0].Id;
        const long bidSats = 18_000;
        var start = ArkadeHeroes.Chain.InMemoryChainService.FaucetSats;

        var bid = await alice.Bids.PlaceAsync(new PlaceBidRequest(theirs, bidSats));
        var accepted = await bob.Bids.AcceptAsync(bid.BidId);
        await alice.PayInvoiceAsync(accepted.Invoice.InvoiceId);

        // Four unwinds in flight at once — both parties may unwind, and both may retry.
        var clients = new[] { alice, bob, alice, bob };
        var results = await Task.WhenAll(clients.Select(async c =>
        {
            try { return (long?)(await c.Bids.RefundAsync(bid.BidId)).RefundedSats; }
            catch (ArkadeHeroesApiException) { return null; }
        }));
        Assert.Single(results, r => r is not null);

        // Exactly ONE refund, however many callers asked for one.
        Assert.Equal(start, (await alice.Players.MeAsync()).BalanceSats);
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => alice.Bids.RefundAsync(bid.BidId));
        Assert.Equal(start, (await alice.Players.MeAsync()).BalanceSats);
    }

    [Fact]
    public async Task ASettledBidCannotThenBeRefunded_SoNobodyIsPaidTwice()
    {
        // The direction that cannot be survived: refunding a sale whose seller has ALREADY been paid would
        // send the same sats out of the treasury twice, with the hero delivered. The seller-paid latch is
        // what shuts that door, and this fails without it.
        using var factory = OpenWindow(_factory);
        var (alice, alicePlayer) = await factory.RegisterAsync("Bid-NoDouble-A");
        var (bob, _) = await factory.RegisterAsync("Bid-NoDouble-B");
        await alice.ClaimStartersAsync();
        var theirs = (await bob.ClaimStartersAsync())[0].Id;
        const long bidSats = 22_000;
        var start = ArkadeHeroes.Chain.InMemoryChainService.FaucetSats;

        var bid = await alice.Bids.PlaceAsync(new PlaceBidRequest(theirs, bidSats));
        var accepted = await bob.Bids.AcceptAsync(bid.BidId);
        await alice.PayInvoiceAsync(accepted.Invoice.InvoiceId);
        await DeliverAsync(bob, theirs, alicePlayer.PlayerId);
        await alice.Bids.SettleAsync(bid.BidId);
        var afterSale = (await alice.Players.MeAsync()).BalanceSats;

        // The window is wide open (zeroed above), so ONLY the settled/seller-paid guard can refuse this.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => alice.Bids.RefundAsync(bid.BidId));
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => bob.Bids.RefundAsync(bid.BidId));

        // The bidder paid once and keeps the hero; nothing came back.
        Assert.Equal(afterSale, (await alice.Players.MeAsync()).BalanceSats);
        Assert.Equal(start - bidSats, afterSale);
        Assert.Equal(alicePlayer.PlayerId, (await alice.Heroes.GetAsync(theirs)).OwnerId);
    }

    [Fact]
    public async Task APaidOutButUnlatchedSettle_StillCannotBeRefunded()
    {
        // The crash window, and the ONE state in which the seller-paid latch is the only thing standing
        // between the treasury and paying for one hero twice.
        //
        // SettleBidAsync pays the owner, then transfers the hero record, then latches Settled. A fault in
        // between leaves SellerPaid=true with Settled=false — and from there the bidder (who now HOLDS the
        // hero asset) can move it on to an alt, which makes the "already delivered" check stop matching.
        // With the seller already paid and the chain no longer showing the bidder holding it, every OTHER
        // guard in RefundBidAsync passes, and a refund would send the same sats out a second time.
        //
        // The flag is set directly for the reason the stud suite reaches into its store: it is the only way
        // to stand in the crash window, and standing in it is the whole point.
        using var factory = OpenWindow(_factory);
        var (alice, alicePlayer) = await factory.RegisterAsync("Bid-CrashWindow-A");
        var (bob, _) = await factory.RegisterAsync("Bid-CrashWindow-B");
        var (carol, carolPlayer) = await factory.RegisterAsync("Bid-CrashWindow-C");
        await alice.ClaimStartersAsync();
        var theirs = (await bob.ClaimStartersAsync())[0].Id;
        await carol.ClaimStartersAsync();
        const long bidSats = 19_000;
        var start = ArkadeHeroes.Chain.InMemoryChainService.FaucetSats;
        var store = factory.Services.GetRequiredService<GameStore>();

        var bid = await alice.Bids.PlaceAsync(new PlaceBidRequest(theirs, bidSats));
        var accepted = await bob.Bids.AcceptAsync(bid.BidId);
        await alice.PayInvoiceAsync(accepted.Invoice.InvoiceId);
        var heroAsset = (await bob.Heroes.GetAsync(theirs)).AssetId!;
        await bob.TransferAssetAsync(heroAsset, alicePlayer.PlayerId);

        // The settle paid the owner and then died before latching. Both facts, exactly as they'd be left.
        store.HeroBids[bid.BidId].SellerPaid = true;
        Assert.False(store.HeroBids[bid.BidId].Settled);
        var afterPayout = (await alice.Players.MeAsync()).BalanceSats;

        // The bidder forwards the hero on, so the "already delivered" guard no longer matches them.
        await alice.TransferAssetAsync(heroAsset, carolPlayer.PlayerId);

        // Neither party can unwind it — the sale is committed, and only a settle may finish it.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => alice.Bids.RefundAsync(bid.BidId));
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => bob.Bids.RefundAsync(bid.BidId));

        // Not one sat came back: the bidder is still down the bid they were charged.
        Assert.Equal(afterPayout, (await alice.Players.MeAsync()).BalanceSats);
        Assert.Equal(start - bidSats, (await alice.Players.MeAsync()).BalanceSats);
    }

    [Fact]
    public async Task DeliveredButUnsettled_CannotBeUnwoundOutFromUnderTheOwner()
    {
        // The owner's side of the same coin: they delivered, and nobody has run the settle yet. Unwinding
        // here would take a delivered hero AND return the bidder's sats — the owner robbed of both. The
        // window is wide open, so only the delivered check can refuse it.
        using var factory = OpenWindow(_factory);
        var (alice, alicePlayer) = await factory.RegisterAsync("Bid-Delivered-A");
        var (bob, _) = await factory.RegisterAsync("Bid-Delivered-B");
        await alice.ClaimStartersAsync();
        var theirs = (await bob.ClaimStartersAsync())[0].Id;
        const long bidSats = 13_500;

        var bid = await alice.Bids.PlaceAsync(new PlaceBidRequest(theirs, bidSats));
        var accepted = await bob.Bids.AcceptAsync(bid.BidId);
        await alice.PayInvoiceAsync(accepted.Invoice.InvoiceId);
        await DeliverAsync(bob, theirs, alicePlayer.PlayerId);

        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => alice.Bids.RefundAsync(bid.BidId));

        // …and the sale can still be completed, which is the point: this was a settle, not a strand.
        var hero = await bob.Bids.SettleAsync(bid.BidId);
        Assert.Equal(alicePlayer.PlayerId, hero.OwnerId);
    }

    [Fact]
    public async Task WithinTheWindow_ABidCannotBeUnwound()
    {
        // The window is the OWNER's protection: without it a bidder could fund a bid, watch the hero leave
        // the owner's wallet, and yank the money back before anyone settled.
        using var factory = _factory.WithWebHostBuilder(b =>
            b.UseSetting("Game:WagerEscrowRefundAfter", "24:00:00"));
        var (alice, _) = await factory.RegisterAsync("Bid-Window-A");
        var (bob, _) = await factory.RegisterAsync("Bid-Window-B");
        await alice.ClaimStartersAsync();
        var theirs = (await bob.ClaimStartersAsync())[0].Id;

        var bid = await alice.Bids.PlaceAsync(new PlaceBidRequest(theirs, 9_900));
        var accepted = await bob.Bids.AcceptAsync(bid.BidId);
        await alice.PayInvoiceAsync(accepted.Invoice.InvoiceId);
        Assert.True(accepted.Bid.ReclaimAfterUnixSeconds > DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        var ex = await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => alice.Bids.RefundAsync(bid.BidId));
        Assert.Contains("window", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AStrandedBid_IsSurfacedOnTheReclaimPage_ToBothParties()
    {
        // Discovery: a bidder whose sats are tied up must be able to FIND them without knowing the bid id,
        // and the owner must be able to find a hero a quiet bidder is blocking.
        using var factory = OpenWindow(_factory);
        var (alice, _) = await factory.RegisterAsync("Bid-Reclaimable-A");
        var (bob, _) = await factory.RegisterAsync("Bid-Reclaimable-B");
        await alice.ClaimStartersAsync();
        var theirs = (await bob.ClaimStartersAsync())[0].Id;

        var bid = await alice.Bids.PlaceAsync(new PlaceBidRequest(theirs, 14_000));
        // An UN-accepted bid holds nothing, so it must not appear — a row offering to recover nothing is noise.
        Assert.DoesNotContain(await alice.Players.ReclaimableAsync(), r => r.Id == bid.BidId);

        var accepted = await bob.Bids.AcceptAsync(bid.BidId);
        await alice.PayInvoiceAsync(accepted.Invoice.InvoiceId);

        Assert.Contains(await alice.Players.ReclaimableAsync(), r => r.Kind == "bid" && r.Id == bid.BidId);
        Assert.Contains(await bob.Players.ReclaimableAsync(), r => r.Kind == "bid" && r.Id == bid.BidId);

        // Once unwound it stops being offered — a reclaim that can only fail is worse than none.
        await alice.Bids.RefundAsync(bid.BidId);
        Assert.DoesNotContain(await alice.Players.ReclaimableAsync(), r => r.Id == bid.BidId);
    }

    // ── (e) Bid-time and accept-time rules ─────────────────────────────────

    [Fact]
    public async Task CannotBidOnYourOwnHero_NorForNothing()
    {
        var (alice, _) = await _factory.RegisterAsync("Bid-Rules-A");
        var (bob, _) = await _factory.RegisterAsync("Bid-Rules-B");
        var mine = (await alice.ClaimStartersAsync())[0].Id;
        var theirs = (await bob.ClaimStartersAsync())[0].Id;

        // Bidding on your own hero would route a payment to yourself through a treasury that takes a cut.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => alice.Bids.PlaceAsync(new PlaceBidRequest(mine, 5_000)));
        // A non-positive bid, and one at or under the marketplace fee, both leave the owner netting
        // nothing or less — no honest sale looks like that.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => alice.Bids.PlaceAsync(new PlaceBidRequest(theirs, 0)));
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => alice.Bids.PlaceAsync(new PlaceBidRequest(theirs, -5_000)));
    }

    [Fact]
    public async Task OnAFeeFreeArena_ABidStillCannotBeZeroOrNegative()
    {
        // The sibling test above passes for the WRONG reason on a default arena: MarketplaceFeeFor refuses
        // anything at or below the 1,000-sat marketplace fee, so a 0-sat bid is caught by the fee boundary
        // rather than by a rule about bids. Turn the fee off — which an operator may legitimately do, and
        // which both listing paths still guard against — and that boundary disappears entirely:
        // MarketplaceFeeFor returns 0 without looking at the amount. A 0-sat bid would then be a real
        // offer, and settling it would hand over the hero while `proceeds > 0` skipped the payout: a hero
        // for nothing.
        using var factory = _factory.WithWebHostBuilder(b => b.UseSetting("Game:OfferListingFeeSats", "0"));
        var (alice, _) = await factory.RegisterAsync("Bid-FeeFree-A");
        var (bob, _) = await factory.RegisterAsync("Bid-FeeFree-B");
        await alice.ClaimStartersAsync();
        var theirs = (await bob.ClaimStartersAsync())[0].Id;

        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => alice.Bids.PlaceAsync(new PlaceBidRequest(theirs, 0)));
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => alice.Bids.PlaceAsync(new PlaceBidRequest(theirs, -10_000)));

        // …and a real bid still works with the fee off, so the guard is about the AMOUNT, not the fee.
        var ok = await alice.Bids.PlaceAsync(new PlaceBidRequest(theirs, 5_000));
        Assert.Equal(0, ok.FeeSats);
        Assert.Equal(5_000, ok.BidSats);
    }

    [Fact]
    public async Task ConcurrentAcceptsOfTwoBidsOnOneHero_AcceptOnlyOne()
    {
        // The one-accepted-bid-per-hero rule reads state owned by OTHER bid ids, so the per-bid lock does
        // not serialise it: two accepts on the same hero can both scan before either writes, and both
        // become accepted. That is two funded claims on a thing there is one of — the second bidder pays
        // for a hero that is already promised and has to wait out the window to get their sats back.
        //
        // SIX contenders rather than two, deliberately. With two the interleaving is easy to miss on a warm
        // host (measured: reliably caught alone, missed inside a full class run), and a race test that only
        // fires in isolation is a race test that rots. Six makes the overlap the common case.
        using var factory = _factory.WithFreeStarters();
        var (bob, _) = await factory.RegisterAsync("Bid-AcceptRace-Owner");
        var theirs = (await bob.ClaimStartersAsync())[0].Id;

        var bidIds = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            var (bidder, _) = await factory.RegisterAsync($"Bid-AcceptRace-{i}");
            await bidder.ClaimStartersAsync();
            bidIds.Add((await bidder.Bids.PlaceAsync(new PlaceBidRequest(theirs, 7_000 + i * 500))).BidId);
        }

        // All six accepts in flight at once — the owner hammering their own inbox.
        var results = await Task.WhenAll(bidIds.Select(async id =>
        {
            try { return (await bob.Bids.AcceptAsync(id)).Bid.BidId; }
            catch (ArkadeHeroesApiException) { return null; }
        }));

        Assert.Single(results, id => id is not null);
        var live = await bob.Bids.ListAsync();
        Assert.Single(live.Where(b => b.HeroId == theirs && b.Status == "accepted"));
    }

    [Fact]
    public async Task AcceptSerialisesPerHero_NotMerelyPerBid()
    {
        // The deterministic companion to the race above. A racing test proves the gate is real but only
        // when the interleaving actually happens — measured, the six-way race fires every time in isolation
        // and not once inside a warm class run, which makes it a poor guard against the gate rotting out.
        //
        // This pins the same fact without depending on the scheduler: hold the per-HERO key from outside,
        // and an accept must not be able to finish. Without that lock the accept only ever takes
        // bid:{bidId}, which nothing here holds, so it sails straight through and this fails immediately.
        using var factory = _factory.WithFreeStarters();
        var (alice, _) = await factory.RegisterAsync("Bid-Serialise-A");
        var (bob, _) = await factory.RegisterAsync("Bid-Serialise-B");
        await alice.ClaimStartersAsync();
        var theirs = (await bob.ClaimStartersAsync())[0].Id;
        var store = factory.Services.GetRequiredService<GameStore>();

        var bid = await alice.Bids.PlaceAsync(new PlaceBidRequest(theirs, 8_400));

        var gate = await store.LockAsync($"hero-bid:{theirs}");
        var accept = Task.Run(() => bob.Bids.AcceptAsync(bid.BidId));
        // Long enough that a request which ISN'T waiting on this key would have finished several times
        // over (the same accept completes in single-digit milliseconds unblocked).
        var finishedEarly = await Task.WhenAny(accept, Task.Delay(1_500)) == accept;
        Assert.False(finishedEarly, "accept must serialise on the HERO, not only on the bid");

        // …and it completes the moment the key is free, so the wait above was the lock and not a hang.
        gate.Dispose();
        var accepted = await accept.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal("accepted", accepted.Bid.Status);
    }

    [Fact]
    public async Task ABidderCannotStackDuplicateBidsOnOneHero()
    {
        var (alice, _) = await _factory.RegisterAsync("Bid-Dup-A");
        var (bob, _) = await _factory.RegisterAsync("Bid-Dup-B");
        await alice.ClaimStartersAsync();
        var theirs = (await bob.ClaimStartersAsync())[0].Id;

        var first = await alice.Bids.PlaceAsync(new PlaceBidRequest(theirs, 5_000));
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => alice.Bids.PlaceAsync(new PlaceBidRequest(theirs, 6_000)));

        // Withdrawing frees the slot — a bidder can always re-price by retracting first.
        await alice.Bids.WithdrawAsync(first.BidId);
        var second = await alice.Bids.PlaceAsync(new PlaceBidRequest(theirs, 6_000));
        Assert.Equal(6_000, second.BidSats);
    }

    [Fact]
    public async Task OnlyOneBidOnAHeroCanBeAcceptedAtATime()
    {
        // Two live acceptances on one hero would be two funded claims on a thing there is one of. The
        // second bidder would pay for a hero that is already promised.
        var (alice, _) = await _factory.RegisterAsync("Bid-OneAccept-A");
        var (carol, _) = await _factory.RegisterAsync("Bid-OneAccept-C");
        var (bob, _) = await _factory.RegisterAsync("Bid-OneAccept-B");
        await alice.ClaimStartersAsync();
        await carol.ClaimStartersAsync();
        var theirs = (await bob.ClaimStartersAsync())[0].Id;

        var first = await alice.Bids.PlaceAsync(new PlaceBidRequest(theirs, 7_000));
        var second = await carol.Bids.PlaceAsync(new PlaceBidRequest(theirs, 9_000));
        await bob.Bids.AcceptAsync(first.BidId);
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => bob.Bids.AcceptAsync(second.BidId));

        // The un-accepted one is untouched and still refusable — it was never billed.
        Assert.Equal("proposed", (await carol.Bids.ListAsync()).Single(b => b.BidId == second.BidId).Status);
    }

    [Fact]
    public async Task AHeroSoldBeforeConsent_ItsOldOwnerCanNoLongerAccept()
    {
        // The bid PINS who it was offered to. A hero sold on since is one whose new owner was never asked,
        // and whose old owner has nothing left to sell.
        using var factory = _factory.WithFreeStarters();
        var (alice, _) = await factory.RegisterAsync("Bid-Resold-A");
        var (bob, _) = await factory.RegisterAsync("Bid-Resold-B");
        var (carol, carolPlayer) = await factory.RegisterAsync("Bid-Resold-C");
        await alice.ClaimStartersAsync();
        var theirs = (await bob.ClaimStartersAsync())[0].Id;
        await carol.ClaimStartersAsync();

        var bid = await alice.Bids.PlaceAsync(new PlaceBidRequest(theirs, 10_000));
        await bob.TransferAssetAsync((await bob.Heroes.GetAsync(theirs)).AssetId!, carolPlayer.PlayerId);
        await bob.Heroes.TransferAsync(theirs, new TransferRequest(carolPlayer.PlayerId));

        // Carol owns it now — and is refused, because she is not who was asked.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => carol.Bids.AcceptAsync(bid.BidId));
        // Bob was who was asked — and is refused too, because it is no longer his to sell.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => bob.Bids.AcceptAsync(bid.BidId));
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => alice.Bids.SettleAsync(bid.BidId));
    }

    [Fact]
    public async Task AHeroRestingInAListingCannotAlsoBeSoldToABidder()
    {
        // Its asset is already escrowed in an offer covenant. Accepting would promise one hero to two buyers.
        using var factory = _factory.WithFreeStarters();
        var (alice, _) = await factory.RegisterAsync("Bid-Listed-A");
        var (bob, _) = await factory.RegisterAsync("Bid-Listed-B");
        await alice.ClaimStartersAsync();
        var theirs = (await bob.ClaimStartersAsync())[0].Id;

        var bid = await alice.Bids.PlaceAsync(new PlaceBidRequest(theirs, 12_000));
        await bob.Offers.CreateHeroAsync(new CreateHeroOfferRequest(theirs, 20_000));
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => bob.Bids.AcceptAsync(bid.BidId));
    }

    [Fact]
    public async Task AnAcceptedBid_CannotBeDeclinedOrWithdrawnBehindTheOthersBack()
    {
        // After consent, the only exits are settle and the reclaim window. A unilateral cancel would let
        // the owner pull out after the bidder had paid, or the bidder pull out after the hero was sent.
        var (alice, _) = await _factory.RegisterAsync("Bid-NoCancel-A");
        var (bob, _) = await _factory.RegisterAsync("Bid-NoCancel-B");
        await alice.ClaimStartersAsync();
        var theirs = (await bob.ClaimStartersAsync())[0].Id;

        var bid = await alice.Bids.PlaceAsync(new PlaceBidRequest(theirs, 8_000));
        await bob.Bids.AcceptAsync(bid.BidId);

        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => bob.Bids.DeclineAsync(bid.BidId));
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => alice.Bids.WithdrawAsync(bid.BidId));
        Assert.Equal("accepted", (await alice.Bids.ListAsync()).Single(b => b.BidId == bid.BidId).Status);
    }

    [Fact]
    public async Task TheInvoiceIsVisibleOnlyToTheBidsParties()
    {
        var (alice, _) = await _factory.RegisterAsync("Bid-Priv-A");
        var (bob, _) = await _factory.RegisterAsync("Bid-Priv-B");
        var (mallory, _) = await _factory.RegisterAsync("Bid-Priv-M");
        await alice.ClaimStartersAsync();
        var theirs = (await bob.ClaimStartersAsync())[0].Id;
        await mallory.ClaimStartersAsync();

        var bid = await alice.Bids.PlaceAsync(new PlaceBidRequest(theirs, 3_000));
        await bob.Bids.AcceptAsync(bid.BidId);

        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => mallory.Bids.InvoiceAsync(bid.BidId));
        Assert.NotNull(await alice.Bids.InvoiceAsync(bid.BidId));   // the payer
        Assert.NotNull(await bob.Bids.InvoiceAsync(bid.BidId));     // the payee
    }

    [Fact]
    public async Task ASettledBid_RecordsThePriceOnTheHerosTimeline()
    {
        // A bid is a SALE, so it belongs in the same history a listing's sale does — otherwise a hero
        // bought this way would read as having changed hands for nothing.
        using var factory = _factory.WithFreeStarters();
        var (alice, alicePlayer) = await factory.RegisterAsync("Bid-Timeline-A");
        var (bob, _) = await factory.RegisterAsync("Bid-Timeline-B");
        await alice.ClaimStartersAsync();
        var theirs = (await bob.ClaimStartersAsync())[0].Id;
        const long bidSats = 17_000;

        var bid = await alice.Bids.PlaceAsync(new PlaceBidRequest(theirs, bidSats));
        var accepted = await bob.Bids.AcceptAsync(bid.BidId);
        await alice.PayInvoiceAsync(accepted.Invoice.InvoiceId);
        await DeliverAsync(bob, theirs, alicePlayer.PlayerId);
        await alice.Bids.SettleAsync(bid.BidId);

        var timeline = await alice.Heroes.TimelineAsync(theirs);
        var sale = Assert.Single(timeline.Events, e => e.Kind == "sold");
        Assert.Equal(bidSats, sale.Sats);
    }
}

/// <summary>
/// Durability of a bid — the second flow (after the stud proposal) where sats owed to ANOTHER PLAYER rest
/// in the treasury between two calls. A restart that forgot an accepted bid would leave the bidder's paid
/// sats with nothing able to name who they belong to; a restart that REVIVED a settled one would hand a
/// stale client a second payout. Drives a real restart: a second host, a fresh GameStore, the same database
/// file (mirrors StudServiceDurabilityTests).
/// </summary>
public class HeroBidDurabilityTests
{
    private static WebApplicationFactory<Program> HostOn(string dbPath) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Game:StateDbPath", dbPath);
            b.UseSetting("Game:BreedingFeeSats", "0");   // free starters: the subject here is the row, not the fees
        });

    [Fact]
    public async Task AnAcceptedBid_SurvivesARestart_WithItsConsentInvoiceAndWindowIntact()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"arkade-bid-{Guid.NewGuid():N}.db");
        try
        {
            string bidId, invoiceId;
            long reclaimAfter;
            using (var first = HostOn(dbPath))
            {
                var (alice, _) = await first.RegisterAsync("Bid-Durable-A");
                var (bob, _) = await first.RegisterAsync("Bid-Durable-B");
                await alice.ClaimStartersAsync();
                var theirs = (await bob.ClaimStartersAsync())[0].Id;

                var bid = await alice.Bids.PlaceAsync(new PlaceBidRequest(theirs, 16_000));
                var accepted = await bob.Bids.AcceptAsync(bid.BidId);
                await alice.PayInvoiceAsync(accepted.Invoice.InvoiceId);
                bidId = bid.BidId;
                invoiceId = accepted.Invoice.InvoiceId;
                reclaimAfter = accepted.Bid.ReclaimAfterUnixSeconds;
            }

            // ── restart: a brand-new host and GameStore over the same database ──
            using var restarted = HostOn(dbPath);
            _ = restarted.CreateClient();   // force the host to start so the boot-time rehydrate runs
            var store = restarted.Services.GetRequiredService<GameStore>();

            Assert.True(store.HeroBids.ContainsKey(bidId),
                "an accepted bid may be holding the bidder's paid sats — losing it strands them");
            var recovered = store.HeroBids[bidId];
            Assert.True(recovered.Accepted, "the consent itself must survive — it is what authorises the sale");
            Assert.False(recovered.Settled);
            Assert.False(recovered.SellerPaid);   // the once-only payout latch, still unspent
            Assert.False(recovered.RefundPaid);   // …and its mirror
            Assert.Equal(16_000, recovered.BidSats);
            Assert.Equal(invoiceId, recovered.BidInvoiceId);
            Assert.Equal(reclaimAfter, recovered.ReclaimAfterUnixSeconds);
        }
        finally
        {
            SqliteTestDb.ReleasePool(dbPath);
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task ASettledBid_IsNotRevived_SoARestartCannotPayTheOwnerASecondTime()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"arkade-bid-{Guid.NewGuid():N}.db");
        try
        {
            string bidId, heroId, buyerId;
            long ownerBalanceAfterSale;
            using (var first = HostOn(dbPath))
            {
                var (alice, alicePlayer) = await first.RegisterAsync("Bid-Terminal-A");
                var (bob, _) = await first.RegisterAsync("Bid-Terminal-B");
                await alice.ClaimStartersAsync();
                heroId = (await bob.ClaimStartersAsync())[0].Id;

                var bid = await alice.Bids.PlaceAsync(new PlaceBidRequest(heroId, 21_000));
                var accepted = await bob.Bids.AcceptAsync(bid.BidId);
                await alice.PayInvoiceAsync(accepted.Invoice.InvoiceId);
                await bob.TransferAssetAsync((await bob.Heroes.GetAsync(heroId)).AssetId!, alicePlayer.PlayerId);
                await alice.Bids.SettleAsync(bid.BidId);
                bidId = bid.BidId;
                buyerId = alicePlayer.PlayerId;
                ownerBalanceAfterSale = (await bob.Players.MeAsync()).BalanceSats;
            }

            using var restarted = HostOn(dbPath);
            _ = restarted.CreateClient();
            var store = restarted.Services.GetRequiredService<GameStore>();

            // The hero's new ownership is durable. The spent CONSENT deliberately is not revived: a settled
            // bid is terminal, and rehydrating one would give a stale settle a second chance to pay out.
            Assert.Equal(buyerId, store.Heroes[heroId].OwnerId);
            Assert.False(store.HeroBids.ContainsKey(bidId),
                "a settled bid is terminal — reviving it would let one acceptance pay twice across a restart");
            Assert.True(ownerBalanceAfterSale > 0);
        }
        finally
        {
            SqliteTestDb.ReleasePool(dbPath);
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch (IOException) { }
        }
    }
}
