using ArkadeHeroes.Core.Heroes;

namespace ArkadeHeroes.Core.Genetics;

public sealed record BreedingPolicy(TimeSpan CooldownBaseUnit)
{
    public static readonly BreedingPolicy Default = new(TimeSpan.FromMinutes(1));

    /// <summary>
    /// Cooldown doubles with each breed (capped at 2^7 units) and is scaled by
    /// the hero's cooldown gene — a breedable trait, exactly like ArkadeKitties'
    /// cooldown byte.
    /// </summary>
    public TimeSpan CooldownAfterBreed(int breedCount, byte cooldownGene)
    {
        var doubling = Math.Pow(2, Math.Min(breedCount, 7));
        var geneScale = 1 + cooldownGene / 128.0; // 1.0 .. ~3.0
        return CooldownBaseUnit * (doubling * geneScale);
    }
}

public sealed record BreedingOutcome(
    Genome ChildGenome,
    int ChildGeneration,
    string ChildName,
    TimeSpan ParentACooldown,
    TimeSpan ParentBCooldown);

/// <summary>
/// Pure breeding rules: validation + deterministic child derivation. Chain
/// anchoring (minting the child as an Arkade asset) and persistence live above
/// this layer.
/// </summary>
public static class BreedingService
{
    public static string? Validate(Hero parentA, Hero parentB, DateTimeOffset now)
    {
        if (parentA.Id == parentB.Id) return "A hero cannot breed with itself.";
        if (parentA.IsOnBreedCooldown(now)) return $"{parentA.Name} is still on breeding cooldown.";
        if (parentB.IsOnBreedCooldown(now)) return $"{parentB.Name} is still on breeding cooldown.";
        return null;
    }

    public static BreedingOutcome Breed(
        Hero parentA, Hero parentB, ReadOnlySpan<byte> entropy, BreedingPolicy? policy = null)
    {
        policy ??= BreedingPolicy.Default;

        var childGenome = GeneMixer.Mix(parentA.Genome, parentB.Genome, entropy);
        var generation = GeneMixer.ChildGeneration(parentA.Generation, parentB.Generation);

        return new BreedingOutcome(
            childGenome,
            generation,
            HeroNamer.DeriveName(childGenome),
            policy.CooldownAfterBreed(parentA.BreedCount, parentA.Genome.CooldownGene),
            policy.CooldownAfterBreed(parentB.BreedCount, parentB.Genome.CooldownGene));
    }
}
