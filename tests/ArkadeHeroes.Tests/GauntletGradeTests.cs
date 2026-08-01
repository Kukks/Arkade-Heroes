using System.Security.Cryptography;
using System.Text;
using ArkadeHeroes.Core;
using ArkadeHeroes.Core.Combat;
using ArkadeHeroes.Core.Equipment;
using ArkadeHeroes.Core.Fairness;
using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Heroes;
using ArkadeHeroes.Core.Progression;

namespace ArkadeHeroes.Tests;

/// <summary>
/// The gauntlet ghost is drawn at the runner's GRADE, not only at its level — and why level scaling alone
/// could never have carried the cohort that pays for the on-ramp.
///
/// <c>Genome.NewRecruit</c> squashes a bought hero's stat and growth genes into the bottom quarter of their
/// range (<c>StarterPolicy.RecruitStatCap</c>, 63 of 255). That cap is deliberate and stays: recruits are
/// repeatable, so their genome must not be a lottery ticket. But <c>Gauntlet.GhostFor</c> used to build
/// EVERY ghost from <c>Genome.NewGen0</c> — raw hash bytes, mean gene 127 — and scaled only the ghost's
/// LEVEL to the runner. A grade deficit is not something a level can pay back: it is multiplicative in the
/// stat curve (<c>StatBlock.StatValue</c> reads <c>10 + gene/4</c> and gains <c>1 + growthGene/64</c> per
/// level, so a capped growth gene is locked to the MINIMUM tier while its foe gains up to four times that),
/// which is why the paying cohort failed at every level rather than only at the first.
///
/// The ghost's grade now comes from the runner's own genome (<c>Genome.StatGeneCeiling</c>) — data the
/// client already holds in the signed hero snapshot it replays against, so no wire field, no stored flag
/// and no server discretion is added. Only the gauntlet ladder is graded; the inflation valve is untouched,
/// so a recruit is still the worst hero in the game everywhere its value is TRADEABLE.
///
/// These tests pin STRUCTURE, not balance — the same convention <see cref="GauntletRampTests"/> records.
/// Clear-rate percentages are content, and content is meant to be retuned; the one test here that reads
/// rates at all compares two cohorts against EACH OTHER, so retuning the ladder moves both and the
/// assertion survives. What is pinned is the shape: the entry cohort runs the same ladder as everybody
/// else, and nobody above the floor moves at all.
/// </summary>
public class GauntletGradeTests
{
    /// <summary>The genome bytes that decide a statline, and exactly the ones the capped mint squashes:
    /// the five visible stat genes and the five hidden growth genes.</summary>
    private static readonly int[] StatAndGrowthGenes = [0, 1, 2, 3, 4, 8, 9, 10, 11, 12];

    private static Hero At(Genome genome, int level, string id = "hero-1") =>
        new() { Id = id, OwnerId = "player-1", Name = "Runner", Genome = genome, Level = level };

    private static Hero Gen0Runner(int i, int level) =>
        At(Genome.NewGen0(SHA256.HashData(Encoding.UTF8.GetBytes($"grade-gen0-{i}"))), level);

    private static Hero RecruitRunner(int i, int level) =>
        At(Genome.NewRecruit(SHA256.HashData(Encoding.UTF8.GetBytes($"grade-recruit-{i}")),
            StarterPolicy.RecruitStatCap), level);

    private static byte[] RunEntropy(int level, int i) =>
        SHA256.HashData(Encoding.UTF8.GetBytes($"grade-run-{level}-{i}"));

    /// <summary>A genome that is all zeroes except one planted byte — so a ceiling can be attributed to the
    /// byte it was read from rather than to the genome as a whole.</summary>
    private static Genome WithOnly(int index, byte value)
    {
        var bytes = new byte[Genome.Size];
        bytes[index] = value;
        return new Genome(bytes);
    }

