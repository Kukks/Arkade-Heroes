using System.Security.Cryptography;
using System.Text;
using ArkadeHeroes.Core.Combat;
using ArkadeHeroes.Core.Equipment;
using ArkadeHeroes.Core.Fairness;
using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Heroes;
using ArkadeHeroes.Core.Progression;

namespace ArkadeHeroes.Sim;

/// <summary>
/// How deep the endless Trials ladder actually lets a hero go. The playthrough harness recorded runs
/// clearing ZERO waves almost every time, against ~40% for the gauntlet, so this measures the ladder
/// directly from <c>Trials.Resolve</c> across levels, genome grades, gear and all five affixes — plus
/// the two counterfactuals that separate the candidate causes: the same heroes down the GAUNTLET, and
/// the same Trials ladder with its ghosts graded to the runner the way <c>Gauntlet.GhostFor</c> grades
/// its own.
/// </summary>
public static class TrialsYield
{
    private static readonly int[] Levels = [1, 2, 3, 5, 8, 12, 20, 30, 50];
    private static readonly int[] AffixLevels = [1, 5, 12, 30];
    private static readonly int[] GauntletLevels = [1, 3, 5, 8, 12];

    private readonly record struct Grade(string Name, bool Recruit, bool Geared);

    private static readonly Grade[] Grades =
    [
        new("recruit", true, false),
        new("recruit+gear", true, true),
        new("bred", false, false),
        new("bred+gear", false, true),
    ];

    private readonly record struct Cell(int Runs, int Zero, long Waves, int ReachedAffix, int Titled, int Best)
    {
        public double ZeroPct => 100.0 * Zero / Math.Max(1, Runs);
        public double ReachedPct => 100.0 * ReachedAffix / Math.Max(1, Runs);
        public double AvgWaves => (double)Waves / Math.Max(1, Runs);
        public double TitledPct => 100.0 * Titled / Math.Max(1, Runs);
    }

    /// The first wave an affix can change anything, derived from the resolver rather than copied — the
    /// constant behind it is private, and a run that never gets this deep never meets the week's rule.
    private static readonly int AffixFromWave = Enumerable.Range(1, Trials.MaxWaves).First(w =>
        Enum.GetValues<TrialsAffix>().Any(a =>
            Trials.GhostLevel(w, a) != Trials.GhostLevel(w) ||
            Trials.GhostGear(w, a).Count != Trials.GhostGear(w).Count));

    public static string Render(int samples, int seed)
    {
        var now = DateTimeOffset.UtcNow;
        var live = Trials.AffixFor(now);

        var sb = new StringBuilder();
        sb.AppendLine($"TRIALS YIELD — {samples} seeded runs per cell, ladder capped at {Trials.MaxWaves} waves, seed {seed}");
        sb.AppendLine($"  affix in force now (week {Trials.WeekNumber(now)}): {Trials.AffixDescription(live)}");
        sb.AppendLine($"  affixes only bite from wave {AffixFromWave} — every week opens on the same level-1 naked ghost.");
        sb.AppendLine();

        var reached = $"fought w{AffixFromWave}+";
        sb.AppendLine("BY LEVEL AND GRADE (plain ladder, no affix)");
        sb.AppendLine($"  {"grade",-13} {"level",5} {"0 waves",8} {reached,12} {"avg waves",10} {"titled",7} {"best seen",10}");
        foreach (var grade in Grades)
        {
            foreach (var level in Levels)
            {
                var c = Measure(grade, level, TrialsAffix.None, samples, seed);
                sb.AppendLine($"  {grade.Name,-13} {level,5} {c.ZeroPct,7:F1}% {c.ReachedPct,11:F1}% " +
                              $"{c.AvgWaves,10:F2} {c.TitledPct,6:F1}% {c.Best,10}" +
                              (c.Best > 0 ? $"  ({Trials.TitleFor(c.Best) ?? "no title"})" : ""));
            }
        }

        sb.AppendLine();
        sb.AppendLine($"BY AFFIX (levels {string.Join("/", AffixLevels)} pooled, {samples * AffixLevels.Length} runs per cell)");
        sb.AppendLine($"  {"affix",-15} {"grade",-13} {"0 waves",8} {reached,12} {"avg waves",10}");
        foreach (var affix in Enum.GetValues<TrialsAffix>())
        {
            foreach (var grade in Grades)
            {
                var c = Pool(AffixLevels.Select(l => Measure(grade, l, affix, samples, seed)));
                sb.AppendLine($"  {affix,-15} {grade.Name,-13} {c.ZeroPct,7:F1}% {c.ReachedPct,11:F1}% {c.AvgWaves,10:F2}");
            }
        }

        sb.Append(AgainstTheGauntlet(samples, seed));
        sb.Append(GradedGhosts(samples, seed));
        sb.Append(Doorstep(samples, seed));

        sb.AppendLine();
        sb.AppendLine("  A zero-wave run is a loss to wave 1: a LEVEL-1 ghost with no gear. It is the same ghost");
        sb.AppendLine("  under every affix, so the weekly rule is not what zeroes the ladder — the grade of the");
        sb.AppendLine("  ghost's GENOME is. Trials.GhostFor mints from Genome.NewGen0 (the full byte range),");
        sb.AppendLine("  while Gauntlet.GhostFor mints from Genome.NewRecruit at the RUNNER's own StatGeneCeiling.");
        sb.AppendLine("  A recruit's stat and growth genes are capped at 63, so its growth term (1 + gene/64) is");
        sb.AppendLine("  locked to the minimum 1/level against a gen-0 ghost's mean 2.5 — the same multiplicative");
        sb.AppendLine("  deficit the gauntlet already fixed, still in force here.");
        sb.AppendLine();
        sb.AppendLine("  Trials is FREE to open and awards no XP, item or sats — only a score, a personal best and");
        sb.AppendLine($"  a title from {TitleFloor} waves up. So a zero-wave run costs no fee and banks nothing: no");
        sb.AppendLine("  title, a personal best of 0, and a bottom-of-board row (TrialsBoardBuilder admits a 0).");
        return sb.ToString();
    }

