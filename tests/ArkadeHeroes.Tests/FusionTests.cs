using ArkadeHeroes.Core.Genetics;

namespace ArkadeHeroes.Tests;

public class FusionTests
{
    private static Genome WithDom(TraitCategory cat, byte value)
    {
        var b = new byte[32];
        b[16 + (int)cat * 2] = value;
        return new Genome(b);
    }
    private static byte[] Ent(int seed)
    {
        var e = new byte[32];
        for (var i = 0; i < 32; i++) e[i] = (byte)(seed * 31 + i * 7);
        return e;
    }

    [Fact]
    public void Fuse_IsDeterministic()
    {
        var a = WithDom(TraitCategory.Aura, 255);
        var b = WithDom(TraitCategory.Crest, 250);
        var e = Ent(3);
        Assert.Equal(Fusion.Fuse(a, b, e).ToHex(), Fusion.Fuse(a, b, e).ToHex());
    }

    [Fact]
    public void Fuse_KeepsBaseStatBytes()
    {
        var baseG = new byte[32]; baseG[0] = 200; baseG[8] = 150; // stat + growth genes
        var sacG = new byte[32]; sacG[0] = 10; sacG[8] = 10;
        var fused = Fusion.Fuse(new Genome(baseG), new Genome(sacG), Ent(1));
        for (var i = 0; i < 16; i++) Assert.Equal(baseG[i], fused[i]); // [0..15] = base
    }

    [Fact]
    public void Fuse_ConcentratesTowardTheRarerTrait_MostOfTheTime()
    {
        // Base has Legendary Aura, sacrifice Common Aura → fused should express the
        // Legendary in the vast majority of entropy draws (~85% concentrate rate).
        var baseG = WithDom(TraitCategory.Aura, 255);
        var sacG = WithDom(TraitCategory.Aura, 50);
        var legendary = 0;
        for (var s = 0; s < 100; s++)
            if (Fusion.Fuse(baseG, sacG, Ent(s)).DominantGene(TraitCategory.Aura) == 255) legendary++;
        Assert.True(legendary is > 60 and < 100, $"expected mostly-but-not-always concentration (got {legendary}/100)");
    }
}
