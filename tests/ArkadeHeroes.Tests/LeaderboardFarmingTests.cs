using ArkadeHeroes.Core.Progression;
using ArkadeHeroes.Shared;

namespace ArkadeHeroes.Tests;

/// <summary>
/// Rank cannot be farmed: a staked win only ranks if the fight actually MOVED XP.
///
/// The XP ladder was always defended against farming down it — <see cref="Leveling.XpTransfer"/> pays
/// ZERO once the winner is several levels above the loser — but RANK counted every staked win
/// identically, so a fight the ruleset had already judged worthless still climbed the board. Rank is
/// paid in real sats (the season settles its top three from a pot with a treasury-funded base), and the
/// same-owner guard on a staked match is per-ACCOUNT while the Sybil argument is about self-play "across
/// any number of wallets" — so a second wallet holding a free starter was a renewable source of rank for
/// the price of the match fees.
///
/// <see cref="LeaderboardBuilder"/> now credits a win only when the receipt records a non-zero transfer.
/// <c>XpAwardA</c> is the clamp's own recorded output, so "zero" is the ruleset's verdict rather than a
/// second judgement invented by the board — which also keeps the board recomputable from receipts alone,
/// which is its whole claim.
///
/// The rule is deliberately "no XP moved", NOT "the gap was too wide". Those come apart at the bottom of
/// the ladder and only the former is safe: two broke level-1 heroes are peers, so a gap-based test would
/// happily rank their fights and a pair of free starters in two wallets would print rank for nothing.
/// See <see cref="Leveling.PayableTransfer"/> for the sibling defect on the XP side.
/// </summary>
public class LeaderboardFarmingTests
{
    private static ProgressionReceiptDto Match(
        string id, string winner, string loser, int winnerLevel, int loserLevel, long xp)
        => new("match", id, winner, loser, winner,
               "seed", "nonce", "commit", xp, -xp, winnerLevel, loserLevel, 1_000, "", "");

    private static Dictionary<string, (string Name, int Level, string OwnerId)> Heroes(
        params (string Id, string Name, int Level, string Owner)[] rows)
        => rows.ToDictionary(r => r.Id, r => (r.Name, r.Level, r.Owner));

    [Fact]
    public void TheGameRatesAFarmedWinWorthless_ItTransfersNoXpAtAll()
    {
        // The premise the rest of the file rests on: the ruleset already decides a big-gap win earns
        // nothing, and rank now agrees with that verdict instead of contradicting it.
        Assert.Equal(0, Leveling.XpTransfer(winnerLevel: 20, loserLevel: 1));
        Assert.True(Leveling.XpTransfer(winnerLevel: 5, loserLevel: 5) > 0);
    }

    [Fact]
    public void AndThatWorthlessWinEarnsNoRank_ThoughTheMatchIsStillTallied()
    {
        var heroes = Heroes(("farmer", "Farmer", 20, "attacker"), ("bag", "Bag", 1, "attacker-alt"));
        var board = LeaderboardBuilder.Build(heroes, [Match("m1", "farmer", "bag", 20, 1, xp: 0)]);

        var farmer = board.Single(e => e.HeroId == "farmer");
        Assert.Equal(0, farmer.Wins);      // no XP moved → no win banked
        Assert.Equal(1, farmer.Matches);   // the fight still happened; hiding it would conceal the attempt
    }

    [Fact]
    public void AWinThatMovedXpDoesRank()
    {
        // The other half of the rule, so the fix cannot pass by simply refusing every win.
        var heroes = Heroes(("honest", "Honest", 20, "somebody"), ("peer", "Peer", 20, "rival"));
        var board = LeaderboardBuilder.Build(
            heroes, [Match("m1", "honest", "peer", 20, 20, xp: Leveling.XpTransfer(20, 20))]);

        var honest = board.Single(e => e.HeroId == "honest");
        Assert.Equal(1, honest.Wins);
        Assert.Equal(1, honest.Rank);
    }

    [Fact]
    public void VolumeOfWorthlessWinsNoLongerOutranksFewerHardWins()
    {
        // The exposure in the shape that cost money: a farmer beating a level-1 hero for 0 XP three times
        // used to outrank a hero that won twice against real peers for real XP.
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

        Assert.Equal(1, board.Single(e => e.HeroId == "honest").Rank);
        Assert.Equal(2, board.Single(e => e.HeroId == "honest").Wins);
        Assert.Equal(0, board.Single(e => e.HeroId == "farmer").Wins);
        Assert.True(board.Single(e => e.HeroId == "honest").Rank
                    < board.Single(e => e.HeroId == "farmer").Rank,
            "two real wins must outrank three farmed ones");
    }

    [Fact]
    public void GrindingOneOpponentForeverStillEarnsNothing()
    {
        // Nothing dedupes opponents, and nothing needs to: volume is harmless once each worthless win is
        // worth zero. 25 farmed fights bank 25 matches and no rank at all.
        var heroes = Heroes(("farmer", "Farmer", 20, "attacker"), ("bag", "Bag", 1, "attacker-alt"));
        var receipts = Enumerable.Range(0, 25)
            .Select(i => Match($"f{i}", "farmer", "bag", 20, 1, xp: 0))
            .ToList();

        var farmer = LeaderboardBuilder.Build(heroes, receipts).Single(e => e.HeroId == "farmer");
        Assert.Equal(0, farmer.Wins);
        Assert.Equal(25, farmer.Matches);
    }

    [Fact]
    public void ABrokeLoserYieldsNoRankEither_WhichIsWhyTheRuleIsAboutXpNotTheGap()
    {
        // The case that rules out the tempting "compare the levels instead" fix. These two are PEERS at
        // level 1, so any gap-based test would rank the fight — but neither owns XP, so nothing was at
        // stake, and a pair of free starters across two wallets would print rank forever. Keying on the
        // recorded transfer closes the bottom of the ladder as well as the top.
        Assert.True(Leveling.XpTransfer(1, 1) > 0);           // the GAP would have paid…
        Assert.Equal(0, Leveling.PayableTransfer(1, 1, 0));   // …but a broke loser pays nothing

        var heroes = Heroes(("a", "A", 1, "w1"), ("b", "B", 1, "w2"));
        var receipts = Enumerable.Range(0, 10).Select(i => Match($"m{i}", "a", "b", 1, 1, xp: 0)).ToList();

        Assert.Equal(0, LeaderboardBuilder.Build(heroes, receipts).Single(e => e.HeroId == "a").Wins);
    }

    [Fact]
    public void TheReceiptCarriesPostFightLevels_WhichIsWhyTheRuleUsesTheRecordedTransfer()
    {
        // Why the rule keys on XpAwardA and not on the levels: LevelA/LevelB are documented as "resulting
        // levels AFTER the event" and the server fills them once ApplyXp has run, so they are not the
        // fight-time levels the clamp was evaluated on. A loser that deleveled reports the level it
        // dropped TO, which makes the gap look wider than the one the game actually judged. XpAwardA is
        // exact — it IS the clamp's output, recorded at settle time.
        var farmed = Match("m1", "farmer", "bag", winnerLevel: 20, loserLevel: 1, xp: 0);
        var real = Match("m2", "honest", "peer", winnerLevel: 20, loserLevel: 20,
                         xp: Leveling.XpTransfer(20, 20));

        Assert.Equal(0, farmed.XpAwardA);
        Assert.True(real.XpAwardA > 0);
        Assert.Equal(20, farmed.LevelA);
        Assert.Equal(1, farmed.LevelB);
    }
}
