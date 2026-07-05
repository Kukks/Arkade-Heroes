using System.Security.Cryptography;

namespace ArkadeHeroes.Core.Genetics;

/// <summary>
/// Merging two heroes into one that CONCENTRATES their traits toward the rarest.
/// Entropy-seeded (from commit–reveal), so the fused genome — and hence its
/// genome-derived rarity + sterility — can't be precomputed: concentration almost
/// always succeeds, but pushing toward Legendary risks a sterile dead-end. Stats
/// [0..15] come only from the base (no power-creep). Deterministic given its inputs.
/// </summary>
public static class Fusion
{
    /// <summary>Per-category concentrate probability: pool byte &lt; 217 (~85%) takes the rarer dominant.</summary>
    private const byte ConcentrateThreshold = 217;

    public static Genome Fuse(Genome baseGenome, Genome sacrificeGenome, ReadOnlySpan<byte> entropy)
    {
        Span<byte> child = stackalloc byte[Genome.Size];
        baseGenome.Bytes[..16].CopyTo(child); // stats/element/skills/growth/cooldown/appearance from base

        // Dedicated deterministic pool: 4 bytes per category (concentrate-roll, recessive-select, spare, spare).
        Span<byte> pool = stackalloc byte[32];
        ComputePool(baseGenome, sacrificeGenome, entropy, pool);

        for (var c = 0; c < Traits.CategoryCount; c++)
        {
            var cat = (TraitCategory)c;
            byte bDom = baseGenome.DominantGene(cat), sDom = sacrificeGenome.DominantGene(cat);
            byte bRec = baseGenome.RecessiveGene(cat), sRec = sacrificeGenome.RecessiveGene(cat);

            // Dominant: usually the rarer of the two inputs (concentration); sometimes the lesser.
            var rarer = Traits.WeightOf(bDom) >= Traits.WeightOf(sDom) ? bDom : sDom;
            var lesser = Traits.WeightOf(bDom) >= Traits.WeightOf(sDom) ? sDom : bDom;
            child[16 + c * 2] = pool[c * 4] < ConcentrateThreshold ? rarer : lesser;

            // Recessive: entropy-pick among the four parent genes (hidden potential + variation).
            child[16 + c * 2 + 1] = (pool[c * 4 + 1] & 0b11) switch
            {
                0 => bDom, 1 => sDom, 2 => bRec, _ => sRec,
            };
        }
        return new Genome(child);
    }

    private static void ComputePool(Genome a, Genome b, ReadOnlySpan<byte> entropy, Span<byte> destination)
    {
        Span<byte> pre = stackalloc byte[Genome.Size * 2 + 32 + 1];
        a.Bytes.CopyTo(pre);
        b.Bytes.CopyTo(pre[Genome.Size..]);
        entropy[..32].CopyTo(pre[(Genome.Size * 2)..]); // entropy is the 32-byte revealed commit–reveal digest
        pre[^1] = 0x4D; // 'M' — domain tag, distinct from GeneMixer's 0x54
        SHA256.HashData(pre, destination);
    }
}
