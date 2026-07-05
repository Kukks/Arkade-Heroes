using System.Security.Cryptography;

namespace ArkadeHeroes.Core.Genetics;

/// <summary>
/// Deterministic genome mixing, modeled on the ArkadeKitties design's
/// <c>mixGenomes(genomeA, genomeB, entropy)</c>: per-trait crossover selected by
/// entropy bytes, with a rare mutation branch that rerolls a trait from
/// <c>SHA256(genomeA || genomeB || entropy || traitIndex)</c>.
///
/// The function is pure: given the same parents and entropy it always produces
/// the same child, so anyone holding the revealed entropy can re-derive and
/// audit a child genome (and a future Arkade Script covenant can enforce the
/// same computation on-chain).
/// </summary>
public static class GeneMixer
{
    /// <summary>Trait regions of the genome that cross over as atomic units.</summary>
    private static readonly (int Offset, int Length)[] TraitRegions =
    [
        (0, 1), (1, 1), (2, 1), (3, 1), (4, 1), // stat genes
        (5, 1),                                 // element
        (6, 1), (7, 1),                         // skill genes
        (8, 1), (9, 1), (10, 1), (11, 1), (12, 1), // growth genes
        (13, 1),                                // cooldown
        (14, 1), (15, 1),                       // appearance
        // [16..31] are the trait categories — handled by MixTraits, not crossover regions.
    ];

    /// <summary>Mutation triggers when the selector byte is ≥ 248 — an 8/256 (1/32) chance per trait.</summary>
    private const byte MutationThreshold = 248;

    public static Genome Mix(Genome parentA, Genome parentB, ReadOnlySpan<byte> entropy)
    {
        if (entropy.Length != 32)
            throw new ArgumentException("Entropy must be exactly 32 bytes.", nameof(entropy));

        Span<byte> child = stackalloc byte[Genome.Size];

        // Trait-region mutation source: one hash per breeding, sliced per trait.
        Span<byte> mutationPool = stackalloc byte[32];
        ComputeMutationPool(parentA, parentB, entropy, mutationPool);

        for (var t = 0; t < TraitRegions.Length; t++)
        {
            var (offset, length) = TraitRegions[t];
            var selector = entropy[offset];

            if (selector >= MutationThreshold)
            {
                // Mutation: reroll this trait region from the pool.
                for (var i = 0; i < length; i++)
                    child[offset + i] = mutationPool[(offset + i) % mutationPool.Length];
            }
            else
            {
                var source = (selector & 1) == 0 ? parentA : parentB;
                for (var i = 0; i < length; i++)
                    child[offset + i] = source[offset + i];
            }
        }

        // Trait categories [16..31]: dominant/recessive inheritance + rare mutation,
        // all seeded deterministically from the parents + entropy (covenant-critical).
        MixTraits(parentA, parentB, entropy, child);

        return new Genome(child);
    }

    /// <summary>Matches ArkadeKitties' <c>computeChildGeneration</c>: max(parents) + 1.</summary>
    public static int ChildGeneration(int generationA, int generationB)
        => Math.Max(generationA, generationB) + 1;

    private static void ComputeMutationPool(
        Genome parentA, Genome parentB, ReadOnlySpan<byte> entropy, Span<byte> destination)
    {
        Span<byte> preimage = stackalloc byte[Genome.Size * 2 + 32];
        parentA.Bytes.CopyTo(preimage);
        parentB.Bytes.CopyTo(preimage[Genome.Size..]);
        entropy.CopyTo(preimage[(Genome.Size * 2)..]);
        SHA256.HashData(preimage, destination);
    }

    /// <summary>Per-category mutation trigger: pool byte ≥ 250 → ~2.3% chance to introduce a new variant.</summary>
    private const byte TraitMutationThreshold = 250;

    private static void MixTraits(Genome a, Genome b, ReadOnlySpan<byte> entropy, Span<byte> child)
    {
        // Dedicated deterministic pool: 4 bytes per category (dom-select, rec-select,
        // mutation-roll, mutation-variant). Domain-separated from the region pool.
        Span<byte> pool = stackalloc byte[32];
        ComputeTraitPool(a, b, entropy, pool);

        for (var c = 0; c < Traits.CategoryCount; c++)
        {
            var cat = (TraitCategory)c;
            var domSel = pool[c * 4 + 0];
            var recSel = pool[c * 4 + 1];
            var mutRoll = pool[c * 4 + 2];
            var mutVar = pool[c * 4 + 3];

            // Four candidate genes: both parents' dominant + recessive.
            byte aDom = a.DominantGene(cat), aRec = a.RecessiveGene(cat);
            byte bDom = b.DominantGene(cat), bRec = b.RecessiveGene(cat);

            // Dominant-favored pick for the child's DOMINANT: dominants ~3/4, a hidden
            // recessive surfaces ~1/4 (the mewtation moment).
            byte childDom = domSel switch
            {
                < 96 => aDom,
                < 192 => bDom,
                < 224 => aRec,
                _ => bRec,
            };
            // Recessive is carried forward, favoring the parents' recessives.
            byte childRec = recSel switch
            {
                < 96 => aRec,
                < 192 => bRec,
                < 224 => aDom,
                _ => bDom,
            };

            // Mutation introduces a NEW variant into the DOMINANT slot (rarity-weighted,
            // biased common so legendaries are vanishingly rare).
            if (mutRoll >= TraitMutationThreshold)
                childDom = MutatedVariant(mutVar);

            child[16 + c * 2] = childDom;
            child[16 + c * 2 + 1] = childRec;
        }
    }

    /// <summary>Maps a roll byte to a mutated variant value, biased toward common tiers so rare traits stay rare even when a mutation fires.</summary>
    private static byte MutatedVariant(byte roll) => roll switch
    {
        < 176 => (byte)(1 + roll % 205),   // Common   1..205   (~69%)
        < 232 => (byte)(206 + roll % 35),  // Uncommon 206..240
        < 250 => (byte)(241 + roll % 12),  // Rare     241..252
        < 254 => (byte)(253 + roll % 2),   // Epic     253..254
        _ => 255,                          // Legendary          (~0.8%)
    };

    private static void ComputeTraitPool(Genome a, Genome b, ReadOnlySpan<byte> entropy, Span<byte> destination)
    {
        // Domain tag byte 0x54 ('T') separates this from ComputeMutationPool.
        Span<byte> preimage = stackalloc byte[Genome.Size * 2 + 32 + 1];
        a.Bytes.CopyTo(preimage);
        b.Bytes.CopyTo(preimage[Genome.Size..]);
        entropy.CopyTo(preimage[(Genome.Size * 2)..]);
        preimage[^1] = 0x54;
        SHA256.HashData(preimage, destination);
    }
}
