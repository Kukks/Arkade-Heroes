using System.Security.Cryptography;
using System.Text;
using ArkadeHeroes.Core;
using ArkadeHeroes.Core.Combat;
using ArkadeHeroes.Core.Equipment;
using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Heroes;
using ArkadeHeroes.Core.Progression;

namespace ArkadeHeroes.Sim;

/// <summary>
/// What the dormant combat flags would actually do. <c>ElementAwareSelection</c>, <c>InnateAbilities</c>
/// and <c>SquadSynergy</c> all ship false in <c>CombatConfig.Default</c> so replays stay verifiable,
/// each waiting on a coordinated client+server release. This runs the SAME matchups under each flag
/// and reports the delta, so the flip is a decision with numbers behind it.
/// </summary>
public static class DormantFlags
{
    public static string Render(int samples, int seed)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"DORMANT COMBAT FLAGS — {samples} identical matchups replayed under each, seed {seed}");
        sb.AppendLine($"  {"config",-24} {"favourite wins",15} {"avg turns",10} {"upsets",8}");

        foreach (var (label, cfg) in Variants())
        {
            var r = Measure(samples, seed, cfg);
            sb.AppendLine($"  {label,-24} {r.FavouriteWinPct,14:F1}% {r.AvgTurns,10:F1} {r.UpsetPct,7:F1}%");
        }

        sb.AppendLine();
        sb.AppendLine("DOES A RARER HERO WIN MORE?");
        sb.AppendLine("  Cosmetic traits are combat-inert by default — rarity touches a fight only through the");
        sb.AppendLine("  capped affinity modifier. InnateAbilities is the flag that changes that.");
        sb.AppendLine($"  {"config",-24} {"win rate of the rarer hero",28}");
        foreach (var (label, cfg) in Variants())
            sb.AppendLine($"  {label,-24} {RarerWinPct(samples, seed, cfg),27:F1}%");
        sb.AppendLine();
        sb.AppendLine("  50% means rarity does not decide fights. Read it against what breeding costs.");
        return sb.ToString();
    }

    private static (string Label, GameConfig Config)[] Variants() =>
    [
        ("default (all off)", GameConfig.Default),
        ("ElementAwareSelection", With(c => c with { ElementAwareSelection = true })),
        ("InnateAbilities", With(c => c with { InnateAbilities = true })),
        ("both", With(c => c with { ElementAwareSelection = true, InnateAbilities = true })),
    ];

    private static GameConfig With(Func<CombatConfig, CombatConfig> f) =>
        GameConfig.Default with { Combat = f(GameConfig.Default.Combat) };

    private readonly record struct Measured(double FavouriteWinPct, double AvgTurns, double UpsetPct);

    /// Same seed per variant, so every variant sees the identical field and the identical fight seeds —
    /// the delta is the flag, not the sample.
    private static Measured Measure(int samples, int seed, GameConfig config)
    {
        var rng = new Random(seed);
        int fights = 0, favouriteWins = 0;
        long turns = 0;
        for (var i = 0; i < samples; i++)
        {
            var (a, b, entropy) = Matchup(rng);
            var pa = PowerScore.Compute(a, config);
            var pb = PowerScore.Compute(b, config);
            // Equal scores have no favourite; scoring one would be scoring a coin flip.
            if (pa == 0 || pb == 0 || pa == pb) continue;
            var result = BattleEngine.Fight(a, b, entropy, config);
            if (result.WinnerId == (pa > pb ? a.Id : b.Id)) favouriteWins++;
            turns += result.Turns;
            fights++;
        }
        var pct = 100.0 * favouriteWins / fights;
        return new Measured(pct, (double)turns / fights, 100 - pct);
    }

    private static double RarerWinPct(int samples, int seed, GameConfig config)
    {
        var rng = new Random(seed);
        int decided = 0, rarerWins = 0;
        for (var i = 0; i < samples; i++)
        {
            var (a, b, entropy) = Matchup(rng);
            var ra = Rarity.Of(a.Genome, config).Score;
            var rb = Rarity.Of(b.Genome, config).Score;
            if (ra == rb) continue;
            var result = BattleEngine.Fight(a, b, entropy, config);
            if (result.WinnerId == (ra > rb ? a.Id : b.Id)) rarerWins++;
            decided++;
        }
        return decided == 0 ? 50 : 100.0 * rarerWins / decided;
    }

    /// <summary>
    /// How many generations of breeding the sampled field has behind it. This is load-bearing, not a
    /// detail: InnateAbilities buys its proc chances with trait rarity, and a gen-1 hero is ~94%
    /// Common (see --rarity), so measuring the flag on a shallow field measures nothing. Six
    /// generations puts the field at roughly a fifth non-Common, which is a population that has
    /// actually been bred.
    /// </summary>
    private const int Generations = 6;

    private static (Hero A, Hero B, byte[] Entropy) Matchup(Random rng)
    {
        var level = 1 + rng.Next(12);
        return (Bred(rng, level, "A"), Bred(rng, level + rng.Next(3) - 1, "B"), Entropy(rng));
    }

    private static Hero Bred(Random rng, int level, string tag)
    {
        var genome = Genome.NewGen0(Entropy(rng));
        for (var g = 0; g < Generations; g++)
            genome = GeneMixer.Mix(genome, Genome.NewGen0(Entropy(rng)), Entropy(rng));
        var hero = new Hero
        {
            Id = $"{tag}-{Guid.NewGuid():N}",
            OwnerId = "sim",
            Name = tag,
            Genome = genome,
            Level = Math.Max(1, level),
        };
        if (rng.Next(2) == 0)
        {
            foreach (var slot in ItemCatalog.All.Select(i => i.Slot).Distinct())
            {
                var options = ItemCatalog.All.Where(i => i.Slot == slot && i.MinLevel <= hero.Level).ToList();
                if (options.Count > 0) hero.Equipment.Equip(options[rng.Next(options.Count)]);
            }
        }
        return hero;
    }

    private static byte[] Entropy(Random rng)
    {
        var b = new byte[32];
        rng.NextBytes(b);
        return SHA256.HashData(b);
    }
}
