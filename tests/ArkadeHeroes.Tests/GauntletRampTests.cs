using System.Security.Cryptography;
using System.Text;
using ArkadeHeroes.Core;
using ArkadeHeroes.Core.Combat;
using ArkadeHeroes.Core.Content;
using ArkadeHeroes.Core.Fairness;
using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Heroes;
using ArkadeHeroes.Core.Progression;

namespace ArkadeHeroes.Tests;

/// <summary>
/// The gauntlet ladder's opening rung, and the fact that it now reaches the cohort it was written for.
///
/// <c>Content/dungeons.json</c> authors the ramp as level offsets −1, 0, +1, +2, +3, so wave 1 is meant to
/// be one level BELOW the runner — a soft opener for a hero taking its first run. That intent used to
/// evaporate for a level-1 hero: <c>Dungeon.GhostLevel</c> clamps with <c>Math.Max(1, …)</c> because no
/// hero can be below level 1, so wave 1 arrived as a PEER and so did wave 2. Every hero above level 1 got
/// the soft opener the content asked for; the entry cohort, which is every brand-new player, was the only
/// one that did not — and it saw one fewer distinct rung than everybody else.
///
/// It cost real progress rather than just flavour. A run ends at the first loss and pays on waves CLEARED,
/// so losing wave 1 yields zero XP while still charging the entry fee and starting the per-hero cooldown —
/// and the gauntlet is the game's ONLY XP mint.
///
/// The floor itself was never the thing to change: there is no level 0, the stat curve refuses one, and a
/// level-0 ghost would drop under the first gene-skill unlock and arrive a whole SKILL short. Nor could a
/// content retune reach it — <c>max(1, 1 + offset)</c> is 1 for EVERY offset at or below zero. So the
/// shortfall the floor eats is now paid on the damage axis instead (<c>Dungeon.GhostHandicap</c>), which is
/// the one place that still has room below a level-1 peer.
///
/// These tests pin the STRUCTURE, not the balance. Clear-rate percentages belong in a report, not an
/// assertion — content is authored data and is meant to be retuned, so a test that fixed the numbers would
/// break on every legitimate tweak. What is pinned is the shape: the entry cohort gets a genuine opener
/// below a peer, gets the same number of distinct rungs as everyone else, and no other cohort moves at all.
/// </summary>
public class GauntletRampTests
{
    private static Hero Runner(int level, int i = 0) => new()
    {
        Id = "hero-1",
        OwnerId = "player-1",
        Name = "Runner",
        Genome = Genome.NewGen0(SHA256.HashData(Encoding.UTF8.GetBytes($"hero-{i}"))),
        Level = level,
    };

    /// <summary>How hard wave <paramref name="wave"/> is for <paramref name="heroLevel"/>, as the pair of
    /// axes the ladder actually varies: the ghost's level, and the damage handicap the floor pays back.
    /// Two waves are the same difficulty only if they agree on BOTH.</summary>
    private static (int Level, double Handicap) Rung(int heroLevel, int wave) =>
        (Gauntlet.GhostLevel(heroLevel, wave), Gauntlet.GhostHandicap(heroLevel, wave));

    [Fact]
    public void WaveOneIsAuthoredEasierThanTheRunner()
    {
        // The intent, read from the content rather than assumed: the ladder opens below the runner and
        // climbs. If this ever goes red the ramp itself was re-authored and the rest of the file is moot.
        var offsets = Gauntlet.Content.Waves.Select(w => w.LevelOffset).ToList();

        Assert.True(offsets[0] < 0, "wave 1 is meant to be easier than the runner");
        Assert.Equal(offsets.OrderBy(o => o), offsets);   // and difficulty only ever climbs
    }

    [Fact]
    public void TheEntryCohortDoesGetTheOpener_OnTheOneAxisWithRoomBelowAPeer()
    {
        // The floor is still the floor — the ghost's LEVEL cannot go under 1, and this test does not ask
        // it to. What changed is that the level the floor ate is now accounted for…
        Assert.Equal(1, Gauntlet.GhostLevel(heroLevel: 1, wave: 1));
        Assert.Equal(1, Gauntlet.Content.ClampedLevels(heroLevel: 1, wave: 1));

        // …and paid back, so a brand-new hero's opener really is easier than an even fight.
        Assert.True(Gauntlet.GhostHandicap(heroLevel: 1, wave: 1) < 1.0,
            "a level-1 hero's wave 1 must be softer than a peer — that is the whole authored intent");
    }

    [Fact]
    public void EveryHeroAboveTheFloorIsUntouched_ExactlyOneSoReplaysAreBitIdentical()
    {
        // The offset mechanism was never broken above the floor, so the fix must be invisible there. Exact
        // equality to 1.0 is the point rather than a tolerance: multiplying a damage roll by exactly 1.0 is
        // exact in IEEE-754, so these fights resolve bit-for-bit as they did before the handicap existed.
        for (var level = 2; level <= 12; level++)
        {
            Assert.Equal(level - 1, Gauntlet.GhostLevel(level, wave: 1));
            for (var wave = 1; wave <= Gauntlet.WaveCount; wave++)
            {
                Assert.Equal(0, Gauntlet.Content.ClampedLevels(level, wave));
                Assert.Equal(1.0, Gauntlet.GhostHandicap(level, wave));
            }
        }
    }

