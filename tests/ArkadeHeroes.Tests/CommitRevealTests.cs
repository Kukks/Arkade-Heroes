using ArkadeHeroes.Core.Fairness;

namespace ArkadeHeroes.Tests;

public class CommitRevealTests
{
    [Fact]
    public void CommitmentVerifies()
    {
        var seed = CommitReveal.NewSeed();
        var commitment = CommitReveal.Commit(seed);
        Assert.True(CommitReveal.Verify(seed, commitment));
        Assert.False(CommitReveal.Verify(CommitReveal.NewSeed(), commitment));
    }

    [Fact]
    public void EntropyIsDeterministicAndContextSensitive()
    {
        var seed = new byte[32];
        var e1 = CommitReveal.DeriveEntropy(seed, "heroA", "heroB", "nonce");
        var e2 = CommitReveal.DeriveEntropy(seed, "heroA", "heroB", "nonce");
        var e3 = CommitReveal.DeriveEntropy(seed, "heroA", "heroB", "other");
        Assert.Equal(Convert.ToHexString(e1), Convert.ToHexString(e2));
        Assert.NotEqual(Convert.ToHexString(e1), Convert.ToHexString(e3));
        Assert.Equal(32, e1.Length);
    }

    [Fact]
    public void PartBoundariesMatter()
    {
        // Length-prefixing must keep ("ab","c") distinct from ("a","bc").
        var seed = new byte[32];
        var e1 = CommitReveal.DeriveEntropy(seed, "ab", "c");
        var e2 = CommitReveal.DeriveEntropy(seed, "a", "bc");
        Assert.NotEqual(Convert.ToHexString(e1), Convert.ToHexString(e2));
    }
}
