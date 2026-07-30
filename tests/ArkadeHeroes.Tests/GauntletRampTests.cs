using ArkadeHeroes.Core.Progression;

namespace ArkadeHeroes.Tests;

/// <summary>
/// The gauntlet ladder's opening rung does not exist for the cohort it was written for.
///
/// <c>Content/dungeons.json</c> authors the ramp as level offsets −1, 0, +1, +2, +3, so wave 1 is meant
/// to be one level BELOW the runner — a soft opener for a hero taking its first run. The engine resolves
/// that through <c>Dungeon.GhostLevel</c>, which clamps with <c>Math.Max(1, …)</c> because no hero can be
/// below level 1. For a level-1 hero the −1 therefore evaporates: wave 1 arrives at level 1, a PEER, and
/// so does wave 2. Every hero above level 1 gets the soft opener the content asked for; the entry cohort,
/// which is every brand-new player, is the only one that does not.
///
/// It costs real progress rather than just flavour. A run ends at the first loss and pays on waves
/// CLEARED, so losing wave 1 yields zero XP while still charging the entry fee and starting the
/// per-hero cooldown — and the gauntlet is the game's ONLY XP mint. Measured over 400 seeded runs per
/// level, a level-1 hero clears nothing 52% of the time against roughly 46% at levels 2/3/5/8: the
/// missing rung is worth about six points of dead runs to the players least equipped to absorb them.
///
/// These tests pin the STRUCTURE, not the balance. The clear-rate percentages above belong in a report,
/// not an assertion — content is authored data and is meant to be retuned, so a test that fixed the
/// numbers would break on every legitimate tweak. What is pinned is the shape: a negative offset on the
/// first wave cannot reach a level-1 hero. Fixing that is a design decision (retune the ladder, express
/// early difficulty through something other than level, or start heroes above 1); this file only makes
/// sure the gap is visible while it stands.
/// </summary>
public class GauntletRampTests
{
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
    public void ButALevelOneHeroMeetsAPeerOnWaveOne_TheSoftOpenerIsUnreachable()
    {
        // The defect. The clamp is correct on its own terms — there is no level 0 — so the authored −1
        // simply has nowhere to land, and the opener silently becomes an even fight.
        Assert.Equal(1, Gauntlet.Content.GhostLevel(heroLevel: 1, wave: 1));

        // Wave 2 is offset 0, so the entry cohort faces TWO peers before the ramp starts climbing.
        Assert.Equal(1, Gauntlet.Content.GhostLevel(heroLevel: 1, wave: 2));
    }

    [Fact]
    public void EveryHeroAboveTheFloorDoesGetIt_SoTheOffsetMechanismItselfIsFine()
    {
        // Proves the finding is specifically about the floor and not a broken offset: from level 2 up,
        // wave 1 lands below the runner exactly as authored.
        for (var level = 2; level <= 12; level++)
            Assert.Equal(level - 1, Gauntlet.Content.GhostLevel(level, wave: 1));
    }

    [Fact]
    public void TheEntryCohortFacesACompressedLadder_OneRungShorterThanEveryoneElse()
    {
        // The consequence stated as a whole-ladder shape: a level-1 hero sees only four distinct
        // difficulties where a higher-level hero sees five, because the bottom two collapse together.
        var atFloor = Enumerable.Range(1, Gauntlet.WaveCount)
            .Select(w => Gauntlet.Content.GhostLevel(1, w)).ToList();
        var aboveFloor = Enumerable.Range(1, Gauntlet.WaveCount)
            .Select(w => Gauntlet.Content.GhostLevel(5, w)).ToList();

        Assert.Equal(Gauntlet.WaveCount - 1, atFloor.Distinct().Count());
        Assert.Equal(Gauntlet.WaveCount, aboveFloor.Distinct().Count());
    }

    [Fact]
    public void ALostFirstWavePaysNothing_WhichIsWhyTheMissingRungCosts()
    {
        // Why the rung matters at all: the schedule pays on waves CLEARED, so the difference between
        // losing and winning the opener is the difference between a wasted entry fee and a real reward.
        Assert.Equal(0, Gauntlet.Content.XpFor(0));
        Assert.True(Gauntlet.Content.XpFor(1) > 0);
    }
}
