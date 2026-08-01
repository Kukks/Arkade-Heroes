using ArkadeHeroes.Chain;
using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Server;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ArkadeHeroes.Tests;

/// <summary>
/// The way OUT of a bracket that will never fill.
///
/// <para>Exactly one entry per player is allowed (<c>JoinTournamentAsync</c>), which is deliberate — it is
/// what stops one wallet buying every seat and playing itself for the pot. The consequence is that a bracket
/// needs as many distinct PLAYERS as it has seats: two players cannot fill a size-4 one however much they
/// want to. A live walk left a real size-4 bracket sitting at 2/4 with 2,000 real sats escrowed.</para>
///
/// <para>The strand refund did not reach that state. Its gate asks whether a bracket is UNRESOLVABLE, and
/// for an OPEN bracket "unresolvable" meant an entrant hero had been burned away. Two live heroes in a
/// bracket nobody else will ever join reads as perfectly resolvable, so the refund refused it — for the
/// entrants, for a bystander, and for the operator console alike, since all three land on the same service
/// call. The sats had no exit at all.</para>
///
/// <para>The gap is not "the bracket is provably dead" — an open bracket can never be PROVED dead, because
/// a new player may sign up tomorrow. It is that an entrant had no way to take back their own stake. So the
/// new door is consent-shaped rather than proof-shaped: an ENTRANT may call off a bracket that is still
/// OPEN, and every cleared buy-in — theirs and everyone's — goes home. Nothing is transferred and nobody
/// profits, so this can only ever unwind a pot to the people who paid into it.</para>
/// </summary>
public class TournamentStuckBracketTests
{
    const long BuyIn = 1_000;

    /// <summary>Two players in a size-4 bracket: the exact live-walk state, both buy-ins cleared.
    /// <c>TreasuryBeforeBracket</c> is sampled AFTER the registrations and starter claims — those charge
    /// their own fees into the same treasury, so a balance taken any earlier is not this bracket's baseline.</summary>
    private static async Task<(string Tid, ArkadeHeroesClient Alice, ArkadeHeroesClient Bob, long TreasuryBeforeBracket)>
        StuckAtTwoOfFourAsync(WebApplicationFactory<Program> factory, InMemoryChainService chain, string tag)
    {
        var (alice, _) = await factory.RegisterAsync($"{tag}-Alice");
        var (bob, _) = await factory.RegisterAsync($"{tag}-Bob");
        var aliceHero = (await alice.ClaimStartersAsync())[0].Id;
        var bobHero = (await bob.ClaimStartersAsync())[0].Id;

        var treasuryBeforeBracket = await chain.TreasuryBalanceAsync();
        var open = await alice.Tournament.OpenAsync(new OpenTournamentRequest(aliceHero, BuyIn, 4));
        await alice.Dev.PayInvoiceAsync(new { open.BuyIn.InvoiceId });
        var join = await bob.Tournament.JoinAsync(open.Tournament.Id, new JoinTournamentRequest(bobHero));
        await bob.Dev.PayInvoiceAsync(new { join.BuyIn.InvoiceId });

        return (open.Tournament.Id, alice, bob, treasuryBeforeBracket);
    }

