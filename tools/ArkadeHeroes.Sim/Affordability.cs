using System.Security.Cryptography;
using System.Text;
using ArkadeHeroes.Core;
using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Heroes;
using ArkadeHeroes.Core.Progression;

namespace ArkadeHeroes.Sim;

/// <summary>
/// Whether a new player can afford to climb, and what would have to change for them to. Analysis
/// only — it re-prices the existing curve under candidate settings, it does not change anything.
/// </summary>
public static class Affordability
{
    public static string Render(int samples, int seed, long budget)
    {
        var sb = new StringBuilder();
        var yield = MeasuredYield(samples, seed);

        sb.AppendLine($"AFFORDABILITY — reaching the level-{Gauntlet.PveXpLevelCap} XP cap on {budget:N0} sats");
        sb.AppendLine($"  measured mint yield: {yield:F1} xp per gauntlet run (ungeared recruit, {samples} runs/level)");
        sb.AppendLine();

        var (runs, sats) = Cost(GameConfig.Default, yield);
        sb.AppendLine($"  as shipped:            {runs,5} runs   {sats,9:N0} sats   {Verdict(sats, budget)}");
        sb.AppendLine();

        sb.AppendLine("  If the curve moved (XpCurve = Base + Coefficient x level^Exponent, today 80/45/1.35):");
        foreach (var (label, curve) in new (string, XpCurve)[]
                 {
                     ("coefficient 45 -> 30", new XpCurve(80, 30, 1.35, 50)),
                     ("coefficient 45 -> 20", new XpCurve(80, 20, 1.35, 50)),
                     ("exponent 1.35 -> 1.15", new XpCurve(80, 45, 1.15, 50)),
                     ("base 80 -> 40, coeff 20", new XpCurve(40, 20, 1.35, 50)),
                 })
        {
            var (r, s) = Cost(GameConfig.Default with { Curve = curve }, yield);
            sb.AppendLine($"  {label,-24} {r,5} runs   {s,9:N0} sats   {Verdict(s, budget)}");
        }

        sb.AppendLine();
        sb.AppendLine("  Or if the mint paid more, leaving the curve alone:");
        foreach (var multiple in new[] { 1.5, 2.0, 3.0, 4.0 })
        {
            var (r, s) = Cost(GameConfig.Default, yield * multiple);
            sb.AppendLine($"  {$"{multiple:F1}x xp per run",-24} {r,5} runs   {s,9:N0} sats   {Verdict(s, budget)}");
        }

        sb.AppendLine();
        sb.AppendLine("  Or if a run cost less (entry = MatchFee(level) + dungeon bonus, today 500+20L+250):");
        foreach (var bonus in new long[] { 100, 0 })
        {
            var (r, s) = Cost(GameConfig.Default, yield, feeBonus: bonus);
            sb.AppendLine($"  {$"dungeon bonus 250 -> {bonus}",-24} {r,5} runs   {s,9:N0} sats   {Verdict(s, budget)}");
        }

        sb.AppendLine();
        sb.AppendLine("  Numbers hold the cooldown and the ~40% zero-wave rate constant; both are already");
        sb.AppendLine("  inside the measured per-run yield. Nothing here is a recommendation — each row");
        sb.AppendLine("  changes who earns sats, which is a decision the measurement cannot make.");
        return sb.ToString();
    }

    private static string Verdict(long sats, long budget) =>
        sats <= budget ? "affordable" : $"SHORT by {sats - budget:N0}";

    private static (int Runs, long Sats) Cost(GameConfig config, double xpPerRun, long? feeBonus = null)
    {
        var runs = 0;
        long sats = 0;
        for (var level = 1; level < Gauntlet.PveXpLevelCap; level++)
        {
            var need = (int)Math.Ceiling(Leveling.XpToNext(level, config) / xpPerRun);
            var fee = Leveling.MatchFee(level, config) + (feeBonus ?? Gauntlet.FeeBonusSats);
            runs += need;
            sats += need * fee;
        }
        return (runs, sats);
    }

    /// Mean XP a run pays below the cap, weighted evenly across the levels a climber passes through.
    private static double MeasuredYield(int samples, int seed)
    {
        long total = 0;
        var runs = 0;
        for (var level = 1; level < Gauntlet.PveXpLevelCap; level++)
        {
            var rng = new Random(seed + level);
            for (var i = 0; i < samples; i++)
            {
                var hero = new Hero
                {
                    Id = Guid.NewGuid().ToString("N"),
                    OwnerId = "sim",
                    Name = "Probe",
                    Genome = Genome.NewRecruit(Entropy(rng), StarterPolicy.RecruitStatCap),
                    Level = level,
                };
                total += Gauntlet.XpForRun(level, Gauntlet.Resolve(hero, Entropy(rng)).WavesCleared);
                runs++;
            }
        }
        return (double)total / runs;
    }

    private static byte[] Entropy(Random rng)
    {
        var b = new byte[32];
        rng.NextBytes(b);
        return SHA256.HashData(b);
    }
}
