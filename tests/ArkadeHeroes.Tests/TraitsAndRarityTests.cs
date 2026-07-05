using System.Net.Http.Json;
using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Heroes;
using ArkadeHeroes.Server;
using ArkadeHeroes.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace ArkadeHeroes.Tests;

public class TraitsAndRarityTests
{
    // Builds a genome with a single trait: dominant byte for `cat` set to `value`.
    private static Genome GenomeWithTrait(TraitCategory cat, byte value)
    {
        var bytes = new byte[32];
        bytes[16 + (int)cat * 2] = value; // dominant gene of the category
        return new Genome(bytes);
    }

    [Fact]
    public void TierBandsMapByteValueToTiers()
    {
        Assert.Equal(RarityTier.Common, Traits.TierOf(0));      // none/plain reads as Common floor
        Assert.Equal(RarityTier.Common, Traits.TierOf(100));
        Assert.Equal(RarityTier.Uncommon, Traits.TierOf(220));
        Assert.Equal(RarityTier.Rare, Traits.TierOf(245));
        Assert.Equal(RarityTier.Epic, Traits.TierOf(253));
        Assert.Equal(RarityTier.Legendary, Traits.TierOf(255));
    }

    [Fact]
    public void ExpressedReadsDominantNonZeroTraits()
    {
        var g = GenomeWithTrait(TraitCategory.Aura, 255);
        var expressed = Traits.Expressed(g);
        Assert.Single(expressed);
        Assert.Equal(TraitCategory.Aura, expressed[0].Category);
        Assert.Equal(RarityTier.Legendary, expressed[0].Tier);
    }

    [Fact]
    public void AffinityCategoriesAreFlagged()
    {
        Assert.True(Traits.IsAffinity(TraitCategory.ElementAffinity));
        Assert.True(Traits.IsAffinity(TraitCategory.Temperament));
        Assert.False(Traits.IsAffinity(TraitCategory.Aura));
    }

    private static Genome Blank() => new(new byte[32]);

    private static Genome WithTraitGenes(TraitCategory cat, byte dom, byte rec)
    {
        var b = new byte[32];
        b[16 + (int)cat * 2] = dom;
        b[16 + (int)cat * 2 + 1] = rec;
        return new Genome(b);
    }

    [Fact]
    public void Mix_IsDeterministic_AcrossTraitBytes()
    {
        var a = WithTraitGenes(TraitCategory.Aura, 255, 0);
        var b = WithTraitGenes(TraitCategory.Crest, 250, 0);
        var entropy = new byte[32];
        for (byte i = 0; i < 32; i++) entropy[i] = (byte)(i * 7 + 3);

        var child1 = GeneMixer.Mix(a, b, entropy);
        var child2 = GeneMixer.Mix(a, b, entropy);
        Assert.Equal(child1.ToHex(), child2.ToHex()); // byte-identical — covenant-critical
    }

    [Fact]
    public void Mix_InheritsAnExpressedTrait_FromAParent()
    {
        // A parent expressing a Legendary Aura should pass it to at least one child
        // across a spread of entropy (dominant-favored inheritance).
        var a = WithTraitGenes(TraitCategory.Aura, 255, 0);
        var b = Blank();
        var passed = false;
        for (var seed = 0; seed < 40 && !passed; seed++)
        {
            var e = new byte[32];
            for (var i = 0; i < 32; i++) e[i] = (byte)(seed * 31 + i);
            var child = GeneMixer.Mix(a, b, e);
            if (child.DominantGene(TraitCategory.Aura) == 255) passed = true;
        }
        Assert.True(passed, "an expressed Legendary should inherit under some entropy");
    }

    [Fact]
    public void Mix_TwoBlankParents_MostlyStayBlank_ButMutationCanIntroduce()
    {
        // Blank parents: children are usually blank on traits, but a rare mutation
        // can introduce a new trait — proving mutation is the only source of new rarity.
        var a = Blank();
        var b = Blank();
        var introduced = 0;
        for (var seed = 0; seed < 300; seed++)
        {
            var e = new byte[32];
            for (var i = 0; i < 32; i++) e[i] = (byte)(seed * 13 + i * 5);
            var child = GeneMixer.Mix(a, b, e);
            if (Traits.Expressed(child).Count > 0) introduced++;
        }
        Assert.True(introduced is > 0 and < 150,
            $"mutation should introduce traits rarely, not never or always (got {introduced}/300)");
    }

