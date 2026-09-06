using ArkadeHeroes.Server;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ArkadeHeroes.Tests;

/// <summary>A stud breed bills the proposer twice, and only the stud fee they chose was ever on the wire.
/// The other is priced off a hero they do not own, so it is the one they cannot work out themselves.</summary>
public class StudBreedFeeDisclosureTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public StudBreedFeeDisclosureTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task AnAcceptedProposalPublishesTheBreedFeeItIsAboutToBill()
    {
        var (alice, _) = await _factory.RegisterAsync("Stud-Disclose-A");
        var (bob, _) = await _factory.RegisterAsync("Stud-Disclose-B");
        var mine = (await alice.ClaimStartersAsync())[0].Id;
        var theirs = (await bob.ClaimStartersAsync())[0].Id;

        var proposal = await alice.Stud.ProposeAsync(new StudProposeRequest(mine, theirs, 3_000));

        Assert.Null((await alice.Stud.ListAsync())
            .Single(p => p.ProposalId == proposal.ProposalId).BreedFeeSats);

        await bob.Stud.AcceptAsync(proposal.ProposalId);

        // Bound to the invoice, never a literal: a merely PLAUSIBLE quote is the failure being caught.
        var bill = await alice.Stud.InvoicesAsync(proposal.ProposalId);
        var published = (await alice.Stud.ListAsync())
            .Single(p => p.ProposalId == proposal.ProposalId).BreedFeeSats;

        Assert.Equal(bill.BreedFeeInvoice.AmountSats, published);
        Assert.True(published > 0, "a breed fee of zero would make the disclosure vacuous");
        Assert.NotEqual(proposal.StudFeeSats, published);
    }
}
