using ArkadeHeroes.Core.Genetics;

namespace ArkadeHeroes.Tests;

public class GenomeTests
{
    [Fact]
    public void HexRoundTrips()
    {
        var bytes = Enumerable.Range(0, 32).Select(i => (byte)(i * 7)).ToArray();
        var genome = new Genome(bytes);
        var restored = Genome.FromHex(genome.ToHex());
        Assert.Equal(genome, restored);
    }

    [Fact]
    public void RejectsWrongLength()
    {
        Assert.Throws<ArgumentException>(() => new Genome(new byte[31]));
        Assert.Throws<ArgumentException>(() => new Genome(new byte[33]));
    }

    [Fact]
    public void Gen0IsDeterministicAndZerosReservedBytes()
    {
        var entropy = new byte[] { 1, 2, 3, 4 };
        var a = Genome.NewGen0(entropy);
        var b = Genome.NewGen0(entropy);
        Assert.Equal(a, b);
        for (var i = 16; i < 32; i++)
            Assert.Equal(0, a[i]);
    }

    [Fact]
    public void TraitAccessorsReadTheirBytes()
    {
        var bytes = new byte[32];
        bytes[0] = 200; // STR
        bytes[4] = 50;  // LUK
        bytes[5] = 10;  // element 10 % 8 = 2 = Terra
        bytes[8] = 130; // STR growth
        bytes[13] = 99; // cooldown
        var g = new Genome(bytes);

        Assert.Equal(200, g.StrengthGene);
        Assert.Equal(50, g.LuckGene);
        Assert.Equal(Element.Terra, g.Element);
        Assert.Equal(130, g.GrowthGene(Stat.Strength));
        Assert.Equal(99, g.CooldownGene);
    }
}
