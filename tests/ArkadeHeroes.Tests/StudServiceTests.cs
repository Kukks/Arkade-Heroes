using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Server;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ArkadeHeroes.Tests;

/// <summary>
/// The stud service over the real HTTP surface: breeding with ANOTHER player's hero, which ordinary
/// breeding cannot do (it demands the caller own both parents). The subject of nearly every test here is
/// the CONSENT GATE — the stud owner's acceptance — because that gate is the only thing standing between a
/// stud service and using a stranger's hero for free. Each test is written so that removing the gate makes
/// it fail: they assert not just the refusal but that the refusal MINTED NOTHING and MOVED NO SATS.
/// </summary>
public class StudServiceTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public StudServiceTests(WebApplicationFactory<Program> factory) => _factory = factory;

    /// <summary>Pays every invoice an accepted proposal bills (from the PROPOSER's wallet) and reveals.</summary>
    private static async Task<StudRevealResponse> PayAndRevealAsync(
        ArkadeHeroesClient proposer, string proposalId, string nonce)
    {
        var bill = await proposer.Stud.InvoicesAsync(proposalId);
        await proposer.PayInvoiceAsync(bill.BreedFeeInvoice.InvoiceId);
        if (bill.StudFeeInvoice is { } studFee) await proposer.PayInvoiceAsync(studFee.InvoiceId);
        return await proposer.Stud.RevealAsync(proposalId, new StudRevealRequest(nonce));
    }

    // ── (a) No consent → refused, and NOTHING minted ───────────────────────

    [Fact]
    public async Task WithoutConsent_RevealIsRefused_AndNothingMints()
    {
        var (alice, _) = await _factory.RegisterAsync("Stud-NoConsent-A");
        var (bob, _) = await _factory.RegisterAsync("Stud-NoConsent-B");
        var mine = (await alice.ClaimStartersAsync())[0].Id;
        var theirs = (await bob.ClaimStartersAsync())[0].Id;
        var rosterBefore = (await alice.Heroes.MineAsync()).Count;

        var proposal = await alice.Stud.ProposeAsync(new StudProposeRequest(mine, theirs, 3_000));

        // Bob has not accepted. Alice cannot even find out what she'd owe, let alone pay it…
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => alice.Stud.InvoicesAsync(proposal.ProposalId));
        // …and the reveal is refused outright.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => alice.Stud.RevealAsync(proposal.ProposalId, new StudRevealRequest("no-consent")));

        // THE assertion the gate exists for: no child. A refusal that still minted would be no refusal.
        Assert.Equal(rosterBefore, (await alice.Heroes.MineAsync()).Count);
        Assert.Equal("proposed", (await alice.Stud.ListAsync()).Single(p => p.ProposalId == proposal.ProposalId).Status);
    }

    [Fact]
    public async Task ConsentFlagIsTheGate_RevealRefusedWithoutIt_EvenWithEveryFeePaid()
    {
        // Normally consent and billing arrive together — accepting is what creates the invoices — so a
        // refusal could be the missing money rather than the missing agreement. This separates them: pay
        // everything, then take the acceptance away on the session row and watch the breed stop dead. It is
        // the one test that pins the CONSENT FLAG itself as the gate, and it fails the moment that check is
        // removed. Reaching into the store is deliberate for exactly that reason.
        using var factory = _factory.WithFreeStarters();
        var (alice, _) = await factory.RegisterAsync("Stud-Flag-A");
        var (bob, _) = await factory.RegisterAsync("Stud-Flag-B");
        var mine = (await alice.ClaimStartersAsync())[0].Id;
        var theirs = (await bob.ClaimStartersAsync())[0].Id;
        const long studFee = 4_400;
        var bobStart = (await bob.Players.MeAsync()).BalanceSats;
        var rosterBefore = (await alice.Heroes.MineAsync()).Count;
        var store = factory.Services.GetRequiredService<GameStore>();

        var proposal = await alice.Stud.ProposeAsync(new StudProposeRequest(mine, theirs, studFee));
        var accept = await bob.Stud.AcceptAsync(proposal.ProposalId);
        await alice.PayInvoiceAsync(accept.BreedFeeInvoice.InvoiceId);
        await alice.PayInvoiceAsync(accept.StudFeeInvoice!.InvoiceId);

        store.StudProposals[proposal.ProposalId].Accepted = false;   // consent withdrawn; money untouched
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => alice.Stud.RevealAsync(proposal.ProposalId, new StudRevealRequest("ungated")));
        Assert.Equal(rosterBefore, (await alice.Heroes.MineAsync()).Count);         // no child
        Assert.Equal(bobStart, (await bob.Players.MeAsync()).BalanceSats);          // no stud fee

        // Put the consent back and the same call breeds — so the refusal above was the flag, nothing else.
        store.StudProposals[proposal.ProposalId].Accepted = true;
        var reveal = await alice.Stud.RevealAsync(proposal.ProposalId, new StudRevealRequest("gated"));
        Assert.Contains(await alice.Heroes.MineAsync(), h => h.Id == reveal.Hero.Id);
        Assert.Equal(bobStart + studFee, (await bob.Players.MeAsync()).BalanceSats);
    }

    [Fact]
    public async Task OrdinaryBreedingStillCannotReachAnotherPlayersHero()
    {
        // The pre-existing gate this feature must not have opened a side door around: the two-parent breed
        // still demands you own both. The stud flow is the ONLY way to a cross-owner child.
        var (alice, _) = await _factory.RegisterAsync("Stud-Direct-A");
        var (bob, _) = await _factory.RegisterAsync("Stud-Direct-B");
        var mine = (await alice.ClaimStartersAsync())[0].Id;
        var theirs = (await bob.ClaimStartersAsync())[0].Id;

        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => alice.Breeding.CommitAsync(new BreedCommitRequest(mine, theirs)));
    }

    [Fact]
    public async Task OnlyTheStudsOwnerCanConsent()
    {
        var (alice, _) = await _factory.RegisterAsync("Stud-Consent-A");
        var (bob, _) = await _factory.RegisterAsync("Stud-Consent-B");
        var (mallory, _) = await _factory.RegisterAsync("Stud-Consent-M");
        var mine = (await alice.ClaimStartersAsync())[0].Id;
        var theirs = (await bob.ClaimStartersAsync())[0].Id;
        await mallory.ClaimStartersAsync();
        var rosterBefore = (await alice.Heroes.MineAsync()).Count;

        var proposal = await alice.Stud.ProposeAsync(new StudProposeRequest(mine, theirs, 1_000));

        // The proposer cannot consent on the stud owner's behalf — the whole gate would be decorative.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => alice.Stud.AcceptAsync(proposal.ProposalId));
        // Nor can an unrelated third party.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => mallory.Stud.AcceptAsync(proposal.ProposalId));

        // Both refusals left the proposal un-accepted, so the reveal is still shut and nothing minted.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => alice.Stud.RevealAsync(proposal.ProposalId, new StudRevealRequest("self-consent")));
        Assert.Equal(rosterBefore, (await alice.Heroes.MineAsync()).Count);
    }

    [Fact]
    public async Task StudSoldBeforeConsent_NeitherTheOldNorTheNewOwnerCanAccept()
    {
        // The proposal PINS who the stud fee was offered to. Sell the stud before consenting and the
        // proposal is stale in both directions: its new owner was never offered anything (and accepting
        // would route her hero's service fee to the seller), while the seller no longer has a hero to
        // offer. The only honest answer is a fresh proposal — this is the case the pinned-owner check
        // exists for, and the one place ownership-of-the-hero alone would wave through.
        using var factory = _factory.WithFreeStarters();
        var (alice, _) = await factory.RegisterAsync("Stud-Resold-A");
        var (bob, _) = await factory.RegisterAsync("Stud-Resold-B");
        var (carol, carolPlayer) = await factory.RegisterAsync("Stud-Resold-C");
        var mine = (await alice.ClaimStartersAsync())[0].Id;
        var theirs = (await bob.ClaimStartersAsync())[0].Id;
        await carol.ClaimStartersAsync();
        var rosterBefore = (await alice.Heroes.MineAsync()).Count;

        var proposal = await alice.Stud.ProposeAsync(new StudProposeRequest(mine, theirs, 2_200));

        // Bob sells the stud to Carol before answering (wallet move, then the server-verified confirm).
        await bob.TransferAssetAsync((await bob.Heroes.GetAsync(theirs)).AssetId!, carolPlayer.PlayerId);
        await bob.Heroes.TransferAsync(theirs, new TransferRequest(carolPlayer.PlayerId));

        // Carol owns the hero now — and is still refused, because she is not who was asked.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => carol.Stud.AcceptAsync(proposal.ProposalId));
        // Bob was who was asked — and is refused too, because the hero is no longer his to offer.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => bob.Stud.AcceptAsync(proposal.ProposalId));

        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => alice.Stud.RevealAsync(proposal.ProposalId, new StudRevealRequest("resold")));
        Assert.Equal(rosterBefore, (await alice.Heroes.MineAsync()).Count);
    }

    [Fact]
    public async Task DeclinedProposal_NeverBreeds()
    {
        var (alice, _) = await _factory.RegisterAsync("Stud-Decline-A");
        var (bob, _) = await _factory.RegisterAsync("Stud-Decline-B");
        var mine = (await alice.ClaimStartersAsync())[0].Id;
        var theirs = (await bob.ClaimStartersAsync())[0].Id;
        var rosterBefore = (await alice.Heroes.MineAsync()).Count;

        var proposal = await alice.Stud.ProposeAsync(new StudProposeRequest(mine, theirs, 2_000));
        var declined = await bob.Stud.DeclineAsync(proposal.ProposalId);
        Assert.Equal("declined", declined.Status);

        // A refusal is final: it can't be walked back into an acceptance, and it never mints.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => bob.Stud.AcceptAsync(proposal.ProposalId));
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => alice.Stud.RevealAsync(proposal.ProposalId, new StudRevealRequest("declined")));
        Assert.Equal(rosterBefore, (await alice.Heroes.MineAsync()).Count);
    }

    // ── (b) With consent → it proceeds ─────────────────────────────────────

    [Fact]
    public async Task WithConsent_ChildMintsToTheProposer_AndVerifies()
    {
        var (alice, _) = await _factory.RegisterAsync("Stud-Go-A");
        var (bob, _) = await _factory.RegisterAsync("Stud-Go-B");
        var mine = (await alice.ClaimStartersAsync())[0].Id;
        var theirs = (await bob.ClaimStartersAsync())[0].Id;
        var parentA = await alice.Heroes.GetAsync(mine);
        var parentB = await bob.Heroes.GetAsync(theirs);

        var proposal = await alice.Stud.ProposeAsync(new StudProposeRequest(mine, theirs, 4_000));
        Assert.Equal(theirs, proposal.StudHeroId);

        await bob.Stud.AcceptAsync(proposal.ProposalId);
        var reveal = await PayAndRevealAsync(alice, proposal.ProposalId, "stud-nonce");

        // The foal belongs to the PROPOSER — the stud's owner was paid in sats, not offspring.
        Assert.Contains(await alice.Heroes.MineAsync(), h => h.Id == reveal.Hero.Id);
        Assert.DoesNotContain(await bob.Heroes.MineAsync(), h => h.Id == reveal.Hero.Id);
        Assert.Equal(mine, reveal.Hero.ParentAId);
        Assert.Equal(theirs, reveal.Hero.ParentBId);

        // Client-verifiable exactly like any other breed — the server can't have picked the genome.
        var (ok, detail) = FairnessAudit.VerifyBreeding(parentA, parentB, "stud-nonce", proposal.CommitmentHex,
            new BreedRevealResponse(reveal.Hero, reveal.ServerSeedHex, reveal.EntropyHex, "stud", reveal.Receipt));
        Assert.True(ok, detail);

        // Typed as a breeding, so quests/season pass/badges count it like one, and the receipt verifies.
        Assert.Equal("breeding", reveal.Receipt!.Type);
        Assert.True(ReceiptVerifier.Verify(reveal.Receipt!).Ok);

        // BOTH parents paid the cost of the breed — the stud's cooldown is what the fee compensates.
        Assert.Equal(1, (await alice.Heroes.GetAsync(mine)).BreedCount);
        Assert.Equal(1, (await bob.Heroes.GetAsync(theirs)).BreedCount);
    }

    // ── (c) The stud fee actually reaches the counterparty ─────────────────

    [Fact]
    public async Task StudFeeReachesTheStudsOwner_AndOnlyOnceTheBreedHappens()
    {
        // Exact sat arithmetic below, so the starter claim must not move the balances first.
        using var factory = _factory.WithFreeStarters();
        var (alice, _) = await factory.RegisterAsync("Stud-Fee-A");
        var (bob, _) = await factory.RegisterAsync("Stud-Fee-B");
        var mine = (await alice.ClaimStartersAsync())[0].Id;
        var theirs = (await bob.ClaimStartersAsync())[0].Id;
        const long studFee = 7_500;
        var start = ArkadeHeroes.Chain.InMemoryChainService.FaucetSats;

        var proposal = await alice.Stud.ProposeAsync(new StudProposeRequest(mine, theirs, studFee));

        // Proposing alone moves nothing: an offer is not a payment.
        Assert.Equal(start, (await alice.Players.MeAsync()).BalanceSats);
        Assert.Equal(start, (await bob.Players.MeAsync()).BalanceSats);

        // Nor does consenting — the stud's owner is never asked to fund anything.
        var accept = await bob.Stud.AcceptAsync(proposal.ProposalId);
        Assert.Equal(studFee, accept.StudFeeInvoice!.AmountSats);
        Assert.Equal(start, (await bob.Players.MeAsync()).BalanceSats);

        // Paying the invoices moves the sats to the TREASURY; the stud's owner is still unpaid, because
        // the fee is owed for a breed that hasn't happened yet.
        var bill = await alice.Stud.InvoicesAsync(proposal.ProposalId);
        await alice.PayInvoiceAsync(bill.BreedFeeInvoice.InvoiceId);
        await alice.PayInvoiceAsync(bill.StudFeeInvoice!.InvoiceId);
        Assert.Equal(start, (await bob.Players.MeAsync()).BalanceSats);

        var reveal = await alice.Stud.RevealAsync(proposal.ProposalId, new StudRevealRequest("fee-nonce"));

        // The stud fee LANDED with the counterparty, to the sat.
        Assert.Equal(studFee, reveal.StudFeePaidSats);
        Assert.Equal(start + studFee, (await bob.Players.MeAsync()).BalanceSats);
        // And it came out of the proposer, alongside the breed fee (zeroed by WithFreeStarters).
        Assert.Equal(start - studFee - bill.BreedFeeInvoice.AmountSats, (await alice.Players.MeAsync()).BalanceSats);
    }

    [Fact]
    public async Task ZeroStudFee_BreedsWithNoInvoiceAndNoPayout()
    {
        using var factory = _factory.WithFreeStarters();
        var (alice, _) = await factory.RegisterAsync("Stud-Free-A");
        var (bob, _) = await factory.RegisterAsync("Stud-Free-B");
        var mine = (await alice.ClaimStartersAsync())[0].Id;
        var theirs = (await bob.ClaimStartersAsync())[0].Id;
        var start = ArkadeHeroes.Chain.InMemoryChainService.FaucetSats;

        var proposal = await alice.Stud.ProposeAsync(new StudProposeRequest(mine, theirs));
        var accept = await bob.Stud.AcceptAsync(proposal.ProposalId);
        Assert.Null(accept.StudFeeInvoice);   // a favour bills no stud invoice at all

        var reveal = await PayAndRevealAsync(alice, proposal.ProposalId, "free-nonce");
        Assert.Equal(0, reveal.StudFeePaidSats);
        Assert.Equal(start, (await bob.Players.MeAsync()).BalanceSats);
        Assert.Contains(await alice.Heroes.MineAsync(), h => h.Id == reveal.Hero.Id);
    }

    [Fact]
    public async Task RevealIsRefusedUntilBothInvoicesArePaid()
    {
        var (alice, _) = await _factory.RegisterAsync("Stud-Unpaid-A");
        var (bob, _) = await _factory.RegisterAsync("Stud-Unpaid-B");
        var mine = (await alice.ClaimStartersAsync())[0].Id;
        var theirs = (await bob.ClaimStartersAsync())[0].Id;
        var bobStart = (await bob.Players.MeAsync()).BalanceSats;

        var proposal = await alice.Stud.ProposeAsync(new StudProposeRequest(mine, theirs, 5_000));
        await bob.Stud.AcceptAsync(proposal.ProposalId);
        var bill = await alice.Stud.InvoicesAsync(proposal.ProposalId);

        // Neither paid → refused.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => alice.Stud.RevealAsync(proposal.ProposalId, new StudRevealRequest("n")));
        // Only the breed fee paid → still refused, and the stud's owner is still unpaid.
        await alice.PayInvoiceAsync(bill.BreedFeeInvoice.InvoiceId);
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => alice.Stud.RevealAsync(proposal.ProposalId, new StudRevealRequest("n")));
        Assert.Equal(bobStart, (await bob.Players.MeAsync()).BalanceSats);
        // Both paid → it breeds.
        await alice.PayInvoiceAsync(bill.StudFeeInvoice!.InvoiceId);
        var reveal = await alice.Stud.RevealAsync(proposal.ProposalId, new StudRevealRequest("n"));
        Assert.Equal(5_000, reveal.StudFeePaidSats);
    }

    // ── (d) One acceptance cannot be replayed ──────────────────────────────

    [Fact]
    public async Task OneAcceptanceBreedsExactlyOnce()
    {
        using var factory = _factory.WithFreeStarters();
        var (alice, _) = await factory.RegisterAsync("Stud-Replay-A");
        var (bob, _) = await factory.RegisterAsync("Stud-Replay-B");
        var mine = (await alice.ClaimStartersAsync())[0].Id;
        var theirs = (await bob.ClaimStartersAsync())[0].Id;
        const long studFee = 6_000;
        var start = ArkadeHeroes.Chain.InMemoryChainService.FaucetSats;

        var proposal = await alice.Stud.ProposeAsync(new StudProposeRequest(mine, theirs, studFee));
        await bob.Stud.AcceptAsync(proposal.ProposalId);
        var first = await PayAndRevealAsync(alice, proposal.ProposalId, "replay-1");
        var rosterAfterFirst = (await alice.Heroes.MineAsync()).Count;

        // Replaying the SAME consent — with a fresh nonce, which is the only lever a caller has — is
        // refused: one acceptance buys one child.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => alice.Stud.RevealAsync(proposal.ProposalId, new StudRevealRequest("replay-2")));
        // …and the same nonce fares no better.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => alice.Stud.RevealAsync(proposal.ProposalId, new StudRevealRequest("replay-1")));
        // Re-accepting to re-arm the gate is refused too.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => bob.Stud.AcceptAsync(proposal.ProposalId));

        // Exactly ONE child, and exactly ONE stud fee — a replayed payout would drain a treasury that
        // cannot print.
        Assert.Equal(rosterAfterFirst, (await alice.Heroes.MineAsync()).Count);
        Assert.Single(await alice.Heroes.MineAsync(), h => h.Id == first.Hero.Id);
        Assert.Equal(start + studFee, (await bob.Players.MeAsync()).BalanceSats);
    }

    [Fact]
    public async Task ReplayLatchStopsASecondBreed_EvenWithNoCooldownInTheWay()
    {
        // The breeding cooldown normally blocks a replayed reveal as a side effect, which would let the
        // once-only latch rot unnoticed behind it. Cooldowns are configurable and can be tuned to nothing,
        // so this run takes that backstop away: with no cooldown left, the ONLY thing standing between one
        // acceptance and a second free child is the completed latch, and this test fails without it.
        using var factory = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Game:BreedingFeeSats", "0");
            b.UseSetting("Game:BreedingCooldownBaseUnit", "00:00:00");
        });
        var (alice, _) = await factory.RegisterAsync("Stud-Latch-A");
        var (bob, _) = await factory.RegisterAsync("Stud-Latch-B");
        var mine = (await alice.ClaimStartersAsync())[0].Id;
        var theirs = (await bob.ClaimStartersAsync())[0].Id;
        const long studFee = 3_100;
        var bobStart = (await bob.Players.MeAsync()).BalanceSats;

        var proposal = await alice.Stud.ProposeAsync(new StudProposeRequest(mine, theirs, studFee));
        await bob.Stud.AcceptAsync(proposal.ProposalId);
        await PayAndRevealAsync(alice, proposal.ProposalId, "latch-1");
        var rosterAfterFirst = (await alice.Heroes.MineAsync()).Count;

        // Cooldown-free, fully paid, freshly nonced — and still refused.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => alice.Stud.RevealAsync(proposal.ProposalId, new StudRevealRequest("latch-2")));
        Assert.Equal(rosterAfterFirst, (await alice.Heroes.MineAsync()).Count);
        Assert.Equal(bobStart + studFee, (await bob.Players.MeAsync()).BalanceSats);   // one fee, not two

        // Proof the cooldown really was out of the way, so the refusal above cannot be a cooldown in
        // disguise: both parents came off cooldown the instant they went on it.
        Assert.True((await alice.Heroes.GetAsync(mine)).BreedCooldownUntil <= DateTimeOffset.UtcNow);
        Assert.True((await bob.Heroes.GetAsync(theirs)).BreedCooldownUntil <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task ConcurrentRevealsOfOneAcceptance_MintOnlyOneChild()
    {
        using var factory = _factory.WithFreeStarters();
        var (alice, _) = await factory.RegisterAsync("Stud-Race-A");
        var (bob, _) = await factory.RegisterAsync("Stud-Race-B");
        var mine = (await alice.ClaimStartersAsync())[0].Id;
        var theirs = (await bob.ClaimStartersAsync())[0].Id;
        const long studFee = 2_500;
        var start = ArkadeHeroes.Chain.InMemoryChainService.FaucetSats;
        var rosterBefore = (await alice.Heroes.MineAsync()).Count;

        var proposal = await alice.Stud.ProposeAsync(new StudProposeRequest(mine, theirs, studFee));
        await bob.Stud.AcceptAsync(proposal.ProposalId);
        var bill = await alice.Stud.InvoicesAsync(proposal.ProposalId);
        await alice.PayInvoiceAsync(bill.BreedFeeInvoice.InvoiceId);
        await alice.PayInvoiceAsync(bill.StudFeeInvoice!.InvoiceId);

        // Two reveals in flight at once — a retrying client, or a double-clicked button.
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(async i =>
        {
            try { return (await alice.Stud.RevealAsync(proposal.ProposalId, new StudRevealRequest($"race-{i}"))).Hero.Id; }
            catch (ArkadeHeroesApiException) { return null; }
        }));

        Assert.Single(results, id => id is not null);
        Assert.Equal(rosterBefore + 1, (await alice.Heroes.MineAsync()).Count);
        Assert.Equal(start + studFee, (await bob.Players.MeAsync()).BalanceSats);   // paid once, not twice
    }

    // ── Proposal-time rules ────────────────────────────────────────────────

    [Fact]
    public async Task CannotProposeWithAHeroYouDoNotOwn()
    {
        var (alice, _) = await _factory.RegisterAsync("Stud-Own-A");
        var (bob, _) = await _factory.RegisterAsync("Stud-Own-B");
        var a = await alice.ClaimStartersAsync();
        var b = await bob.ClaimStartersAsync();

        // Alice offering BOB's hero as her side of the deal.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => alice.Stud.ProposeAsync(new StudProposeRequest(b[0].Id, a[0].Id, 100)));
    }

    [Fact]
    public async Task CannotProposeAgainstYourOwnHero_OrOfferANegativeFee()
    {
        var (alice, _) = await _factory.RegisterAsync("Stud-Self-A");
        var (bob, _) = await _factory.RegisterAsync("Stud-Self-B");
        var a = await alice.ClaimStartersAsync();
        var theirs = (await bob.ClaimStartersAsync())[0].Id;

        // Both parents hers — that is ordinary breeding, and routing it through a stud proposal would let
        // her pay a "fee" to herself.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => alice.Stud.ProposeAsync(new StudProposeRequest(a[0].Id, a[1].Id, 100)));
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => alice.Stud.ProposeAsync(new StudProposeRequest(a[0].Id, a[0].Id, 100)));
        // A negative fee would pay the PROPOSER out of the stud owner's pocket.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => alice.Stud.ProposeAsync(new StudProposeRequest(a[0].Id, theirs, -1)));
    }

    [Fact]
    public async Task StudSoldAfterConsent_BreedIsRefused()
    {
        // Consent belongs to the owner who gave it. A stud sold on afterwards has a new owner who agreed to
        // nothing — and it is THEIR hero's cooldown the breed would spend.
        using var factory = _factory.WithFreeStarters();
        var (alice, _) = await factory.RegisterAsync("Stud-Sold-A");
        var (bob, _) = await factory.RegisterAsync("Stud-Sold-B");
        var (carol, carolPlayer) = await factory.RegisterAsync("Stud-Sold-C");
        var mine = (await alice.ClaimStartersAsync())[0].Id;
        var theirs = (await bob.ClaimStartersAsync())[0].Id;
        await carol.ClaimStartersAsync();
        var rosterBefore = (await alice.Heroes.MineAsync()).Count;

        var proposal = await alice.Stud.ProposeAsync(new StudProposeRequest(mine, theirs, 1_500));
        await bob.Stud.AcceptAsync(proposal.ProposalId);
        var bill = await alice.Stud.InvoicesAsync(proposal.ProposalId);
        await alice.PayInvoiceAsync(bill.BreedFeeInvoice.InvoiceId);
        await alice.PayInvoiceAsync(bill.StudFeeInvoice!.InvoiceId);

        // Bob hands the stud to Carol before Alice reveals (wallet move, then the server-verified confirm).
        var studAsset = (await bob.Heroes.GetAsync(theirs)).AssetId!;
        await bob.TransferAssetAsync(studAsset, carolPlayer.PlayerId);
        await bob.Heroes.TransferAsync(theirs, new TransferRequest(carolPlayer.PlayerId));

        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => alice.Stud.RevealAsync(proposal.ProposalId, new StudRevealRequest("sold")));
        Assert.Equal(rosterBefore, (await alice.Heroes.MineAsync()).Count);
    }

    [Fact]
    public async Task List_SurfacesTheProposalToBothPartiesUntilItIsBred()
    {
        using var factory = _factory.WithFreeStarters();
        var (alice, alicePlayer) = await factory.RegisterAsync("Stud-List-A");
        var (bob, bobPlayer) = await factory.RegisterAsync("Stud-List-B");
        var mine = (await alice.ClaimStartersAsync())[0].Id;
        var theirs = (await bob.ClaimStartersAsync())[0].Id;

        var proposal = await alice.Stud.ProposeAsync(new StudProposeRequest(mine, theirs, 900));

        // The list is the browser's discovery path — it is how the stud's owner ever learns they were asked.
        var listed = (await bob.Stud.ListAsync()).Single(p => p.ProposalId == proposal.ProposalId);
        Assert.Equal("proposed", listed.Status);
        Assert.Equal(alicePlayer.PlayerId, listed.ProposerPlayerId);
        Assert.Equal(bobPlayer.PlayerId, listed.StudOwnerPlayerId);
        Assert.Equal(900, listed.StudFeeSats);
        Assert.Null(listed.ChildHeroId);

        await bob.Stud.AcceptAsync(proposal.ProposalId);
        Assert.Equal("accepted", (await alice.Stud.ListAsync()).Single(p => p.ProposalId == proposal.ProposalId).Status);

        var reveal = await PayAndRevealAsync(alice, proposal.ProposalId, "list-nonce");
        var done = (await alice.Stud.ListAsync()).Single(p => p.ProposalId == proposal.ProposalId);
        Assert.Equal("completed", done.Status);
        Assert.Equal(reveal.Hero.Id, done.ChildHeroId);
    }

    [Fact]
    public async Task InvoicesAreVisibleOnlyToTheProposalsParties()
    {
        var (alice, _) = await _factory.RegisterAsync("Stud-Priv-A");
        var (bob, _) = await _factory.RegisterAsync("Stud-Priv-B");
        var (mallory, _) = await _factory.RegisterAsync("Stud-Priv-M");
        var mine = (await alice.ClaimStartersAsync())[0].Id;
        var theirs = (await bob.ClaimStartersAsync())[0].Id;
        await mallory.ClaimStartersAsync();

        var proposal = await alice.Stud.ProposeAsync(new StudProposeRequest(mine, theirs, 500));
        await bob.Stud.AcceptAsync(proposal.ProposalId);

        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => mallory.Stud.InvoicesAsync(proposal.ProposalId));
        Assert.NotNull(await alice.Stud.InvoicesAsync(proposal.ProposalId));   // the payer
        Assert.NotNull(await bob.Stud.InvoicesAsync(proposal.ProposalId));     // the payee
    }

    [Fact]
    public async Task AcceptedProposal_IsCountedAsAnOpenFlowForTheOperator()
    {
        // A proposal holding a consent (and possibly paid fees) is unfinished business the console must
        // show — an accepted-but-unrevealed stud is exactly the state a stranded fee hides in.
        using var factory = _factory.WithFreeStarters();
        var (alice, _) = await factory.RegisterAsync("Stud-Admin-A");
        var (bob, _) = await factory.RegisterAsync("Stud-Admin-B");
        var mine = (await alice.ClaimStartersAsync())[0].Id;
        var theirs = (await bob.ClaimStartersAsync())[0].Id;
        var store = factory.Services.GetRequiredService<GameStore>();

        var proposal = await alice.Stud.ProposeAsync(new StudProposeRequest(mine, theirs, 250));
        await bob.Stud.AcceptAsync(proposal.ProposalId);
        Assert.Equal(1, store.StudProposals.Values.Count(s => !s.Completed && !s.Declined));

        await PayAndRevealAsync(alice, proposal.ProposalId, "admin-nonce");
        Assert.Equal(0, store.StudProposals.Values.Count(s => !s.Completed && !s.Declined));
    }
}

