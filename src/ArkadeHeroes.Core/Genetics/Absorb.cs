using System.Security.Cryptography;

namespace ArkadeHeroes.Core.Genetics;

/// <summary>
/// Server-configured, client-propagated absorb odds — passed into <see cref="Absorb.Resolve"/>
/// so the server (settle), the client (VerifyAbsorb), and the InMemory sim all compute an
/// identical outcome. Published on <c>/api/chain/info</c> (T4); NOT a Core constant.
/// </summary>
public sealed record AbsorbOdds(byte AbsorbChance, byte ContinueChance)
{
    /// <summary>≈40% the absorb happens at all; ≈35% decay for each additional trait.</summary>
    public static AbsorbOdds Default => new(102, 90);
}

/// <summary>The result of an absorb roll: the (possibly re-minted) genome and how much was absorbed.</summary>
public sealed record AbsorbOutcome(Genome Result, bool Minted, int TraitsAbsorbed, IReadOnlyList<TraitCategory> Absorbed);

/// <summary>
/// A death-match winner absorbing traits from the burned loser. Seed-driven and weighted toward
/// few traits (mostly one, rarely a full concentrate). Only IMPROVEMENTS are candidates — the
/// loser's dominant genes that are rarer than the winner's. Stats [0..15] and recessives stay the
/// winner's (no power-creep). Deterministic given (winner, loser, entropy, odds). Domain tag 0x41
/// ('A') — distinct from Fusion (0x4D) and GeneMixer (0x54).
/// </summary>
public static class Absorb
{
    public static AbsorbOutcome Resolve(Genome winner, Genome loser, ReadOnlySpan<byte> entropy, AbsorbOdds odds)
    {
        // 1. Candidate categories: the loser's dominant is strictly rarer than the winner's.
        //    Ordered by the rarity GAIN (desc), then category index — deterministic.
        var candidates = new List<(TraitCategory Cat, int Delta)>();
        for (var c = 0; c < Traits.CategoryCount; c++)
        {
            var cat = (TraitCategory)c;
            var delta = Traits.WeightOf(loser.DominantGene(cat)) - Traits.WeightOf(winner.DominantGene(cat));
            if (delta > 0) candidates.Add((cat, delta));
        }
        candidates.Sort((a, b) => a.Delta != b.Delta ? b.Delta.CompareTo(a.Delta) : ((int)a.Cat).CompareTo((int)b.Cat));
        var m = candidates.Count;

        // 2. Roll k from a dedicated pool. pool[0] gates whether it happens at all; each further
        //    pool byte extends the absorb by one trait (front-loaded → mostly 1, rarely m).
        Span<byte> pool = stackalloc byte[32];
        ComputePool(winner, loser, entropy, pool);
        var k = 0;
        if (m > 0 && pool[0] < odds.AbsorbChance)
        {
            k = 1;
            while (k < m && pool[k] < odds.ContinueChance) k++;
        }

        // 3. Graft the top-k loser dominants onto a copy of the winner (stats + recessives kept).
        Span<byte> child = stackalloc byte[Genome.Size];
        winner.Bytes.CopyTo(child);
        var absorbed = new List<TraitCategory>(k);
        for (var i = 0; i < k; i++)
        {
            var cat = candidates[i].Cat;
            child[16 + (int)cat * 2] = loser.DominantGene(cat);
            absorbed.Add(cat);
        }
        return new AbsorbOutcome(new Genome(child), Minted: k >= 1, TraitsAbsorbed: k, Absorbed: absorbed);
    }

    private static void ComputePool(Genome w, Genome l, ReadOnlySpan<byte> entropy, Span<byte> destination)
    {
        Span<byte> pre = stackalloc byte[Genome.Size * 2 + 32 + 1];
        w.Bytes.CopyTo(pre);
        l.Bytes.CopyTo(pre[Genome.Size..]);
        entropy[..32].CopyTo(pre[(Genome.Size * 2)..]);
        pre[^1] = 0x41; // 'A' — domain tag, distinct from Fusion (0x4D) and GeneMixer (0x54)
        SHA256.HashData(pre, destination);
    }
}
