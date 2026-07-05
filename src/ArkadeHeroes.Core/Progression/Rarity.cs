using ArkadeHeroes.Core.Genetics;

namespace ArkadeHeroes.Core.Progression;

/// <summary>A hero's rarity: a visible tier from expressed traits, plus carried recessives (breeding potential). Pure function of the genome — recomputable + verifiable by anyone.</summary>
public readonly record struct RarityResult(
    int Score,
    RarityTier Tier,
    IReadOnlyList<TraitVariant> Expressed,
    IReadOnlyList<TraitVariant> CarriedRecessives);

public static class Rarity
{
    public static RarityResult Of(Genome genome)
    {
        var expressed = Traits.Expressed(genome);
        var recessives = Traits.Recessives(genome);
        var score = expressed.Sum(t => Traits.WeightOf(t.Value));
        // Visible tier = the highest expressed trait's tier (a hero is as rare as its
        // rarest showing trait); no expressed traits → Common.
        var tier = expressed.Count == 0 ? RarityTier.Common : expressed.Max(t => t.Tier);
        return new RarityResult(score, tier, expressed, recessives);
    }
}
