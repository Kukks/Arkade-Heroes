using System.Security.Cryptography;
using System.Text;
using ArkadeHeroes.Core.Content;
using ArkadeHeroes.Core.Progression;

namespace ArkadeHeroes.Tests;

/// <summary>
/// How an authored dungeon picks its drop. The property that matters is not "random" but REPRODUCIBLE: a
/// drop a client cannot re-derive from the run's commit-reveal entropy is a drop it cannot verify, and an
/// unverifiable payout is indistinguishable from a server paying whatever it likes. There is deliberately
/// no roll mode that reaches for ambient randomness.
/// </summary>
public class ContentDropRollTests
{
    private static byte[] Entropy(int i) => SHA256.HashData(Encoding.UTF8.GetBytes($"drop-roll-{i}"));

    private static Dungeon Pit(string drops, string roll = "DeterministicRng", string id = "pit") =>
        ContentPackLoader.Parse(Items, $$"""
            {
              "dungeons": [
                {
                  "id": "{{id}}", "name": "The Pit",
                  "entryFeeBonusSats": 250, "xpLevelCap": 10,
                  "dropRequiresFullClear": true, "dropRoll": "{{roll}}",
                  "waves": [ { "wave": 1, "levelOffset": -1, "xp": 15, "ghostGear": [] } ],
                  "drops": {{drops}}
                }
              ]
            }
            """).Dungeons[0];

    private const string Items = """
        {
          "packId": "roll",
          "items": [
            { "id": "common", "name": "Common", "slot": "Trinket", "priceSats": 100, "minLevel": 1 },
            { "id": "rare", "name": "Rare", "slot": "Trinket", "priceSats": 200, "minLevel": 1 }
          ]
        }
        """;

    private const string EvenTable = """[ { "itemId": "common", "weight": 1 }, { "itemId": "rare", "weight": 1 } ]""";
    private const string SkewedTable = """[ { "itemId": "common", "weight": 99 }, { "itemId": "rare", "weight": 1 } ]""";

    [Fact]
    public void TheSameEntropyAlwaysRollsTheSameDrop()
    {
        // No ambient randomness anywhere: a thousand repeats of the same input give one answer. This is
        // what FairnessAudit relies on when it recomputes a payout it did not observe.
        var pit = Pit(EvenTable);
        for (var i = 0; i < 50; i++)
        {
            var first = pit.RollDrop(Entropy(i), 1);
            for (var repeat = 0; repeat < 20; repeat++)
                Assert.Equal(first, pit.RollDrop(Entropy(i), 1));
        }
    }

    [Fact]
    public void TheRollIsRebuiltIdenticallyByASeparatelyParsedPack()
    {
        // The client parses its own copy of the content; the server parses its own. Two independent parses
        // of the same bytes must roll the same drop, or a verifier would disagree with an honest server.
        var a = Pit(EvenTable);
        var b = Pit(EvenTable);
        for (var i = 0; i < 100; i++)
            Assert.Equal(a.RollDrop(Entropy(i), 1), b.RollDrop(Entropy(i), 1));
    }

    [Fact]
    public void WeightsActuallySteerTheOutcome()
    {
        // A 99:1 table must overwhelmingly pick the heavy line — otherwise "drop chance" is decoration.
        var pit = Pit(SkewedTable);
        var rare = 0;
        for (var i = 0; i < 2_000; i++)
            if (pit.RollDrop(Entropy(i), 1) == "rare") rare++;

        Assert.True(rare > 0, "the 1-in-100 line never dropped in 2000 rolls — the weights are not being read");
        Assert.True(rare < 200, $"the 1-in-100 line dropped {rare}/2000 times — far above its authored weight");
    }

    [Fact]
    public void AnEvenTableIsActuallyEven()
    {
        var pit = Pit(EvenTable);
        var common = 0;
        for (var i = 0; i < 2_000; i++)
            if (pit.RollDrop(Entropy(i), 1) == "common") common++;
        Assert.InRange(common, 850, 1_150);
    }

    [Fact]
    public void TwoDungeonsDoNotRollInLockstepOnTheSameRunEntropy()
    {
        // The DeterministicRng mode is domain-separated by dungeon id. Without that, a run that somehow
        // resolved two dungeons from one entropy would hand out correlated drops.
        var a = Pit(EvenTable, id: "pit-a");
        var b = Pit(EvenTable, id: "pit-b");
        var agree = 0;
        for (var i = 0; i < 500; i++)
            if (a.RollDrop(Entropy(i), 1) == b.RollDrop(Entropy(i), 1)) agree++;
        Assert.InRange(agree, 175, 325);   // ~50% by chance, not ~100% as lockstep would give
    }

    [Fact]
    public void TheEntropyByteModeReproducesThePublishedGauntletPickExactly()
    {
        // The published v1 roll, stated directly rather than only through the golden vector: for every
        // value of the byte it reads, the authored gauntlet must pick what the hand-written pool picked.
        var pool = Gauntlet.RewardItems;
        for (var b = 0; b < 256; b++)
        {
            var entropy = new byte[32];
            entropy[0] = (byte)b;
            Assert.Equal(pool[b % pool.Count], Gauntlet.Content.RollDrop(entropy, Gauntlet.WaveCount));
        }
        Assert.Equal(DropRoll.EntropyByte, Gauntlet.Content.Roll);
    }

    [Fact]
    public void ARunThatDidNotFullyClearDropsNothing()
    {
        for (var cleared = 0; cleared < Gauntlet.WaveCount; cleared++)
            Assert.Null(Gauntlet.Content.RollDrop(Entropy(cleared), cleared));
        Assert.NotNull(Gauntlet.Content.RollDrop(Entropy(0), Gauntlet.WaveCount));
    }

    [Fact]
    public void ARunThatClearedNothingDropsNothingEvenWhenFullClearIsNotRequired()
    {
        var open = ContentPackLoader.Parse(Items, """
            {
              "dungeons": [
                {
                  "id": "open-pit", "name": "Open Pit",
                  "entryFeeBonusSats": 250, "xpLevelCap": 10,
                  "dropRequiresFullClear": false, "dropRoll": "DeterministicRng",
                  "waves": [ { "wave": 1, "levelOffset": -1, "xp": 15, "ghostGear": [] } ],
                  "drops": [ { "itemId": "common", "weight": 1 } ]
                }
              ]
            }
            """).Dungeons[0];

        Assert.Null(open.RollDrop(Entropy(1), 0));
        Assert.Equal("common", open.RollDrop(Entropy(1), 1));
    }
}
