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
        (16, 16),                               // reserved block inherits as one unit
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

        // v1 trait map: reserved bytes stay zero even through mutation, so all
        // genomes remain forward-compatible until a trait-map version bump.
        child[16..].Clear();

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
}
