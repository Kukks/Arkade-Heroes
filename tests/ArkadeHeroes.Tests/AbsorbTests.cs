using ArkadeHeroes.Core.Genetics;

namespace ArkadeHeroes.Tests;

/// <summary>
/// The deterministic trait-absorb ladder (<see cref="Absorb.Resolve"/>): only the loser's rarer
/// dominants are candidates, the roll is odds-driven and front-loaded, stats + recessives stay the
/// winner's, and the whole thing is a pure function of (winner, loser, entropy, odds). Where a
/// probabilistic mint is needed the tests scan entropies for a qualifying roll, so every assertion
/// is deterministic without predicting SHA256.
/// </summary>
public class AbsorbTests
{
    /// <summary>A genome with the given DOMINANT genes per category (recessives + stats zero unless set below).</summary>
    private static Genome GenomeWith(params (TraitCategory Cat, byte Dom)[] traits)
    {
        var b = new byte[Genome.Size];
        foreach (var (cat, dom) in traits) b[16 + (int)cat * 2] = dom;
        return new Genome(b);
    }

    private static byte[] Ent(byte seed)
    {
        var e = new byte[32];
        Array.Fill(e, seed);
        return e;
    }

    [Fact]
    public void Resolve_IsDeterministic()
    {
        var winner = GenomeWith((TraitCategory.Aura, 100));
        var loser = GenomeWith((TraitCategory.Aura, 255)); // Legendary — a better Aura
        var e = Ent(7);
        var a = Absorb.Resolve(winner, loser, e, AbsorbOdds.Default);
        var b = Absorb.Resolve(winner, loser, e, AbsorbOdds.Default);
        Assert.Equal(a.Result.ToHex(), b.Result.ToHex());
        Assert.Equal(a.Minted, b.Minted);
        Assert.Equal(a.TraitsAbsorbed, b.TraitsAbsorbed);
    }

    [Fact]
    public void Resolve_NoBetterTraits_AlwaysKeeps()
    {
        // Winner rarer everywhere (loser all-plain) → m==0 → keep for ANY entropy/odds.
        var winner = GenomeWith((TraitCategory.Aura, 255), (TraitCategory.Eyes, 250));
        var loser = GenomeWith(); // all zero → nothing better to absorb
        for (byte i = 0; i < 40; i++)
        {
            var a = Absorb.Resolve(winner, loser, Ent(i), new AbsorbOdds(255, 255));
            Assert.False(a.Minted);
            Assert.Equal(0, a.TraitsAbsorbed);
            Assert.Equal(winner.ToHex(), a.Result.ToHex());
        }
    }

    [Fact]
    public void Resolve_ZeroAbsorbChance_AlwaysKeeps()
    {
        // pool[0] < 0 is never true → k stays 0 for every entropy, even with better traits present.
        var winner = GenomeWith((TraitCategory.Aura, 10));
        var loser = GenomeWith((TraitCategory.Aura, 255));
        for (byte i = 0; i < 40; i++)
        {
            var a = Absorb.Resolve(winner, loser, Ent(i), new AbsorbOdds(0, 0));
            Assert.False(a.Minted);
            Assert.Equal(winner.ToHex(), a.Result.ToHex());
        }
    }

    [Fact]
    public void Resolve_WhenItAbsorbs_TakesTheLosersBetterTrait()
    {
        var winner = GenomeWith((TraitCategory.Aura, 10));  // weak Aura
        var loser = GenomeWith((TraitCategory.Aura, 255));  // Legendary Aura
        for (byte i = 0; i < 40; i++)
        {
            var a = Absorb.Resolve(winner, loser, Ent(i), new AbsorbOdds(255, 0)); // 0 continue → exactly 1 trait
            if (!a.Minted) continue;
            Assert.Equal(1, a.TraitsAbsorbed);
            Assert.Equal(255, a.Result.DominantGene(TraitCategory.Aura)); // absorbed the better Aura
            Assert.Contains(TraitCategory.Aura, a.Absorbed);
            return;
        }
        Assert.Fail("expected at least one absorb across 40 entropies at AbsorbChance=255");
    }

    [Fact]
    public void Resolve_StatsAndRecessivesStayWinners()
    {
        var wb = new byte[Genome.Size];
        for (var i = 0; i < 16; i++) wb[i] = (byte)(i + 1);            // distinctive stat block
        wb[16 + (int)TraitCategory.Aura * 2] = 10;                     // winner's weak Aura dominant
        wb[16 + (int)TraitCategory.Aura * 2 + 1] = 77;                 // winner's Aura recessive
        var winner = new Genome(wb);
        var loser = GenomeWith((TraitCategory.Aura, 255));
        for (byte i = 0; i < 40; i++)
        {
            var a = Absorb.Resolve(winner, loser, Ent(i), new AbsorbOdds(255, 0));
            if (!a.Minted) continue;
            Assert.Equal(winner.Bytes[..16].ToArray(), a.Result.Bytes[..16].ToArray()); // stats unchanged
            Assert.Equal(77, a.Result.RecessiveGene(TraitCategory.Aura));                // recessive unchanged
            Assert.Equal(255, a.Result.DominantGene(TraitCategory.Aura));                // dominant absorbed
            return;
        }
        Assert.Fail("expected an absorb across 40 entropies at AbsorbChance=255");
    }

    [Fact]
    public void Resolve_HighContinueOdds_CanAbsorbAllBetterTraits()
    {
        // With max absorb + continue odds, k can reach m (all better traits) — the "full fuse" tier.
        var winner = GenomeWith((TraitCategory.Aura, 10), (TraitCategory.Eyes, 10), (TraitCategory.Crest, 10));
        var loser = GenomeWith((TraitCategory.Aura, 255), (TraitCategory.Eyes, 255), (TraitCategory.Crest, 255));
        for (byte i = 0; i < 40; i++)
        {
            var a = Absorb.Resolve(winner, loser, Ent(i), new AbsorbOdds(255, 255));
            if (a.TraitsAbsorbed != 3) continue;
            Assert.Equal(255, a.Result.DominantGene(TraitCategory.Aura));
            Assert.Equal(255, a.Result.DominantGene(TraitCategory.Eyes));
            Assert.Equal(255, a.Result.DominantGene(TraitCategory.Crest));
            return;
        }
        Assert.Fail("expected a full 3-trait concentrate across 40 entropies at odds (255,255)");
    }

    [Fact]
    public void Resolve_OnlyImprovements_NeverDowngrades()
    {
        // The loser is BETTER in Aura but WORSE in Eyes → only Aura is ever a candidate.
        var winner = GenomeWith((TraitCategory.Aura, 10), (TraitCategory.Eyes, 255));
        var loser = GenomeWith((TraitCategory.Aura, 255), (TraitCategory.Eyes, 10));
        for (byte i = 0; i < 40; i++)
        {
            var a = Absorb.Resolve(winner, loser, Ent(i), new AbsorbOdds(255, 255));
            Assert.Equal(255, a.Result.DominantGene(TraitCategory.Eyes)); // Eyes never downgraded to the loser's 10
            Assert.DoesNotContain(TraitCategory.Eyes, a.Absorbed);
        }
    }
}
