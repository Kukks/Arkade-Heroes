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
/// Whether a fight is worth watching. Two things make combat unfun from opposite ends: a favourite
/// that always wins (the result was decided at the matchmaking screen) and a favourite that wins
/// half the time (stats are decoration). This runs real <c>BattleEngine</c> fights across a spread
/// of power gaps and reports the curve between those poles — which is also the win-rate sim
/// <c>PowerScore</c>'s own doc comment says its heuristic weights should be tuned against.
/// </summary>
public static class CombatBalance
{
    public static string Render(int samples, int seed)
    {
        var rng = new Random(seed);
        var buckets = new SortedDictionary<int, (int Fights, int FavouriteWins, long Turns)>();
        var timeouts = 0;
        var mirrorWins = 0;
        var mirrors = 0;

        for (var i = 0; i < samples; i++)
        {
            var a = RandomHero(rng, "A");
            var b = RandomHero(rng, "B");
            var pa = PowerScore.Compute(a);
            var pb = PowerScore.Compute(b);
            if (pa == 0 || pb == 0) continue;

            var result = BattleEngine.Fight(a, b, Entropy(rng));
            var favourite = pa >= pb ? a.Id : b.Id;
            var gap = (int)Math.Round(100.0 * Math.Abs(pa - pb) / Math.Max(pa, pb));
            var bucket = Math.Min(gap / 10 * 10, 50);

            var cur = buckets.GetValueOrDefault(bucket);
            buckets[bucket] = (cur.Fights + 1,
                cur.FavouriteWins + (result.WinnerId == favourite ? 1 : 0),
                cur.Turns + result.Turns);

            if (result.Turns >= BattleEngine.MaxTurns) timeouts++;
            if (pa == pb) { mirrors++; if (result.WinnerId == a.Id) mirrorWins++; }
        }

        var sb = new StringBuilder();
        sb.AppendLine($"COMBAT BALANCE — {samples} seeded fights between random heroes, seed {seed}");
        sb.AppendLine($"  {"power gap",-12} {"fights",7} {"favourite wins",15} {"avg turns",10}");
        foreach (var (bucket, v) in buckets)
        {
            var label = bucket >= 50 ? "50%+" : $"{bucket}-{bucket + 9}%";
            sb.AppendLine($"  {label,-12} {v.Fights,7} {100.0 * v.FavouriteWins / v.Fights,14:F1}% " +
                          $"{(double)v.Turns / v.Fights,10:F1}");
        }

        var total = buckets.Values.Sum(v => v.Fights);
        var upsets = total - buckets.Values.Sum(v => v.FavouriteWins);
        sb.AppendLine();
        sb.AppendLine($"  upsets overall: {100.0 * upsets / total:F1}% of {total} fights");
        sb.AppendLine($"  fights hitting the {BattleEngine.MaxTurns}-turn cap: {timeouts} ({100.0 * timeouts / total:F1}%)");
        if (mirrors > 0)
            sb.AppendLine($"  equal-power fights: {mirrors}, first-named side won {100.0 * mirrorWins / mirrors:F1}% " +
                          "(50% = no turn-order bias)");
        sb.Append(Matchmade(samples, seed));

        sb.AppendLine();
        sb.AppendLine("  Read the top row as fairness and the bottom as decisiveness: a favourite winning ~50%");
        sb.AppendLine("  at a 0-9% gap means power tracks nothing, and ~100% at 50%+ means the fight was");
        sb.AppendLine("  decided before it started. PowerScore only orders opponent SUGGESTIONS — it never");
        sb.AppendLine("  enters combat or the XP transfer — so this measures the heuristic, not the rules.");
        return sb.ToString();
    }

    /// The fight a player actually gets. Random pairings are not what the game serves: Matchmaking
    /// ranks opponents by power gap and the UI offers the closest few, so the realistic distribution
    /// is far narrower than a random field's.
    private static string Matchmade(int samples, int seed)
    {
        var rng = new Random(seed + 1);
        var pool = Enumerable.Range(0, 40).Select(_ => RandomHero(rng, "P")).ToList();
        var scored = pool.Select(h => (Hero: h, Power: PowerScore.Compute(h))).Where(x => x.Power > 0).ToList();

        int fights = 0, favouriteWins = 0, gapSum = 0;
        long turns = 0;
        for (var i = 0; i < samples; i++)
        {
            var me = scored[rng.Next(scored.Count)];
            // The suggestion list: nearest by power, as Matchmaking orders it.
            var candidates = scored.Where(x => !ReferenceEquals(x.Hero, me.Hero))
                .OrderBy(x => Math.Abs(x.Power - me.Power)).Take(4).ToList();
            if (candidates.Count == 0) continue;
            var them = candidates[rng.Next(candidates.Count)];

            var result = BattleEngine.Fight(me.Hero, them.Hero, Entropy(rng));
            var favourite = me.Power >= them.Power ? me.Hero.Id : them.Hero.Id;
            if (result.WinnerId == favourite) favouriteWins++;
            gapSum += (int)Math.Round(100.0 * Math.Abs(me.Power - them.Power) / Math.Max(me.Power, them.Power));
            turns += result.Turns;
            fights++;
        }

        if (fights == 0) return string.Empty;
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine($"  MATCHMADE (opponent drawn from the 4 nearest by power, as the game suggests them)");
        sb.AppendLine($"    {fights} fights   mean power gap {(double)gapSum / fights,4:F1}%   " +
                      $"favourite wins {100.0 * favouriteWins / fights:F1}%   " +
                      $"avg turns {(double)turns / fights:F1}");
        return sb.ToString();
    }

    /// A spread of levels, genome grades and gear, so the sample covers real matchups rather than clones.
    private static Hero RandomHero(Random rng, string tag)
    {
        var level = 1 + rng.Next(12);
        var recruit = rng.Next(2) == 0;
        var hero = new Hero
        {
            Id = $"{tag}-{Guid.NewGuid():N}",
            OwnerId = "sim",
            Name = tag,
            Genome = recruit
                ? Genome.NewRecruit(Entropy(rng), StarterPolicy.RecruitStatCap)
                : Genome.NewGen0(Entropy(rng)),
            Level = level,
        };

        // Roughly half the field brings gear it could actually equip at its level.
        if (rng.Next(2) == 0)
        {
            foreach (var slot in ItemCatalog.All.Select(i => i.Slot).Distinct())
            {
                var options = ItemCatalog.All.Where(i => i.Slot == slot && i.MinLevel <= level).ToList();
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
