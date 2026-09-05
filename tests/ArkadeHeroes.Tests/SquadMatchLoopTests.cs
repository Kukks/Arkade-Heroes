using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ArkadeHeroes.Tests;

/// <summary>Wagered 3v3 squad matches end to end: lineup validation, a staked best-of-3 that pays the
/// winner + issues a per-duel receipt, and a trustlessly-verifiable replay. Fresh factory per test.</summary>
public class SquadMatchLoopTests
{
    // Two gen-0 starters + one dev-minted hero = a full 3-hero lineup.
    static async Task<List<string>> Lineup(ArkadeHeroesClient c)
    {
        var ids = (await c.ClaimStartersAsync()).Select(h => h.Id).ToList();
        ids.Add((await c.Dev.MintHeroAsync()).Id);
        return ids.Take(3).ToList();
    }

    [Fact]
    public async Task Open_RejectsBadLineups()
    {
        using var factory = new WebApplicationFactory<Program>();
        var (alice, _) = await factory.RegisterAsync("Squad-Val-A");
        var (bob, _) = await factory.RegisterAsync("Squad-Val-B");
        var mine = await Lineup(alice);
        var theirs = await Lineup(bob);

        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() =>      // wrong size
            alice.Squad.OpenAsync(new OpenSquadMatchRequest(mine.Take(2).ToList(), theirs, 1000)));
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() =>      // non-distinct
            alice.Squad.OpenAsync(new OpenSquadMatchRequest(new List<string> { mine[0], mine[0], mine[1] }, theirs, 1000)));
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() =>      // challenger doesn't own a hero
            alice.Squad.OpenAsync(new OpenSquadMatchRequest(theirs, mine, 1000)));
    }

    [Fact]
    public async Task StakedSquadMatch_ResolvesBestOfThree_PaysWinner_AndVerifies()
    {
        using var factory = new WebApplicationFactory<Program>();
        var (alice, _) = await factory.RegisterAsync("Squad-A");
        var (bob, _) = await factory.RegisterAsync("Squad-B");
        var mine = await Lineup(alice);
        var theirs = await Lineup(bob);

        var (open, accept) = await alice.StakedSquadAsync(bob, mine, theirs, 1000);
        Assert.Null(open.StakeInvoice);                   // covenant-only: each side stakes its own escrow
        Assert.NotEqual(open.EscrowAddress, accept.EscrowAddress);

        var resolve = await alice.Squad.ResolveAsync(open.MatchId, new FightRequest("squad-nonce"));

        Assert.Equal(3, resolve.Result.Duels.Count);
        Assert.Equal(3, resolve.Result.ChallengerWins + resolve.Result.DefenderWins);   // odd → decisive
        Assert.Equal(3, resolve.Receipts.Count);                                         // one "match" receipt per duel
        Assert.All(resolve.Receipts, r => Assert.Equal("match", r.Type));
        Assert.Equal(2000, resolve.WinnerPayoutSats);                                     // pot = 2 × stake

        // Trustless replay: re-running the best-of-3 from the revealed seed verifies.
        var replay = await alice.Squad.ReplayAsync(open.MatchId);
        Assert.True(FairnessAudit.VerifySquad(open.MatchId, "squad-nonce", replay.CommitmentHex, replay).Ok);
    }

    /// <summary>
    /// The board's 50-row cap must drop the OLDEST rows, not arbitrary ones. Listing took 50 straight off
    /// an unordered dictionary, so once more than 50 squad matches existed a freshly-opened one could be
    /// among the rows cut — a player opens a match and it is simply invisible to the opponent.
    /// </summary>
    [Fact]
    public async Task List_ReturnsTheNewestFifty_NotAnArbitraryFifty()
    {
        using var factory = new WebApplicationFactory<Program>();
        var (alice, _) = await factory.RegisterAsync("Squad-List-A");
        var (bob, _) = await factory.RegisterAsync("Squad-List-B");
        var mine = await Lineup(alice);
        var theirs = await Lineup(bob);

        // Friendly (wager 0) so opening bills nothing, and the same two lineups throughout: the ONLY
        // thing that differs between these matches is when each was opened. Ten more than the cap, so
        // the ten oldest are exactly the rows that have to fall off.
        var opened = new List<string>();
        for (var i = 0; i < 60; i++)
            opened.Add((await alice.Squad.OpenAsync(new OpenSquadMatchRequest(mine, theirs, 0))).MatchId);

        var listed = await alice.Squad.ListAsync();

        Assert.Equal(50, listed.Count);
        Assert.Equal(opened.Skip(10).Reverse().ToList(), listed.Select(m => m.MatchId).ToList());
    }
}
