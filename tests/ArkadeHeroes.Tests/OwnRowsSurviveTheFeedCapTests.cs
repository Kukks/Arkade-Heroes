using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Server;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ArkadeHeroes.Tests;

/// <summary>
/// <c>/stud</c> and <c>/deathmatch</c> serve the 50 newest sessions ARENA-WIDE and the browser filters
/// them down to yours. So once fifty newer sessions exist anywhere, your own rows fall out of the feed —
/// taking the "Pay &amp; breed" button off a proposal you have already paid for, and the Settle button off a
/// death-match whose hero is locked in the joint escrow. Both feeds now add the caller's own LIVE rows
/// back, however old.
/// </summary>
public class OwnRowsSurviveTheFeedCapTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public OwnRowsSurviveTheFeedCapTests(WebApplicationFactory<Program> factory) => _factory = factory;

    /// <summary>Sessions belonging to nobody in particular, stamped NEWER than the one under test — the
    /// arena filling up around a player who is still waiting on theirs.</summary>
    private static void FloodWithNewerSessions(GameStore store, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var id = $"flood-{Guid.NewGuid():N}";
            store.StudProposals[id] = new StudProposal
            {
                Id = id, ProposerPlayerId = "someone", StudOwnerPlayerId = "another",
                ProposerHeroId = "h-x", StudHeroId = "h-y",
                ServerSeed = new byte[32], CommitmentHex = "00", StudFeeSats = 0,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(10 + i),
            };
            store.DeathMatches[id] = new DeathMatchSession
            {
                Id = id, ChallengerPlayerId = "someone", DefenderPlayerId = "another",
                ChallengerHeroId = "h-x", DefenderHeroId = "h-y",
                ServerSeed = new byte[32], CommitmentHex = "00",
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(10 + i),
            };
        }
    }

    [Fact]
    public async Task AStudProposalYouArePartyTo_SurvivesFiftyNewerOnes()
    {
        var (client, me) = await _factory.RegisterAsync("F-Stud");
        var store = _factory.Services.GetRequiredService<GameStore>();

        const string mine = "stud-mine";
        store.StudProposals[mine] = new StudProposal
        {
            Id = mine, ProposerPlayerId = me.PlayerId, StudOwnerPlayerId = "other-owner",
            ProposerHeroId = "h-a", StudHeroId = "h-b",
            ServerSeed = new byte[32], CommitmentHex = "00", StudFeeSats = 1_500,
            Accepted = true,   // consented and awaiting the reveal the proposer has already paid for
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-1),
        };
        FloodWithNewerSessions(store, 60);

        Assert.Contains(await client.Stud.ListAsync(), p => p.ProposalId == mine);

        // …and it is the CALLER that brings it back, not a wider cap: an anonymous reader still sees only
        // the newest fifty.
        var anonymous = new ArkadeHeroesClient(_factory.CreateClient());
        Assert.DoesNotContain(await anonymous.Stud.ListAsync(), p => p.ProposalId == mine);
    }

    [Fact]
    public async Task ADeathMatchYouAreStakedIn_SurvivesFiftyNewerOnes()
    {
        var (client, me) = await _factory.RegisterAsync("F-DeathMatch");
        var store = _factory.Services.GetRequiredService<GameStore>();

        const string mine = "dm-mine";
        store.DeathMatches[mine] = new DeathMatchSession
        {
            Id = mine, ChallengerPlayerId = me.PlayerId, DefenderPlayerId = "other-player",
            ChallengerHeroId = "h-a", DefenderHeroId = "h-b",
            ServerSeed = new byte[32], CommitmentHex = "00",
            Accepted = true,   // both heroes are in the joint escrow; only Settle gets them out
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-1),
        };
        FloodWithNewerSessions(store, 60);

        Assert.Contains(await client.DeathMatch.ListAsync(), d => d.DeathMatchId == mine);
    }

    [Fact]
    public async Task ASettledSessionOfYoursIsNotDraggedBack()
    {
        // Only LIVE rows are re-added. A finished one has no button left to press, so it belongs in the
        // feed's ordinary recency window like everyone else's.
        var (client, me) = await _factory.RegisterAsync("F-Settled");
        var store = _factory.Services.GetRequiredService<GameStore>();

        const string done = "dm-done";
        store.DeathMatches[done] = new DeathMatchSession
        {
            Id = done, ChallengerPlayerId = me.PlayerId, DefenderPlayerId = "other-player",
            ChallengerHeroId = "h-a", DefenderHeroId = "h-b",
            ServerSeed = new byte[32], CommitmentHex = "00",
            Accepted = true, Completed = true, WinnerHeroId = "h-a",
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-1),
        };
        FloodWithNewerSessions(store, 60);

        Assert.DoesNotContain(await client.DeathMatch.ListAsync(), d => d.DeathMatchId == done);
    }
}