    /// <summary>
    /// The constraint itself, pinned so the fix below is understood as living WITH it rather than as an
    /// excuse to remove it. One wallet must not be able to fill a bracket by itself.
    /// </summary>
    [Fact]
    public async Task OneEntryPerPlayer_IsEnforced_SoASingleWalletCannotFillABracketAlone()
    {
        using var factory = new WebApplicationFactory<Program>();
        var (alice, _) = await factory.RegisterAsync("Solo-Alice");
        var heroes = await alice.ClaimStartersAsync();
        Assert.True(heroes.Count >= 2, "the starter claim must hand out enough heroes to try this");

        var open = await alice.Tournament.OpenAsync(new OpenTournamentRequest(heroes[0].Id, BuyIn, 4));
        await alice.Dev.PayInvoiceAsync(new { open.BuyIn.InvoiceId });

        var refused = await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => alice.Tournament.JoinAsync(open.Tournament.Id, new JoinTournamentRequest(heroes[1].Id)));
        Assert.Contains("already joined", refused.Message);
    }

    /// <summary>
    /// The defect: an entrant in a bracket that can never fill gets their sats back, without an operator.
    /// Net-zero at the chain — every cleared buy-in goes back to the player who paid it, and the house takes
    /// no rake on a refund.
    /// </summary>
    [Fact]
    public async Task AnEntrant_CanCallOffAnOpenBracket_AndEveryClearedBuyInGoesHome()
    {
        using var factory = new WebApplicationFactory<Program>();
        var chain = (InMemoryChainService)factory.Services.GetRequiredService<IChainService>();
        chain.FundTreasury(100_000);

        var (tid, alice, _, treasuryBeforeBracket) = await StuckAtTwoOfFourAsync(factory, chain, "Stuck");

        // The pot really is escrowed: both buy-ins reached the treasury.
        Assert.Equal(treasuryBeforeBracket + 2 * BuyIn, await chain.TreasuryBalanceAsync());
        var stuck = await alice.Tournament.GetAsync(tid);
        Assert.Equal("open", stuck.Status);
        Assert.Equal(2, stuck.Joined);
        Assert.Equal(4, stuck.Size);

        // Bob is an entrant too, but Alice — who opened it — calls it off.
        var refund = await alice.Tournament.RefundAsync(tid);

        Assert.Equal("refunded", refund.Tournament.Status);
        Assert.Equal(2, refund.EntrantsRefunded);
        Assert.Equal(2 * BuyIn, refund.RefundedSats);
        // Net zero: the pot came in and went straight back out. No rake on a refund.
        Assert.Equal(treasuryBeforeBracket, await chain.TreasuryBalanceAsync());
    }

    /// <summary>
    /// Calling a bracket off is an ENTRANT's power, not a passer-by's. A signed-in stranger with nothing at
    /// stake must not be able to unwind other people's pot — the one who pays is the one who decides.
    /// </summary>
    [Fact]
    public async Task AStranger_CannotCallOffAnOpenBracketTheyHaveNoStakeIn()
    {
        using var factory = new WebApplicationFactory<Program>();
        var chain = (InMemoryChainService)factory.Services.GetRequiredService<IChainService>();
        chain.FundTreasury(100_000);

        var (tid, _, _, _) = await StuckAtTwoOfFourAsync(factory, chain, "Stranger");
        var (mallory, _) = await factory.RegisterAsync("Stranger-Mallory");
        await mallory.ClaimStartersAsync();

        var escrowed = await chain.TreasuryBalanceAsync();
        var refused = await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => mallory.Tournament.RefundAsync(tid));
        Assert.Contains("entrant", refused.Message);

        // The bracket is untouched and the pot is still escrowed.
        var after = await mallory.Tournament.GetAsync(tid);
        Assert.Equal("open", after.Status);
        Assert.Equal(2, after.Joined);
        Assert.Equal(escrowed, await chain.TreasuryBalanceAsync());
    }

    /// <summary>
    /// The line this must not cross. Once a bracket is FULL the field is locked and committed, and it is
    /// about to be fought for real sats — letting an entrant call it off THEN would be a free look at the
    /// draw followed by an exit, which is a way to never lose. Full brackets get resolved, or refunded only
    /// on the existing "can never run again" proof.
    /// </summary>
    [Fact]
    public async Task AnEntrant_CannotCallOffABracketOnceItIsFull()
    {
        using var factory = new WebApplicationFactory<Program>();
        var chain = (InMemoryChainService)factory.Services.GetRequiredService<IChainService>();
        chain.FundTreasury(100_000);

        var (alice, _) = await factory.RegisterAsync("Full-Alice");
        var (bob, _) = await factory.RegisterAsync("Full-Bob");
        var aliceHero = (await alice.ClaimStartersAsync())[0].Id;
        var bobHero = (await bob.ClaimStartersAsync())[0].Id;

        var open = await alice.Tournament.OpenAsync(new OpenTournamentRequest(aliceHero, BuyIn, 2));
        await alice.Dev.PayInvoiceAsync(new { open.BuyIn.InvoiceId });
        var join = await bob.Tournament.JoinAsync(open.Tournament.Id, new JoinTournamentRequest(bobHero));
        await bob.Dev.PayInvoiceAsync(new { join.BuyIn.InvoiceId });

        var full = await alice.Tournament.GetAsync(open.Tournament.Id);
        Assert.Equal("full", full.Status);

        var escrowed = await chain.TreasuryBalanceAsync();
        var refused = await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => alice.Tournament.RefundAsync(open.Tournament.Id));
        Assert.Contains("can still be resolved", refused.Message);
        Assert.Equal(escrowed, await chain.TreasuryBalanceAsync());
    }

    /// <summary>
    /// An entrant who joined but never PAID is not owed anything back. Only sats that actually reached the
    /// treasury are returned — "refunding" an unpaid seat would pay out money the treasury never took in,
    /// which on a real-bitcoin balance is a straight loss.
    /// </summary>
    [Fact]
    public async Task CallingOffABracket_ReturnsOnlyTheBuyInsThatActuallyCleared()
    {
        using var factory = new WebApplicationFactory<Program>();
        var chain = (InMemoryChainService)factory.Services.GetRequiredService<IChainService>();
        chain.FundTreasury(100_000);

        var (alice, _) = await factory.RegisterAsync("Unpaid-Alice");
        var (bob, _) = await factory.RegisterAsync("Unpaid-Bob");
        var aliceHero = (await alice.ClaimStartersAsync())[0].Id;
        var bobHero = (await bob.ClaimStartersAsync())[0].Id;

        var open = await alice.Tournament.OpenAsync(new OpenTournamentRequest(aliceHero, BuyIn, 4));
        await alice.Dev.PayInvoiceAsync(new { open.BuyIn.InvoiceId });
        // Bob takes a seat and never pays for it.
        await bob.Tournament.JoinAsync(open.Tournament.Id, new JoinTournamentRequest(bobHero));

        var beforeRefund = await chain.TreasuryBalanceAsync();
        var refund = await alice.Tournament.RefundAsync(open.Tournament.Id);

        Assert.Equal("refunded", refund.Tournament.Status);
        Assert.Equal(1, refund.EntrantsRefunded);      // Alice only
        Assert.Equal(BuyIn, refund.RefundedSats);
        Assert.Equal(beforeRefund - BuyIn, await chain.TreasuryBalanceAsync());
    }
}