/// <summary>
/// Durability of the stud proposal — the one breed session that gets a database row, because it is the one
/// where sats are owed to ANOTHER PLAYER. A restart that forgot an accepted proposal would leave the
/// proposer's paid fees in the treasury with nothing left able to name who they were owed to; a restart that
/// REVIVED a completed one would hand a stale client a second mint. Drives a real restart: a second host, a
/// fresh GameStore, the same database file (mirrors StateDurabilityTests).
/// </summary>
public class StudServiceDurabilityTests
{
    private static WebApplicationFactory<Program> HostOn(string dbPath) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Game:StateDbPath", dbPath);
            b.UseSetting("Game:BreedingFeeSats", "0");   // free starters: the subject here is the row, not the fees
        });

    [Fact]
    public async Task AcceptedProposal_SurvivesARestart_WithItsConsentAndInvoicesIntact()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"arkade-stud-{Guid.NewGuid():N}.db");
        try
        {
            string proposalId, breedInvoiceId, studInvoiceId;
            using (var first = HostOn(dbPath))
            {
                var (alice, _) = await first.RegisterAsync("Stud-Durable-A");
                var (bob, _) = await first.RegisterAsync("Stud-Durable-B");
                var mine = (await alice.ClaimStartersAsync())[0].Id;
                var theirs = (await bob.ClaimStartersAsync())[0].Id;

                var proposal = await alice.Stud.ProposeAsync(new StudProposeRequest(mine, theirs, 3_300));
                var accept = await bob.Stud.AcceptAsync(proposal.ProposalId);
                proposalId = proposal.ProposalId;
                breedInvoiceId = accept.BreedFeeInvoice.InvoiceId;
                studInvoiceId = accept.StudFeeInvoice!.InvoiceId;
            }

            // ── restart: a brand-new host and GameStore over the same database ──
            using var restarted = HostOn(dbPath);
            _ = restarted.CreateClient();   // force the host to start so the boot-time rehydrate runs
            var store = restarted.Services.GetRequiredService<GameStore>();

            Assert.True(store.StudProposals.ContainsKey(proposalId),
                "an accepted proposal may be holding the proposer's paid fees — losing it strands them");
            var recovered = store.StudProposals[proposalId];
            Assert.True(recovered.Accepted, "the consent itself must survive — it is what authorises the breed");
            Assert.False(recovered.Completed);
            Assert.False(recovered.StudFeePaid);          // the once-only payout latch, still unspent
            Assert.Equal(3_300, recovered.StudFeeSats);
            Assert.Equal(breedInvoiceId, recovered.BreedFeeInvoiceId);
            Assert.Equal(studInvoiceId, recovered.StudFeeInvoiceId);
        }
        finally
        {
            SqliteTestDb.ReleasePool(dbPath);
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task BredProposal_IsNotRevived_SoARestartCannotMintASecondChild()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"arkade-stud-{Guid.NewGuid():N}.db");
        try
        {
            string proposalId;
            string childId;
            using (var first = HostOn(dbPath))
            {
                var (alice, _) = await first.RegisterAsync("Stud-Terminal-A");
                var (bob, _) = await first.RegisterAsync("Stud-Terminal-B");
                var mine = (await alice.ClaimStartersAsync())[0].Id;
                var theirs = (await bob.ClaimStartersAsync())[0].Id;

                var proposal = await alice.Stud.ProposeAsync(new StudProposeRequest(mine, theirs, 1_200));
                var accept = await bob.Stud.AcceptAsync(proposal.ProposalId);
                await alice.PayInvoiceAsync(accept.BreedFeeInvoice.InvoiceId);
                await alice.PayInvoiceAsync(accept.StudFeeInvoice!.InvoiceId);
                var reveal = await alice.Stud.RevealAsync(proposal.ProposalId, new StudRevealRequest("terminal"));
                proposalId = proposal.ProposalId;
                childId = reveal.Hero.Id;
            }

            using var restarted = HostOn(dbPath);
            var client = restarted.CreateClient();
            var store = restarted.Services.GetRequiredService<GameStore>();

            // The child is durable (heroes are). The spent CONSENT is deliberately not revived: a completed
            // proposal is terminal, and rehydrating one would give a stale reveal a second chance to mint.
            Assert.True(store.Heroes.ContainsKey(childId));
            Assert.False(store.StudProposals.ContainsKey(proposalId),
                "a bred proposal is terminal — reviving it would let one acceptance mint twice across a restart");
            Assert.DoesNotContain(await new ArkadeHeroesClient(client).Stud.ListAsync(), p => p.ProposalId == proposalId);
        }
        finally
        {
            SqliteTestDb.ReleasePool(dbPath);
            try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch (IOException) { }
        }
    }
}