    [Fact]
    public void Rarity_ScoresExpressedTraits_AndPicksTierFromHighest()
    {
        var g = WithTraitGenes(TraitCategory.Aura, 255, 0);   // Legendary expressed
        var r = ArkadeHeroes.Core.Progression.Rarity.Of(g);
        Assert.Equal(RarityTier.Legendary, r.Tier);
        Assert.True(r.Score >= 50);
        Assert.Single(r.Expressed);
    }

    [Fact]
    public void Rarity_CarriedRecessives_DoNotInflateTheVisibleTier()
    {
        // Plain dominant, Legendary recessive: visible tier stays Common, but the
        // recessive is reported as breeding potential.
        var g = WithTraitGenes(TraitCategory.Aura, 0, 255);
        var r = ArkadeHeroes.Core.Progression.Rarity.Of(g);
        Assert.Equal(RarityTier.Common, r.Tier);
        Assert.Empty(r.Expressed);
        Assert.Single(r.CarriedRecessives);
        Assert.Equal(RarityTier.Legendary, r.CarriedRecessives[0].Tier);
    }

    [Fact]
    public void Rarity_IsMonotonic_InExpressedRarity()
    {
        var common = ArkadeHeroes.Core.Progression.Rarity.Of(WithTraitGenes(TraitCategory.Aura, 100, 0));
        var epic = ArkadeHeroes.Core.Progression.Rarity.Of(WithTraitGenes(TraitCategory.Aura, 253, 0));
        Assert.True(epic.Score > common.Score);
    }

    [Fact]
    public void AffinityModifier_IsCappedAndDerivedFromAffinityTraits()
    {
        Assert.Equal(1.0, Traits.AffinityModifier(Blank())); // no affinities → no nudge

        // Two Legendary affinities → at the cap, never above.
        var b = new byte[32];
        b[16 + (int)TraitCategory.ElementAffinity * 2] = 255;
        b[16 + (int)TraitCategory.Temperament * 2] = 255;
        var maxed = Traits.AffinityModifier(new Genome(b));
        Assert.True(maxed > 1.0 && maxed <= 1.05, $"expected (1.0, 1.05], got {maxed}");

        // A cosmetic legendary does NOT move it.
        var cosmetic = new byte[32];
        cosmetic[16 + (int)TraitCategory.Aura * 2] = 255;
        Assert.Equal(1.0, Traits.AffinityModifier(new Genome(cosmetic)));
    }

    [Fact]
    public void ToDto_PopulatesRarity_FromTheGenome()
    {
        var b = new byte[32];
        b[16 + (int)TraitCategory.Aura * 2] = 255; // Legendary Aura
        var hero = new Hero
        {
            Id = "h", Name = "Rare One", OwnerId = "p",
            Genome = new Genome(b), Generation = 1, Level = 1,
        };
        var dto = ArkadeHeroes.Server.DtoMapper.ToDto(hero);
        Assert.NotNull(dto.Rarity);
        Assert.Equal("Legendary", dto.Rarity!.Tier);
        Assert.Contains(dto.Rarity.Expressed, t => t.Category == "Aura");
    }

    [Fact]
    public async Task RarestEndpoint_RanksHeroesByRarityScore()
    {
        using var factory = new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>();
        var client = factory.CreateClient();
        var store = factory.Services.GetRequiredService<GameStore>();

        // A plain hero and a Legendary-Aura hero, injected straight into the store.
        store.Heroes["plain"] = new Hero
            { Id = "plain", OwnerId = "p", Name = "Plain", Genome = new Genome(new byte[32]), Generation = 1 };
        var g = new byte[32];
        g[16 + (int)TraitCategory.Aura * 2] = 255; // Legendary Aura
        store.Heroes["rare"] = new Hero
            { Id = "rare", OwnerId = "p", Name = "Rarest", Genome = new Genome(g), Generation = 1 };

        var board = (await client.GetFromJsonAsync<List<HeroDto>>("/api/rarest"))!;
        Assert.Equal("rare", board[0].Id); // the legendary sits on top
        Assert.Equal("Legendary", board[0].Rarity!.Tier);
    }
}
