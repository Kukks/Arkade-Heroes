using System.Security.Cryptography;
using ArkadeHeroes.Core.Genetics;

namespace ArkadeHeroes.Tests;

public class GeneMixerTests
{
    private static Genome GenomeOf(byte fill)
    {
        var bytes = new byte[32];
        Array.Fill(bytes, fill, 0, 16);
        return new Genome(bytes);
    }

    [Fact]
    public void MixIsDeterministic()
    {
        var a = Genome.NewGen0([1]);
        var b = Genome.NewGen0([2]);
        var entropy = SHA256.HashData([3]);

        var child1 = GeneMixer.Mix(a, b, entropy);
        var child2 = GeneMixer.Mix(a, b, entropy);
        Assert.Equal(child1, child2);
    }

    [Fact]
    public void EveryNonMutatedTraitComesFromAParent()
    {
        var a = GenomeOf(10);
        var b = GenomeOf(250);
        var entropy = SHA256.HashData([42]);

        var child = GeneMixer.Mix(a, b, entropy);

        // Each of the 16 active bytes is from parent A, parent B, or (rarely) a mutation.
        for (var i = 0; i < 16; i++)
        {
            var fromParent = child[i] == a[i] || child[i] == b[i];
            var wasMutationEligible = entropy[i] >= 248;
            Assert.True(fromParent || wasMutationEligible,
                $"Byte {i} = {child[i]} is neither parental (10/250) nor flagged as mutation (selector {entropy[i]}).");
        }
    }

    [Fact]
    public void ReservedBytesStayZero()
    {
        var a = Genome.NewGen0([7]);
        var b = Genome.NewGen0([8]);
        for (var n = 0; n < 50; n++)
        {
            var child = GeneMixer.Mix(a, b, SHA256.HashData([(byte)n]));
            for (var i = 16; i < 32; i++)
                Assert.Equal(0, child[i]);
        }
    }

    [Fact]
    public void MutationsOccurAtRoughlyExpectedRate()
    {
        // 1/32 per trait × 16 single-byte traits — over 2000 breedings expect ~1000
        // mutated bytes; assert a generous window to avoid flakiness.
        var a = GenomeOf(10);
        var b = GenomeOf(250);
        var mutated = 0;
        for (var n = 0; n < 2000; n++)
        {
            var entropy = SHA256.HashData(BitConverter.GetBytes(n));
            var child = GeneMixer.Mix(a, b, entropy);
            for (var i = 0; i < 16; i++)
                if (child[i] != a[i] && child[i] != b[i])
                    mutated++;
        }
        Assert.InRange(mutated, 300, 2200);
    }

    [Fact]
    public void ChildGenerationIsMaxPlusOne()
    {
        Assert.Equal(1, GeneMixer.ChildGeneration(0, 0));
        Assert.Equal(6, GeneMixer.ChildGeneration(5, 2));
        Assert.Equal(6, GeneMixer.ChildGeneration(2, 5));
    }

    [Fact]
    public void EntropyMustBe32Bytes()
    {
        var a = Genome.NewGen0([1]);
        Assert.Throws<ArgumentException>(() => GeneMixer.Mix(a, a, new byte[16]));
    }
}