    // ── The grade itself ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheGradeIsTheTightestPowerOfTwoBoundOverTheStatAndGrowthGenes()
    {
        foreach (var i in StatAndGrowthGenes)
        {
            Assert.Equal(255, WithOnly(i, 255).StatGeneCeiling);
            Assert.Equal(255, WithOnly(i, 128).StatGeneCeiling);   // the first value the top rung must hold
            Assert.Equal(127, WithOnly(i, 127).StatGeneCeiling);
            Assert.Equal(63, WithOnly(i, 63).StatGeneCeiling);     // exactly the recruit cap
            Assert.Equal(63, WithOnly(i, 32).StatGeneCeiling);
            Assert.Equal(31, WithOnly(i, 31).StatGeneCeiling);
            Assert.Equal(1, WithOnly(i, 1).StatGeneCeiling);
        }

        // The grade is the MAXIMUM over those ten, so any one of them alone lifts it — pinned above — and a
        // genome with no statline at all grades at the bottom rather than throwing or wrapping.
        Assert.Equal(0, new Genome(new byte[Genome.Size]).StatGeneCeiling);

        // Bytes that say nothing about how hard a hero hits must not move it: element, both skill genes,
        // the cooldown gene, both appearance genes, and the whole trait block.
        foreach (var i in new[] { 5, 6, 7, 13, 14, 15, 16, 20, 24, 31 })
            Assert.Equal(0, WithOnly(i, 255).StatGeneCeiling);
    }

    [Fact]
    public void MintingAtTheTopRungIsExactlyAGen0Mint()
    {
        // The identity every "nobody above the floor moved" claim in this file rests on. The capped mint
        // reduces each gene modulo cap+1, so at a cap of 255 the modulo is the identity — which is what
        // lets a full-grade runner keep facing byte-for-byte the ghost the ladder always built. Stated
        // here rather than trusted, because it is a property of an implementation detail of NewRecruit.
        for (var i = 0; i < 64; i++)
        {
            var e = SHA256.HashData(Encoding.UTF8.GetBytes($"grade-identity-{i}"));
            Assert.Equal(Genome.NewGen0(e).ToHex(), Genome.NewRecruit(e, 255).ToHex());
        }
    }

    [Fact]
    public void OneFullRangeGeneAmongCappedOnesGradesTheWholeGenomeUp_TheKnownSharpEdge()
    {
        // The estimator is the MAXIMUM, which makes it deliberately one-sided: it never softens a ghost
        // because ten draws happened to come in low, and the price is that a single high gene lifts the
        // whole grade. That edge is reachable — GeneMixer rerolls a stat region from raw hash bytes ~3% of
        // the time, so a child of two recruits can carry one full-range gene among nine capped ones, and is
        // then graded against a full-range ghost on the strength of one stat.
        //
        // Measured at N=8000 recruit-bred children, level 1, by the grade they inherited:
        //   ceiling  63 — 79.0% of the cohort — clears nothing 39.1% of the time (matched; fixed)
        //   ceiling 127 —  6.6%                                69.0%
        //   ceiling 255 — 14.4%                                88.9%  (88.9%, not 95.3%: it IS a better hero)
        //
        // Pinned so the edge is a documented property rather than a surprise. Softening it means grading
        // DOWN on sampling noise — taking the second-largest gene instead would misgrade about 1% of EVERY
        // cohort, handing a softer ladder to heroes that never needed one and re-baselining the replay
        // vectors to do it. That trade is refused here on purpose; changing this is a deliberate act.
        // This first line is also a deliberate tripwire on the recruit cap itself. The grade lands on a rung
        // of the form 2^k−1, and 63 IS one, so a recruit is graded exactly. Retune the cap to something off
        // a rung — 60, say — and this goes red, which is the point: the ghost would then be drawn a little
        // above the runner rather than level with it. That is a small effect, not a return of the bug, but
        // whoever moves the cap should see it rather than discover it in a clear-rate months later.
        var genes = new byte[Genome.Size];
        foreach (var i in StatAndGrowthGenes) genes[i] = StarterPolicy.RecruitStatCap;
        Assert.Equal(StarterPolicy.RecruitStatCap, new Genome(genes).StatGeneCeiling);

        foreach (var i in StatAndGrowthGenes)
        {
            var mutated = (byte[])genes.Clone();
            mutated[i] = 200;                                             // one region rerolled from raw bytes
            Assert.Equal(255, new Genome(mutated).StatGeneCeiling);
        }
    }

