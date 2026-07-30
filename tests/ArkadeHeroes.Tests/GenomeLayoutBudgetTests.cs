using ArkadeHeroes.Core.Genetics;

namespace ArkadeHeroes.Tests;

/// <summary>
/// The genome's BYTE BUDGET, pinned — because it is fully spent and nothing else says so.
///
/// A genome is exactly 32 bytes: [0..15] are the mechanical genes (stats, element, skills, growth,
/// cooldown, appearance) and [16..31] are the trait block, two bytes per category (an expressed dominant
/// and a hidden recessive). With eight categories that is 8 x 2 = 16 bytes, so the trait block is
/// SATURATED — there is not one spare byte left in a hero's genome.
///
/// That matters because <c>Genome.NewGen0</c> still documents [16..31] as "reserved ... so v1 genomes are
/// forward-compatible with later trait-map versions". That headroom was real once and has since been
/// consumed in full. A ninth trait category has nowhere to live, and neither escape route is free:
///   • growing Genome.Size rejects every existing 32-byte hero at construction (the ctor throws on a
///     wrong length), and their genomes are fixed in immutable mint metadata, so they cannot be padded;
///   • reusing bytes [0..15] silently rewrites every existing hero's stats, and every receipt and replay
///     already signed against the old reading stops agreeing.
/// The only non-breaking route is a VERSIONED genome — v1 heroes keep 32 bytes and the v1 trait map
/// forever, v2 heroes get their own — and that is dramatically cheaper before launch than after, because
/// after launch every hero is real-bitcoin-backed and its genome can never be rewritten.
///
/// So this file is a tripwire, not a unit test. If you are here because it went red, you have almost
/// certainly just added a trait category, and the fix is NOT to update the numbers below.
///
/// Everything is measured through the PUBLIC accessors rather than the private layout constants, so the
/// test pins the observable mapping and stays honest if the internals are reshuffled.
/// </summary>
public class GenomeLayoutBudgetTests
{
    /// <summary>First byte of the trait block. Hardcoded because Genome.TraitBase is private, and pinning it
    /// from outside is the point — a test that read the constant would agree with any value it was changed to.</summary>
    private const int TraitBlockStart = 16;

    /// <summary>
    /// The budget arithmetic, checked BEFORE anything reads a gene.
    ///
    /// Without this, adding a ninth category fails as a bare IndexOutOfRangeException from inside
    /// <c>DominantGene</c> — which is technically red but tells the next author nothing about what they broke
    /// or what their options are. The accessor is <c>Bytes[16 + (int)category * 2]</c> with no bounds check of
    /// its own, so category 8 walks straight off the end of a 32-byte genome. Fail here instead, out loud.
    /// </summary>
    private static void AssertEveryCategoryStillFitsInTheTraitBlock()
    {
        var capacity = (Genome.Size - TraitBlockStart) / 2;
        Assert.True(Traits.CategoryCount <= capacity,
            $"The trait block is {Genome.Size - TraitBlockStart} bytes = room for exactly {capacity} categories "
            + $"at two bytes each, but Traits.CategoryCount is now {Traits.CategoryCount}. The genome is FULL.\n\n"
            + "Do NOT fix this by bumping Genome.Size or by borrowing a byte from [0..15]. Both rewrite what "
            + "an ALREADY-MINTED hero means, and a minted hero's genome lives in immutable mint metadata where "
            + "it can never be migrated:\n"
            + "  * a wider Genome.Size makes every existing 32-byte hero throw at construction;\n"
            + "  * reusing a mechanical byte silently changes existing heroes' stats, and every receipt and "
            + "battle replay already signed against the old reading stops agreeing.\n\n"
            + "The non-breaking route is a VERSIONED genome: v1 heroes keep 32 bytes and the v1 trait map "
            + "forever, v2 heroes get their own layout. See this file's summary comment.");
    }

    /// <summary>Which single genome byte an accessor reads, found by probing one marked byte at a time.</summary>
    private static int IndexReadBy(Func<Genome, byte> read)
    {
        var hits = new List<int>();
        for (var i = 0; i < Genome.Size; i++)
        {
            var bytes = new byte[Genome.Size];
            bytes[i] = 0xFF;
            if (read(new Genome(bytes)) == 0xFF) hits.Add(i);
        }
        return Assert.Single(hits);   // a trait accessor reads exactly one byte, never a blend
    }

    [Fact]
    public void TheTraitBlockIsSaturated_SoANinthCategoryHasNowhereToGo()
    {
        AssertEveryCategoryStillFitsInTheTraitBlock();

        var touched = new List<int>();
        for (var c = 0; c < Traits.CategoryCount; c++)
        {
            var category = (TraitCategory)c;
            touched.Add(IndexReadBy(g => g.DominantGene(category)));
            touched.Add(IndexReadBy(g => g.RecessiveGene(category)));
        }

        // Two bytes per category, no category sharing a byte with another.
        Assert.Equal(Traits.CategoryCount * 2, touched.Count);
        Assert.Equal(touched.Count, touched.Distinct().Count());

        // And they occupy the trait block EXACTLY — 16..31 with nothing left over. This equality is the
        // whole point: the moment it stops holding, either a category has overflowed the block or a byte
        // has been quietly repurposed, and both change what an ALREADY-MINTED hero means.
        Assert.Equal(Enumerable.Range(TraitBlockStart, 16), touched.OrderBy(i => i));
    }

    [Fact]
    public void Gen0LeavesTheWholeTraitBlockZero_WhichIsWhyStartersExpressNoTraits()
    {
        // The property the reserved-region comment rests on, and the same one that makes a green combat
        // suite blind to trait-driven changes when its fixtures are gen-0 starters (see the flip-safety
        // tests, which breed real children precisely to escape this).
        AssertEveryCategoryStillFitsInTheTraitBlock();

        var gen0 = Genome.NewGen0(Enumerable.Range(0, 32).Select(i => (byte)(i * 7 + 1)).ToArray());

        for (var c = 0; c < Traits.CategoryCount; c++)
        {
            var category = (TraitCategory)c;
            Assert.Equal(0, gen0.DominantGene(category));
            Assert.Equal(0, gen0.RecessiveGene(category));
        }
    }

    [Fact]
    public void AGenomeIsExactlyThirtyTwoBytes_SoAWiderOneCannotBeLoadedAlongsideTheOldOnes()
    {
        // Names the constraint that closes off "just make the genome bigger": the ctor is strict in BOTH
        // directions, so a v2 genome cannot simply be read by v1 code, and a v1 genome cannot be padded
        // into v2 shape without changing the bytes a hero's identity was minted from.
        Assert.Equal(32, Genome.Size);
        Assert.Throws<ArgumentException>(() => new Genome(new byte[Genome.Size + 1]));
        Assert.Throws<ArgumentException>(() => new Genome(new byte[Genome.Size - 1]));
    }
}