    [Fact]
    public void NothingTheContentAsksForGoesUnaccounted()
    {
        // The invariant the two halves are built on, and which Dungeon's own docs claim: whatever the floor
        // ADDED to reach a legal level is exactly what ClampedLevels reports back. Pinned rather than
        // trusted, because a drift between the two would silently under- or over-pay the handicap.
        for (var level = 1; level <= 12; level++)
            for (var wave = 1; wave <= Gauntlet.WaveCount; wave++)
            {
                var authored = level + Gauntlet.Content.Waves[wave - 1].LevelOffset;
                Assert.Equal(Gauntlet.GhostLevel(level, wave),
                    authored + Gauntlet.Content.ClampedLevels(level, wave));
            }
    }

    [Fact]
    public void TheEntryCohortSeesAFullSetOfDistinctRungs_LikeEveryOtherCohort()
    {
        // The consequence stated as a whole-ladder shape. This is the assertion that used to record the
        // defect: at the floor the bottom two rungs collapsed together and a level-1 hero saw one fewer
        // distinct difficulty than a level-5 hero. Counted across BOTH axes they are distinct again.
        var atFloor = Enumerable.Range(1, Gauntlet.WaveCount).Select(w => Rung(1, w)).ToList();
        var aboveFloor = Enumerable.Range(1, Gauntlet.WaveCount).Select(w => Rung(5, w)).ToList();

        Assert.Equal(Gauntlet.WaveCount, atFloor.Distinct().Count());
        Assert.Equal(Gauntlet.WaveCount, aboveFloor.Distinct().Count());
    }

    [Fact]
    public void AndTheEntryCohortsLadderStillOnlyEverClimbs()
    {
        // Distinct is not enough on its own — the opener has to be the EASIEST rung, not merely a different
        // one. Wave 1 is softer than wave 2 at the same ghost level; from there the level itself climbs.
        Assert.Equal(Gauntlet.GhostLevel(1, 1), Gauntlet.GhostLevel(1, 2));
        Assert.True(Gauntlet.GhostHandicap(1, 1) < Gauntlet.GhostHandicap(1, 2));

        for (var wave = 2; wave < Gauntlet.WaveCount; wave++)
            Assert.True(Gauntlet.GhostLevel(1, wave) < Gauntlet.GhostLevel(1, wave + 1));
    }

    [Fact]
    public void TheHandicapReachesTheGhostAndNotTheRunner()
    {
        // Wiring, not balance: the resolver must hand the multiplier to the GHOST. Passed to the hero, or
        // dropped, or inverted, this comparison goes the other way. Measured against a locally rebuilt
        // handicap-free ladder rather than against a pinned percentage, so retuning the content cannot
        // break it — only misrouting the handicap can.
        var withHandicap = 0;
        var without = 0;
        for (var i = 0; i < 200; i++)
        {
            var entropy = SHA256.HashData(Encoding.UTF8.GetBytes($"run-{i}"));
            withHandicap += Gauntlet.Resolve(Runner(1, i), entropy).WavesCleared;
            without += ClearedWithoutHandicap(Runner(1, i), entropy);
        }

        Assert.True(withHandicap > without,
            $"the entry cohort must clear more with the opener than without it ({withHandicap} vs {without})");
    }

    /// <summary>Gauntlet.Resolve's loop with the handicap forced off — the counterfactual ladder.</summary>
    private static int ClearedWithoutHandicap(Hero hero, byte[] entropy)
    {
        var cleared = 0;
        for (var wave = 1; wave <= Gauntlet.WaveCount; wave++)
        {
            var ghost = Gauntlet.GhostFor(entropy, wave, hero.Level);
            var seed = CommitReveal.DeriveEntropy(entropy, "gauntlet-fight", wave.ToString());
            if (BattleEngine.Fight(hero, ghost, seed, GameConfig.Default).WinnerId != hero.Id) break;
            cleared++;
        }
        return cleared;
    }

    [Fact]
    public void ADeeplyNegativeAuthoredOffsetCannotDriveTheHandicapToZeroOrBelow()
    {
        // Waves are AUTHORED, so the floor has to hold against content nobody has written yet. Without the
        // clamp an offset this deep walks the multiplier negative and a ghost's blows start healing.
        var absurd = new Dungeon("absurd", "Absurd", 250, 10, true, DropRoll.DeterministicRng,
            [new DungeonWave(1, -40, 15, [])], []);

        Assert.Equal(40, absurd.ClampedLevels(heroLevel: 1, wave: 1));
        Assert.Equal(Dungeon.MinGhostHandicap, absurd.GhostHandicap(heroLevel: 1, wave: 1));
        Assert.True(absurd.GhostHandicap(heroLevel: 1, wave: 1) > 0);
    }

    [Fact]
    public void ALostFirstWavePaysNothing_WhichIsWhyTheOpenerIsWorthHaving()
    {
        // Why the rung matters at all: the schedule pays on waves CLEARED, so the difference between
        // losing and winning the opener is the difference between a wasted entry fee and a real reward.
        Assert.Equal(0, Gauntlet.Content.XpFor(0));
        Assert.True(Gauntlet.Content.XpFor(1) > 0);
    }
}
