using ArkadeHeroes.Core.Progression;
using ArkadeHeroes.Shared;

namespace ArkadeHeroes.Tests;

/// <summary>
/// A KNOWN, UNRESOLVED exposure, pinned so it stays a decision instead of an accident.
///
/// The XP ladder is deliberately defended against farming down it: <see cref="Leveling.XpTransfer"/>
/// pays ZERO once the winner is several levels above the loser, and the comment on it is explicit that
/// this exists so nobody can grind a weak opponent for progression. RANK never got the same defence.
/// <see cref="LeaderboardBuilder"/> counts every staked win identically, so a fight the game already
/// judged worthless — literally zero XP — still moves a hero up the board.
///
/// That matters because rank is paid in real sats: the season settles the top three out of a pot that
/// includes a treasury-funded base, so board position is a claim on money, not a cosmetic.
///
/// The same-owner guard on a staked match is per-ACCOUNT ("Wagered matches need an opponent — you own
/// both heroes"), while the Sybil argument the conserved transfer rests on is about self-play "across
/// any number of wallets". A second wallet holding a free starter satisfies the guard, and the wager
/// itself round-trips between the two wallets, so the standing cost of a farmed win is the match fees.
///
/// These tests assert what the code DOES, not what it should do. If one went red, someone has changed
/// how rank is earned — that is the intended fix, and the pin should be updated to match it rather than
/// worked around. See the summary on <see cref="Leveling.PayableTransfer"/> for the sibling defect on
/// the XP side, which was closed.
/// </summary>
public class LeaderboardFarmingExposureTests
{
    private static ProgressionReceiptDto Match(
        string id, string winner, string loser, int winnerLevel, int loserLevel, long xp)
        => new("match", id, winner, loser, winner,
               "seed", "nonce", "commit", xp, -xp, winnerLevel, loserLevel, 1_000, "", "");

    private static Dictionary<string, (string Name, int Level, string OwnerId)> Heroes(
        params (string Id, string Name, int Level, string Owner)[] rows)
        => rows.ToDictionary(r => r.Id, r => (r.Name, r.Level, r.Owner));

    [Fact]
    public void TheGameAlreadyRatesAFarmedWinWorthless_ItTransfersNoXpAtAll()
    {
        // The defence that exists. This is the premise the rest of the file rests on: the ruleset has
        // already decided a big-gap win earns nothing, so counting it toward rank is an inconsistency,
        // not merely an unguarded case.
        Assert.Equal(0, Leveling.XpTransfer(winnerLevel: 20, loserLevel: 1));
        Assert.True(Leveling.XpTransfer(winnerLevel: 5, loserLevel: 5) > 0);
    }

    [Fact]
    public void YetThatSameWorthlessWinStillCountsFullyTowardRank()
    {
        var heroes = Heroes(("farmer", "Farmer", 20, "attacker"), ("bag", "Bag", 1, "attacker-alt"));
        var board = LeaderboardBuilder.Build(heroes, [Match("m1", "farmer", "bag", 20, 1, xp: 0)]);

        var farmer = board.Single(e => e.HeroId == "farmer");
        Assert.Equal(1, farmer.Wins);       // a zero-XP fight still banks a full win…
        Assert.Equal(1, farmer.Rank);       // …and that win is what the board ranks on
    }

    [Fact]
    public void VolumeOfWorthlessWinsOutranksFewerHardWins()
    {
        // The exposure in the shape that costs money: rank is a raw COUNT, so a farmer beating a level-1
        // hero for 0 XP three times outranks a hero that won twice against real peers for real XP.
        var heroes = Heroes(
            ("farmer", "Farmer", 20, "attacker"),
            ("bag", "Bag", 1, "attacker-alt"),
            ("honest", "Honest", 20, "somebody"),
            ("peer", "Peer", 20, "rival"));

        var board = LeaderboardBuilder.Build(heroes, [
            Match("f1", "farmer", "bag", 20, 1, xp: 0),
            Match("f2", "farmer", "bag", 20, 1, xp: 0),
            Match("f3", "farmer", "bag", 20, 1, xp: 0),
            Match("h1", "honest", "peer", 20, 20, xp: Leveling.XpTransfer(20, 20)),
            Match("h2", "honest", "peer", 20, 20, xp: Leveling.XpTransfer(20, 20)),
        ]);

        Assert.Equal(1, board.Single(e => e.HeroId == "farmer").Rank);
        Assert.Equal(2, board.Single(e => e.HeroId == "honest").Rank);
    }

    [Fact]
    public void RepeatingTheSameOpponentIsNotLimited()
    {
        // Nothing dedupes opponents, so one disposable hero in a second wallet is a renewable source of
        // rank. A distinct-opponent requirement is one of the candidate fixes; this pins that today
        // there is none.
        var heroes = Heroes(("farmer", "Farmer", 20, "attacker"), ("bag", "Bag", 1, "attacker-alt"));
        var receipts = Enumerable.Range(0, 25)
            .Select(i => Match($"f{i}", "farmer", "bag", 20, 1, xp: 0))
            .ToList();

        Assert.Equal(25, LeaderboardBuilder.Build(heroes, receipts).Single(e => e.HeroId == "farmer").Wins);
    }

    [Fact]
    public void TheReceiptCarriesPostFightLevels_WhichConstrainsAnyGapAwareFix()
    {
        // A trap for whoever implements the fix. The leaderboard's whole claim is that anyone holding the
        // receipts can recompute it, so a fix may only use facts the receipts carry — and the obvious move,
        // "re-apply the XP clamp to LevelA/LevelB", does NOT work off a single receipt.
        //
        // LevelA/LevelB are documented as "resulting levels AFTER the event", and the server fills them
        // after ApplyXp has already run, so they are not the fight-time levels the clamp was evaluated on.
        // A loser that deleveled reports the level it dropped TO, which makes the gap look wider than the
        // one the game actually judged. Reconstructing fight-time levels means folding the chain, the way
        // Receipts.ReplayLevel already does — not reading one row.
        //
        // XpAwardA is the cheaper signal and is exact: it IS the clamp's output, recorded at settle time.
        // A zero on a staked receipt is precisely "the ruleset rated this fight worthless".
        var farmed = Match("m1", "farmer", "bag", winnerLevel: 20, loserLevel: 1, xp: 0);
        var real = Match("m2", "honest", "peer", winnerLevel: 20, loserLevel: 20,
                         xp: Leveling.XpTransfer(20, 20));

        Assert.Equal(0, farmed.XpAwardA);
        Assert.True(real.XpAwardA > 0);

        // And the levels really are just carried through, so nothing downstream re-derives them.
        Assert.Equal(20, farmed.LevelA);
        Assert.Equal(1, farmed.LevelB);
    }
}
