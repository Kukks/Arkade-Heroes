using System.Security.Cryptography;
using System.Text;
using ArkadeHeroes.Core.Fairness;
using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Heroes;
using ArkadeHeroes.Core.Progression;

namespace ArkadeHeroes.Tests;

/// <summary>
/// The endless solo Trials resolver — a deterministic, client-replayable PvE ladder on an ABSOLUTE
/// difficulty curve (wave N ghost = level N), so it always terminates, its score (waves survived) reads
/// the hero's realized power, and it awards only a flavor title (no XP/item/sats → treasury-neutral).
/// </summary>
public class TrialsTests
{
    private static Hero Runner(int level)
        => new() { Id = "hero", OwnerId = "p", Name = "H", Genome = Genome.NewGen0(new byte[] { 9, 9, 9 }), Level = level };

    [Fact]
    public void ResolveIsDeterministic()
    {
        var hero = Runner(8);
        var entropy = CommitReveal.DeriveEntropy(SHA256.HashData([1]), "trial", "1");

        var a = Trials.Resolve(hero, entropy);
        var b = Trials.Resolve(hero, entropy);

        Assert.Equal(a.WavesCleared, b.WavesCleared);
        Assert.Equal(a.Waves.Count, b.Waves.Count);
        for (var i = 0; i < a.Waves.Count; i++)
        {
            Assert.Equal(a.Waves[i].Won, b.Waves[i].Won);
            Assert.Equal(a.Waves[i].GhostLevel, b.Waves[i].GhostLevel);
            Assert.Equal(a.Waves[i].Result.WinnerId, b.Waves[i].Result.WinnerId);   // replayable, wave by wave
        }
    }

    [Fact]
    public void RunEndsAtFirstLoss_AndScoreEqualsClearedCount()
    {
        var run = Trials.Resolve(Runner(6), CommitReveal.DeriveEntropy(SHA256.HashData([2]), "trial", "1"));
        // Unless the hero somehow caps out, the last recorded wave is the loss that ended the run and every
        // wave before it was a win — so the score is exactly the count of cleared waves.
        if (run.WavesCleared < Trials.MaxWaves)
        {
            Assert.False(run.Waves[^1].Won);
            Assert.Equal(run.WavesCleared, run.Waves.Count - 1);
            Assert.All(run.Waves.Take(run.WavesCleared), w => Assert.True(w.Won));
        }
    }

    [Fact]
    public void NeverExceedsMaxWaves()
    {
        // Even a maxed hero is bounded by MaxWaves (the compute safety net) and the ghost ladder terminates it.
        var run = Trials.Resolve(Runner(50), CommitReveal.DeriveEntropy(SHA256.HashData([3]), "trial", "1"));
        Assert.True(run.WavesCleared <= Trials.MaxWaves);
        Assert.True(run.Waves.Count <= Trials.MaxWaves);
    }