    [Fact]
    public void AFullRangeStatlineIsTheNorm_WhichIsWhyGradingChangesSoLittle()
    {
        // How much of the population the top rung covers, since the blast-radius claims depend on it: ten
        // genes drawn from the full byte, so a genome NONE of which reaches 128 is about one in a thousand.
        // Loose on purpose — this is a statement that the top rung is the normal case, not a pinned rate.
        var fullGrade = 0;
        for (var i = 0; i < 500; i++)
            if (Gen0Runner(i, 1).Genome.StatGeneCeiling == 255) fullGrade++;

        Assert.True(fullGrade >= 480, $"only {fullGrade}/500 gen-0 genomes reach the top rung");
    }

    // ── What the ladder does with it ────────────────────────────────────────────────────────────────

    [Fact]
    public void TheGhostIsDrawnAtTheRunnersGrade_NotAlwaysAtTheFullByteRange()
    {
        var cappedRunnerGhosts = 0;
        var fullRunnerGenesOverTheRecruitCap = 0;

        for (var i = 0; i < 40; i++)
        {
            var entropy = RunEntropy(1, i);
            for (var wave = 1; wave <= Gauntlet.WaveCount; wave++)
            {
                var recruitGhost = Gauntlet.GhostFor(entropy, wave, RecruitRunner(i, 1)).Genome;
                foreach (var g in StatAndGrowthGenes)
                    Assert.True(recruitGhost[g] <= StarterPolicy.RecruitStatCap,
                        $"a recruit's wave-{wave} ghost carried gene {g} = {recruitGhost[g]}, above the runner's grade");
                cappedRunnerGhosts++;

                // …and the other half of the claim: the grade is not a blanket cap. A full-range runner's
                // ghost must still routinely break the recruit ceiling, or grading did nothing but nerf
                // every ghost in the game.
                var fullGhost = Gauntlet.GhostFor(entropy, wave, Gen0Runner(i, 1)).Genome;
                foreach (var g in StatAndGrowthGenes)
                    if (fullGhost[g] > StarterPolicy.RecruitStatCap) fullRunnerGenesOverTheRecruitCap++;
            }
        }

        Assert.Equal(40 * Gauntlet.WaveCount, cappedRunnerGhosts);
        Assert.True(fullRunnerGenesOverTheRecruitCap > 0,
            "a full-range runner's ghosts never exceeded the recruit cap — the ladder got easier for everyone");
    }

    [Fact]
    public void AFullGradeRunnerFacesByteForByteTheGhostTheLadderAlwaysBuilt()
    {
        // The no-regression statement at its sharpest: not "a similar ghost" but the SAME genome, so an
        // older receipt from a full-grade hero still re-verifies and the golden replay vectors do not move.
        var checkedRunners = 0;
        for (var i = 0; i < 60; i++)
        {
            var runner = Gen0Runner(i, 5);
            if (runner.Genome.StatGeneCeiling != 255) continue;   // the ~1-in-1000 exception, covered above
            checkedRunners++;

            var entropy = RunEntropy(5, i);
            for (var wave = 1; wave <= Gauntlet.WaveCount; wave++)
            {
                var legacy = Genome.NewGen0(CommitReveal.DeriveEntropy(entropy, "gauntlet-wave", wave.ToString()));
                Assert.Equal(legacy.ToHex(), Gauntlet.GhostFor(entropy, wave, runner).Genome.ToHex());
            }
        }

        Assert.True(checkedRunners >= 55, $"only {checkedRunners}/60 runners were full-grade to compare");
    }