    /// The lowest score <see cref="Trials.TitleFor"/> pays anything for — read off the resolver, not copied.
    private static int TitleFloor =>
        Enumerable.Range(0, Trials.MaxWaves + 1).First(w => Trials.TitleFor(w) is not null);

    /// The same heroes and seeds down the gauntlet, so the two ladders are compared on one sample rather
    /// than across separate harness runs.
    private static string AgainstTheGauntlet(int samples, int seed)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("AGAINST THE GAUNTLET (identical heroes and entropy, plain ladder)");
        sb.AppendLine($"  {"grade",-13} {"level",5} {"trials 0-wave",14} {"gauntlet 0-wave",16} {"trials avg",11} {"gauntlet avg",13}");
        foreach (var grade in Grades)
        {
            foreach (var level in GauntletLevels)
            {
                var rng = new Random(seed + level);
                int trialsZero = 0, gauntletZero = 0;
                long trialsWaves = 0, gauntletWaves = 0;
                for (var i = 0; i < samples; i++)
                {
                    var hero = HeroAt(grade, level, rng);
                    var entropy = Entropy(rng);
                    var t = Trials.Resolve(hero, entropy).WavesCleared;
                    var g = Gauntlet.Resolve(hero, entropy).WavesCleared;
                    if (t == 0) trialsZero++;
                    if (g == 0) gauntletZero++;
                    trialsWaves += t;
                    gauntletWaves += g;
                }
                sb.AppendLine($"  {grade.Name,-13} {level,5} {100.0 * trialsZero / samples,13:F1}% " +
                              $"{100.0 * gauntletZero / samples,15:F1}% {(double)trialsWaves / samples,11:F2} " +
                              $"{(double)gauntletWaves / samples,13:F2}");
            }
        }
        return sb.ToString();
    }

    /// The counterfactual: the SHIPPED ladder — same levels, same gear bands, same affix — with the one
    /// line changed that the gauntlet already changed, so the ghost's statline is minted at the runner's
    /// own ceiling instead of the full byte range.
    private static string GradedGhosts(int samples, int seed)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("COUNTERFACTUAL — ghosts graded to the runner, as Gauntlet.GhostFor grades its own");
        sb.AppendLine($"  {"grade",-13} {"level",5} {"0 waves (shipped)",18} {"0 waves (graded)",17} {"avg (shipped)",14} {"avg (graded)",13}");
        foreach (var grade in Grades)
        {
            foreach (var level in GauntletLevels)
            {
                var rng = new Random(seed + level);
                int shippedZero = 0, gradedZero = 0;
                long shippedWaves = 0, gradedWaves = 0;
                for (var i = 0; i < samples; i++)
                {
                    var hero = HeroAt(grade, level, rng);
                    var entropy = Entropy(rng);
                    var shipped = Trials.Resolve(hero, entropy).WavesCleared;
                    var graded = GradedLadder(hero, entropy);
                    if (shipped == 0) shippedZero++;
                    if (graded == 0) gradedZero++;
                    shippedWaves += shipped;
                    gradedWaves += graded;
                }
                sb.AppendLine($"  {grade.Name,-13} {level,5} {100.0 * shippedZero / samples,17:F1}% " +
                              $"{100.0 * gradedZero / samples,16:F1}% {(double)shippedWaves / samples,14:F2} " +
                              $"{(double)gradedWaves / samples,13:F2}");
            }
        }
        return sb.ToString();
    }

    /// What wave 1 actually asks for: the mean statline of the level-1 gen-0 ghost every run opens against,
    /// and the level a recruit has to reach before its own statline gets there.
    private static string Doorstep(int samples, int seed)
    {
        var rng = new Random(seed);
        var ghost = MeanStats(samples, false, 1, rng);

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine($"THE DOORSTEP — mean unequipped statline over {samples} genomes");
        sb.AppendLine($"  {"who",-26} {"maxhp",7} {"attack",7} {"defense",8} {"speed",7}");
        sb.AppendLine($"  {"wave-1 ghost (gen0, L1)",-26} {ghost.Hp,7:F1} {ghost.Atk,7:F1} {ghost.Def,8:F1} {ghost.Spd,7:F1}");
        foreach (var level in Levels)
        {
            var r = MeanStats(samples, true, level, rng);
            sb.AppendLine($"  {$"recruit L{level}",-26} {r.Hp,7:F1} {r.Atk,7:F1} {r.Def,8:F1} {r.Spd,7:F1}");
        }

        var crossing = Enumerable.Range(1, Leveling.MaxLevel)
            .Cast<int?>()
            .FirstOrDefault(l => MeanStats(256, true, l!.Value, rng).Atk >= ghost.Atk);
        sb.AppendLine($"  A recruit's mean attack first reaches the wave-1 ghost's at level " +
                      (crossing is { } c ? $"{c}." : $"— never below {Leveling.MaxLevel}."));
        return sb.ToString();
    }

    private static Cell Measure(Grade grade, int level, TrialsAffix affix, int samples, int seed)
    {
        var rng = new Random(seed + level);
        int zero = 0, reached = 0, titled = 0, best = 0;
        long waves = 0;
        for (var i = 0; i < samples; i++)
        {
            var run = Trials.Resolve(HeroAt(grade, level, rng), Entropy(rng), affix: affix);
            if (run.WavesCleared == 0) zero++;
            if (run.WavesCleared >= AffixFromWave - 1) reached++;
            if (Trials.TitleFor(run.WavesCleared) is not null) titled++;
            best = Math.Max(best, run.WavesCleared);
            waves += run.WavesCleared;
        }
        return new Cell(samples, zero, waves, reached, titled, best);
    }

    private static Cell Pool(IEnumerable<Cell> cells)
    {
        var acc = new Cell(0, 0, 0, 0, 0, 0);
        foreach (var c in cells)
            acc = new Cell(acc.Runs + c.Runs, acc.Zero + c.Zero, acc.Waves + c.Waves,
                acc.ReachedAffix + c.ReachedAffix, acc.Titled + c.Titled, Math.Max(acc.Best, c.Best));
        return acc;
    }

    /// Mirrors <see cref="Trials.Resolve"/> exactly except for the ghost's genome mint.
    private static int GradedLadder(Hero hero, byte[] entropy, TrialsAffix affix = TrialsAffix.None)
    {
        var cleared = 0;
        for (var wave = 1; wave <= Trials.MaxWaves; wave++)
        {
            var ghost = new Hero
            {
                Id = $"trial-ghost-{wave}",
                OwnerId = "trials",
                Name = $"Trial Wave {wave}",
                Genome = Genome.NewRecruit(
                    CommitReveal.DeriveEntropy(entropy, "trials-wave", wave.ToString()),
                    hero.Genome.StatGeneCeiling),
                Level = Trials.GhostLevel(wave, affix),
            };
            foreach (var itemId in Trials.GhostGear(wave, affix))
                ghost.Equipment.Equip(ItemCatalog.Find(itemId)!);

            var fightSeed = CommitReveal.DeriveEntropy(entropy, "trials-fight", wave.ToString());
            if (BattleEngine.Fight(hero, ghost, fightSeed).WinnerId != hero.Id) break;
            cleared++;
        }
        return cleared;
    }

    private static (double Hp, double Atk, double Def, double Spd) MeanStats(
        int samples, bool recruit, int level, Random rng)
    {
        double hp = 0, atk = 0, def = 0, spd = 0;
        for (var i = 0; i < samples; i++)
        {
            var e = Entropy(rng);
            var genome = recruit ? Genome.NewRecruit(e, StarterPolicy.RecruitStatCap) : Genome.NewGen0(e);
            var s = StatBlock.ComputeFor(genome, level);
            hp += s.MaxHp;
            atk += s.Attack;
            def += s.Defense;
            spd += s.Speed;
        }
        return (hp / samples, atk / samples, def / samples, spd / samples);
    }

    private static Hero HeroAt(Grade grade, int level, Random rng)
    {
        var entropy = Entropy(rng);
        var hero = new Hero
        {
            Id = Guid.NewGuid().ToString("N"),
            OwnerId = "sim",
            Name = "Probe",
            Genome = grade.Recruit
                ? Genome.NewRecruit(entropy, StarterPolicy.RecruitStatCap)
                : Genome.NewGen0(entropy),
            Level = level,
        };
        if (grade.Geared) EquipBest(hero);
        return hero;
    }

    /// The best item per slot the hero's level allows — what a player with sats would be wearing.
    private static void EquipBest(Hero hero)
    {
        foreach (var slot in ItemCatalog.All.Select(i => i.Slot).Distinct())
        {
            var best = ItemCatalog.All
                .Where(i => i.Slot == slot && i.MinLevel <= hero.Level)
                .MaxBy(i => i.PriceSats);
            if (best is not null) hero.Equipment.Equip(best);
        }
    }

    private static byte[] Entropy(Random rng)
    {
        var b = new byte[32];
        rng.NextBytes(b);
        return SHA256.HashData(b);
    }
}