    [Fact]
    public void GhostLevelIsTheWaveNumber_AbsoluteLadder()
    {
        // An explicit lambda, not a method group: GhostLevel now takes an optional affix, and optional
        // parameters don't participate in method-group conversion.
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, Enumerable.Range(1, 5).Select(w => Trials.GhostLevel(w)).ToArray());
        Assert.True(Trials.GhostLevel(30) > Trials.GhostLevel(1));   // strictly climbs with depth
    }

    [Fact]
    public void GhostGearRampsWithDepth()
    {
        Assert.Empty(Trials.GhostGear(1));
        Assert.Empty(Trials.GhostGear(7));
        Assert.Equal(2, Trials.GhostGear(8).Count);                    // mid gear from wave 8
        Assert.Contains("arkforged-edge", Trials.GhostGear(15));       // top gear from wave 15
    }

    [Fact]
    public void GhostForIsDeterministicAndTrialsScoped()
    {
        var entropy = SHA256.HashData([4]);
        var g1 = Trials.GhostFor(entropy, 3);
        var g2 = Trials.GhostFor(entropy, 3);
        Assert.Equal(g1.Genome.ToHex(), g2.Genome.ToHex());   // same entropy + wave → same ghost
        Assert.Equal(3, g1.Level);                            // absolute ladder: wave 3 ghost is level 3
        Assert.StartsWith("trial-ghost-", g1.Id);             // distinct id space from the gauntlet ghosts
    }

    [Fact]
    public void TitleTiersByDepth()
    {
        Assert.Null(Trials.TitleFor(0));
        Assert.Null(Trials.TitleFor(5));
        Assert.Equal("Trialgoer", Trials.TitleFor(6));
        Assert.Equal("Trialblazer", Trials.TitleFor(12));
        Assert.Equal("Trial Legend", Trials.TitleFor(20));
        Assert.Equal("Trial Legend", Trials.TitleFor(Trials.MaxWaves));   // the top band holds at the cap
    }

    [Fact]
    public void AffixRotatesWeekly_FromTheSeasonEpoch_AndIsDeterministic()
    {
        // Weeks ride the same fixed anchor the seasons use, so the clock alone fixes the affix.
        Assert.Equal(0, Trials.WeekNumber(Season.Epoch));
        Assert.Equal(1, Trials.WeekNumber(Season.Epoch.AddDays(7)));
        Assert.Equal(0, Trials.WeekNumber(Season.Epoch.AddDays(-30)));   // before the epoch → week 0

        // A fixed four-affix rotation: predictable, and independently derivable (no server discretion
        // over which week is the easy one).
        Assert.Equal(Trials.AffixForWeek(0), Trials.AffixForWeek(4));    // wraps after four weeks
        Assert.NotEqual(Trials.AffixForWeek(0), Trials.AffixForWeek(1));
        Assert.All(Enumerable.Range(0, 8).Select(w => Trials.AffixForWeek((long)w)),
            a => Assert.NotEqual(TrialsAffix.None, a));                  // None is the baseline, never rotates in
        Assert.Equal(Trials.AffixForWeek(3), Trials.AffixFor(Season.Epoch.AddDays(21)));
    }

    [Fact]
    public void AffixesReshapeTheLadder_ButNeverTheOpening()
    {
        // Waves 1-2 are IDENTICAL under every affix — the shared fair opening. Measured before this rule:
        // Veteran's +5 offset made wave 1 a level-6 ghost and zeroed ~80% of level-5 heroes, so one week in
        // four was dead for everyone below the training band — the opposite of a cold-start ladder's point.
        foreach (var affix in Enum.GetValues<TrialsAffix>())
        {
            Assert.Equal(1, Trials.GhostLevel(1, affix));
            Assert.Equal(2, Trials.GhostLevel(2, affix));
            Assert.Empty(Trials.GhostGear(1, affix));
            Assert.Empty(Trials.GhostGear(2, affix));
        }

        // From wave 3 on, each affix defines its own climb.
        Assert.Equal(10, Trials.GhostLevel(10));                              // plain: wave N → level N
        Assert.Equal(20, Trials.GhostLevel(10, TrialsAffix.Relentless));      // two levels a wave
        Assert.Equal(15, Trials.GhostLevel(10, TrialsAffix.Veteran));         // the ladder starts five in
        Assert.Empty(Trials.GhostGear(20, TrialsAffix.Featherweight));        // bare-handed at any depth
        Assert.NotEmpty(Trials.GhostGear(3, TrialsAffix.Ironclad));           // armed as soon as the affix bites
        Assert.Contains("arkforged-edge", Trials.GhostGear(4, TrialsAffix.Ironclad));
    }

    [Fact]
    public void AffixChangesTheRun_AndReplaysUnderTheSameAffix()
    {
        var hero = Runner(20);
        var entropy = CommitReveal.DeriveEntropy(SHA256.HashData([9]), "trial", "1");

        var plain = Trials.Resolve(hero, entropy);
        var brutal = Trials.Resolve(hero, entropy, affix: TrialsAffix.Relentless);

        // Relentless doubles the ghost's level each wave, so the same hero can't reach as deep.
        Assert.True(brutal.WavesCleared < plain.WavesCleared,
            $"Relentless should shorten the ladder (plain={plain.WavesCleared}, relentless={brutal.WavesCleared})");

        // Replaying under the SAME affix reproduces the run exactly — the property verification relies on.
        Assert.Equal(brutal.WavesCleared, Trials.Resolve(hero, entropy, affix: TrialsAffix.Relentless).WavesCleared);
    }

    [Fact]
    public void StrongerHeroSurvivesDeeper()
    {
        // Same genome, different level — on an absolute ladder the higher-level hero must reach deeper,
        // averaged over seeds (combat has per-fight variance). This is what makes the score a power read.
        var seeds = Enumerable.Range(1, 12).Select(i => SHA256.HashData([(byte)i])).ToArray();
        var weak = seeds.Sum(s => Trials.Resolve(Runner(3), CommitReveal.DeriveEntropy(s, "t", "1")).WavesCleared);
        var strong = seeds.Sum(s => Trials.Resolve(Runner(20), CommitReveal.DeriveEntropy(s, "t", "1")).WavesCleared);
        Assert.True(strong > weak, $"a level-20 hero should out-survive a level-3 one across seeds (weak={weak}, strong={strong})");
    }

    // ── The trials ghost is not graded to its runner — AN UNFIXED DEFECT ────────────────────────────
    // Gauntlet.GhostFor mints at the runner's own StatGeneCeiling; Trials.GhostFor mints from NewGen0 and
    // never takes the runner at all. The fix is DEFERRED pending a release decision — it moves the ghost
    // every stamped receipt is client-replayed against. So these pin BROKEN behaviour on purpose: the
    // first two go RED when it lands and should be rewritten in GauntletGradeTests' wording, the
    // growth-gene one does not move (it is the capped mint, not this ladder).

    private const int Cohort = 150;

    private static readonly int[] EntryLevels = [1, 3, 8];

    private static Hero At(Genome genome, int level) =>
        new() { Id = "hero", OwnerId = "p", Name = "H", Genome = genome, Level = level };

    private static Hero RecruitRunner(int i, int level) =>
        At(Genome.NewRecruit(SHA256.HashData(Encoding.UTF8.GetBytes($"trials-recruit-{i}")),
            StarterPolicy.RecruitStatCap), level);

    private static Hero BredRunner(int i, int level) =>
        At(Genome.NewGen0(SHA256.HashData(Encoding.UTF8.GetBytes($"trials-bred-{i}"))), level);

    private static byte[] RunEntropy(int level, int i) =>
        SHA256.HashData(Encoding.UTF8.GetBytes($"trials-run-{level}-{i}"));

    [Fact]
    public void TheEntryCohortIsWalledOutOfTheLadder_AnUnfixedDefect()
    {
        // Structure, not rates — percentages are content, and content is meant to be retuned.
        long recruitWaves = 0, bredWavesAtTheFloor = 0;
        int recruitTitles = 0, bredTitles = 0;

        foreach (var level in EntryLevels)
            for (var i = 0; i < Cohort; i++)
            {
                var entropy = RunEntropy(level, i);
                var recruit = Trials.Resolve(RecruitRunner(i, level), entropy).WavesCleared;
                var bred = Trials.Resolve(BredRunner(i, level), entropy).WavesCleared;

                recruitWaves += recruit;
                if (level == EntryLevels[0]) bredWavesAtTheFloor += bred;
                if (Trials.TitleFor(recruit) is not null) recruitTitles++;
                if (Trials.TitleFor(bred) is not null) bredTitles++;
            }

        Assert.True(recruitTitles == 0,
            $"{recruitTitles} recruit runs earned a title — the entry cohort can reach the mode's first " +
            "reward band again, so this defect pin is obsolete");
        Assert.True(bredTitles > 0,
            "no cohort earned a title at all — the harness is broken, not the ladder");

        Assert.True(recruitWaves < bredWavesAtTheFloor,
            $"recruits pooled over levels {string.Join("/", EntryLevels)} cleared {recruitWaves} waves against " +
            $"{bredWavesAtTheFloor} for bred heroes at level {EntryLevels[0]} alone");
    }

    [Fact]
    public void TheTrialsGhostIsMintedUngraded_WhileTheGauntletsIsGradedToItsRunner_AnUnfixedDefect()
    {
        var recruit = RecruitRunner(0, 1);
        var entropy = RunEntropy(1, 0);
        Assert.Equal(StarterPolicy.RecruitStatCap, recruit.Genome.StatGeneCeiling);

        for (var wave = 1; wave <= 5; wave++)
        {
            // The one-line difference, stated as the two mints themselves.
            Assert.Equal(
                Genome.NewGen0(CommitReveal.DeriveEntropy(entropy, "trials-wave", wave.ToString())).ToHex(),
                Trials.GhostFor(entropy, wave).Genome.ToHex());
            Assert.Equal(
                Genome.NewRecruit(CommitReveal.DeriveEntropy(entropy, "gauntlet-wave", wave.ToString()),
                    recruit.Genome.StatGeneCeiling).ToHex(),
                Gauntlet.GhostFor(entropy, wave, recruit).Genome.ToHex());

            Assert.True(Trials.GhostFor(entropy, wave).Genome.StatGeneCeiling > recruit.Genome.StatGeneCeiling,
                $"wave {wave}: the trials ghost no longer outgrades its recruit runner — grading has landed " +
                "here and this defect pin is obsolete");
            Assert.Equal(recruit.Genome.StatGeneCeiling,
                Gauntlet.GhostFor(entropy, wave, recruit).Genome.StatGeneCeiling);
        }
    }

    [Fact]
    public void ARecruitsPerLevelStatGainIsLockedToTheMinimumTier_AnUnfixedDefect()
    {
        var bredGainedMore = false;
        foreach (var level in new[] { 1, 20 })
            for (var i = 0; i < Cohort; i++)
            {
                Assert.Equal(1, AttackGain(RecruitRunner(i, level).Genome, level));
                if (AttackGain(BredRunner(i, level).Genome, level) > 1) bredGainedMore = true;
            }

        Assert.True(bredGainedMore,
            "no bred hero gained more than the minimum either — the growth term is inert for everyone");
    }

    /// <summary>Attack is strength with no equipment mods, so this is exactly the growth term.</summary>
    private static int AttackGain(Genome genome, int level) =>
        StatBlock.ComputeFor(genome, level + 1).Attack - StatBlock.ComputeFor(genome, level).Attack;
}