    [Fact]
    public void NoCohortAboveTheFloorGotAnEasierLadder_AndTheEntryCohortDid()
    {
        // Whole RUNS, not just ghost genomes: a full-grade runner resolves exactly as it did against the
        // old always-gen-0 ladder, and — the non-vacuity half — a recruit does not, or nothing changed.
        var unchanged = 0;
        var recruitRunsThatMoved = 0;

        foreach (var level in new[] { 1, 3, 8 })
            for (var i = 0; i < 150; i++)
            {
                var entropy = RunEntropy(level, i);

                var gen0 = Gen0Runner(i, level);
                if (gen0.Genome.StatGeneCeiling == 255)
                {
                    Assert.Equal(ClearedAgainstFullGradeGhosts(gen0, entropy),
                        Gauntlet.Resolve(gen0, entropy).WavesCleared);
                    unchanged++;
                }

                var recruit = RecruitRunner(i, level);
                if (Gauntlet.Resolve(recruit, entropy).WavesCleared !=
                    ClearedAgainstFullGradeGhosts(recruit, entropy)) recruitRunsThatMoved++;
            }

        Assert.True(unchanged >= 400, $"only {unchanged} full-grade runs were available to compare");
        Assert.True(recruitRunsThatMoved > 0,
            "grading changed nothing for the cohort it exists for — the harness or the fix is inert");
    }

    /// <summary>Gauntlet.Resolve's loop with the ghost forced back to full grade — the ladder as it stood
    /// before grading. Everything else (level, gear, handicap, fight seed) is left exactly as the resolver
    /// builds it, so the only difference between this and the real run is the ghost's genome.</summary>
    private static int ClearedAgainstFullGradeGhosts(Hero hero, byte[] entropy)
    {
        var cleared = 0;
        for (var wave = 1; wave <= Gauntlet.WaveCount; wave++)
        {
            var ghost = new Hero
            {
                Id = $"ghost-{wave}",
                OwnerId = "gauntlet",
                Name = $"Gauntlet Wave {wave}",
                Genome = Genome.NewGen0(CommitReveal.DeriveEntropy(entropy, "gauntlet-wave", wave.ToString())),
                Level = Gauntlet.GhostLevel(hero.Level, wave),
            };
            foreach (var itemId in Gauntlet.GhostGear(wave))
                ghost.Equipment.Equip(ItemCatalog.Find(itemId)!);

            var seed = CommitReveal.DeriveEntropy(entropy, "gauntlet-fight", wave.ToString());
            var result = BattleEngine.Fight(hero, ghost, seed, GameConfig.Default,
                advantageB: Gauntlet.GhostHandicap(hero.Level, wave));
            if (result.WinnerId != hero.Id) break;
            cleared++;
        }
        return cleared;
    }

    [Fact]
    public void TheEntryCohortNowRunsTheSameLadderAsEveryoneElse()
    {
        // The only test here that reads rates, and it reads them RELATIVE to each other rather than against
        // a pinned number: a content retune moves both cohorts together, so it survives one, while the
        // defect this fixes — recruits clearing nothing 96-99% of the time against gen-0's ~44% — is a gap
        // of fifty-odd points and cannot survive it. The band is deliberately far wider than the difference
        // the two cohorts should show, because a recruit is still a WORSE hero; it is just no longer on a
        // different ladder.
        foreach (var level in new[] { 1, 3, 8 })
        {
            var recruit = ClearedNothingRate(RecruitRunner, level);
            var gen0 = ClearedNothingRate(Gen0Runner, level);
            Assert.True(Math.Abs(recruit - gen0) <= 15,
                $"level {level}: recruits clear nothing {recruit:F1}% of the time against gen-0's {gen0:F1}% — " +
                "the entry cohort is running a different ladder");
        }
    }

