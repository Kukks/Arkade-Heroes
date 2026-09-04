using System.Security.Cryptography;
using System.Text;
using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Progression;

namespace ArkadeHeroes.Sim;

/// <summary>
/// How fast collectibility actually arrives. Gen-0 and recruited heroes are trait-blank by
/// construction (<c>Genome.NewGen0</c> clears bytes [16..]), so every rare trait in the world
/// entered it through a breeding mutation. This walks generations of a closed population and
/// reports the tier mix at each one.
/// </summary>
public static class RarityCurve
{
    public static string Render(int population, int generations, int seed)
    {
        var rng = new Random(seed);
        var sb = new StringBuilder();
        sb.AppendLine($"RARITY BY GENERATION — {population} heroes/gen, seed {seed}");
        sb.AppendLine($"  {"gen",4} {"Common",8} {"Uncommon",9} {"Rare",6} {"Epic",6} {"Legend",7}   {"non-Common",11} {"avg score",9}");

        var current = new List<Genome>();
        for (var i = 0; i < population; i++)
            current.Add(Genome.NewGen0(Bytes(rng, 32)));
        Row(sb, 0, current);

        for (var gen = 1; gen <= generations; gen++)
        {
            var next = new List<Genome>(population);
            for (var i = 0; i < population; i++)
            {
                var a = current[rng.Next(current.Count)];
                var b = current[rng.Next(current.Count)];
                next.Add(GeneMixer.Mix(a, b, Bytes(rng, 32)));
            }
            Row(sb, gen, next);
            current = next;
        }

        sb.AppendLine();
        sb.AppendLine("  Read this as the ceiling, not the forecast: it assumes every hero breeds every");
        sb.AppendLine("  generation with no cooldown, no fee, and no sterility culling the rare lines.");
        return sb.ToString();
    }

    private static void Row(StringBuilder sb, int gen, List<Genome> genomes)
    {
        var results = genomes.Select(g => Rarity.Of(g)).ToList();
        int Count(RarityTier t) => results.Count(r => r.Tier == t);
        var nonCommon = results.Count(r => r.Tier != RarityTier.Common);
        sb.AppendLine(
            $"  {gen,4} {Count(RarityTier.Common),8} {Count(RarityTier.Uncommon),9} {Count(RarityTier.Rare),6} " +
            $"{Count(RarityTier.Epic),6} {Count(RarityTier.Legendary),7}   " +
            $"{100.0 * nonCommon / results.Count,10:F1}% {results.Average(r => r.Score),9:F2}");
    }

    private static byte[] Bytes(Random rng, int n)
    {
        var b = new byte[n];
        rng.NextBytes(b);
        return SHA256.HashData(b);
    }
}
