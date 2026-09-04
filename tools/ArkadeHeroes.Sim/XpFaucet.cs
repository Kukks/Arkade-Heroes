using System.Security.Cryptography;
using System.Text;
using ArkadeHeroes.Core.Equipment;
using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Heroes;
using ArkadeHeroes.Core.Progression;

namespace ArkadeHeroes.Sim;

/// <summary>
/// What the game's only XP mint actually pays. Every hero's first XP comes from a gauntlet run —
/// staked PvP moves XP between heroes but never creates it, so a hero with nothing has nothing to
/// win and nothing to lose. This measures the mint directly from <c>Gauntlet.Resolve</c>.
/// </summary>
public static class XpFaucet
{
    public static string Render(int samples, int seed)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"GAUNTLET YIELD — {samples} seeded runs per level/grade, {Gauntlet.WaveCount} waves, seed {seed}");
        sb.AppendLine($"  {"grade",-8} {"level",5} {"0 waves",8} {"full clear",11} {"avg waves",10} {"avg xp",8}   {"wave reached",12}");

        foreach (var (grade, cap, geared) in new[]
                 {
                     ("recruit", StarterPolicy.RecruitStatCap, false),
                     ("bred", (byte)255, false),
                     ("bred+gear", (byte)255, true),
                 })
        {
            foreach (var level in new[] { 1, 2, 3, 5, 8, 10, 12 })
            {
                var rng = new Random(seed + level);
                var zero = 0;
                var full = 0;
                var waves = 0L;
                var xp = 0L;
                var best = 0;
                for (var i = 0; i < samples; i++)
                {
                    var hero = HeroAt(level, cap, rng);
                    if (geared) EquipBest(hero);
                    var run = Gauntlet.Resolve(hero, Entropy(rng));
                    if (run.WavesCleared == 0) zero++;
                    if (run.WavesCleared == Gauntlet.WaveCount) full++;
                    best = Math.Max(best, run.WavesCleared);
                    waves += run.WavesCleared;
                    xp += Gauntlet.XpForRun(level, run.WavesCleared);
                }
                sb.AppendLine(
                    $"  {grade,-9} {level,5} {100.0 * zero / samples,7:F1}% {100.0 * full / samples,10:F1}% " +
                    $"{(double)waves / samples,10:F2} {(double)xp / samples,8:F1}   best seen: {best,-4}" +
                    (level >= Gauntlet.PveXpLevelCap ? " (xp capped)" : ""));
            }
        }

        sb.AppendLine();
        sb.AppendLine($"  PvE XP is capped at level {Gauntlet.PveXpLevelCap}; above it the mint pays nothing and");
        sb.AppendLine("  XP only moves between heroes. A hero holding zero XP therefore cannot lose any,");
        sb.AppendLine("  so a staked duel between two such heroes transfers nothing and ranks nothing.");
        return sb.ToString();
    }

    private static Hero HeroAt(int level, byte statCap, Random rng) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        OwnerId = "sim",
        Name = "Probe",
        Genome = Genome.NewRecruit(Entropy(rng), statCap),
        Level = level,
    };

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