    private static double ClearedNothingRate(Func<int, int, Hero> population, int level)
    {
        const int n = 400;
        var zero = 0;
        for (var i = 0; i < n; i++)
            if (Gauntlet.Resolve(population(i, level), RunEntropy(level, i)).WavesCleared == 0) zero++;
        return zero * 100.0 / n;
    }

    // ── What the grade is allowed to depend on ──────────────────────────────────────────────────────

    [Fact]
    public void TheGhostIsPureInTheRunnersGenomeAndLevel_AndInNothingTheServerPicks()
    {
        // Client-derivability, stated as a purity claim. The verifier rebuilds the runner from the signed
        // snapshot; if the grade keyed on anything else — an id, an owner, equipment, a stored flag — the
        // browser could not reproduce it and an honest run would render "SERVER CHEATED".
        var genome = RecruitRunner(7, 4).Genome;
        var entropy = RunEntropy(4, 7);

        var plain = At(genome, 4, "hero-1");
        var disguised = At(genome, 4, "some-other-id");
        disguised.OwnerId = "someone-else";
        disguised.Name = "Different";
        disguised.Equipment.Equip(ItemCatalog.Find("arkforged-edge")!);

        for (var wave = 1; wave <= Gauntlet.WaveCount; wave++)
            Assert.Equal(Gauntlet.GhostFor(entropy, wave, plain).Genome.ToHex(),
                Gauntlet.GhostFor(entropy, wave, disguised).Genome.ToHex());

        // And it does depend on the genome — a differently graded runner draws a different ghost, or the
        // equality above would be trivially true of everything.
        var fullGrade = At(Gen0Runner(7, 4).Genome, 4);
        Assert.Equal(255, fullGrade.Genome.StatGeneCeiling);
        Assert.NotEqual(Gauntlet.GhostFor(entropy, 1, plain).Genome.ToHex(),
            Gauntlet.GhostFor(entropy, 1, fullGrade).Genome.ToHex());
    }

    [Fact]
    public void GradingComposesWithTheOpenersHandicapRatherThanReplacingIt()
    {
        // The two fixes are on different axes and must both still reach the entry cohort: the ghost's LEVEL
        // is still floored at 1 and still paid back on the damage axis (PR #219), and its GRADE is now the
        // runner's. A recruit at level 1 is the hero that needs both.
        var recruit = RecruitRunner(3, 1);
        Assert.Equal(1, Gauntlet.GhostLevel(recruit.Level, 1));
        Assert.True(Gauntlet.GhostHandicap(recruit.Level, 1) < 1.0);
        Assert.Equal(1.0, Gauntlet.GhostHandicap(recruit.Level, 2));

        var ghost = Gauntlet.GhostFor(RunEntropy(1, 3), 1, recruit);
        Assert.Equal(1, ghost.Level);
        foreach (var g in StatAndGrowthGenes)
            Assert.True(ghost.Genome[g] <= StarterPolicy.RecruitStatCap);
    }

    [Fact]
    public void ARunnerWithNoStatlineAtAllStillResolvesALegalRun()
    {
        // The bottom rung is reachable in principle (a genome whose ten stat genes are all zero grades 0),
        // and a cap of 0 makes the capped mint emit zeroes. StatBlock refuses a level below 1 but not a
        // gene of 0, so this must resolve rather than throw — and the ghost is graded down with the runner.
        var runner = At(new Genome(new byte[Genome.Size]), 1);
        var entropy = RunEntropy(1, 99);

        var ghost = Gauntlet.GhostFor(entropy, 1, runner);
        foreach (var g in StatAndGrowthGenes) Assert.Equal(0, ghost.Genome[g]);

        var run = Gauntlet.Resolve(runner, entropy);
        Assert.NotEmpty(run.Waves);
        Assert.InRange(run.WavesCleared, 0, Gauntlet.WaveCount);
    }
}
