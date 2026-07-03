using System.Security.Cryptography;
using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Heroes;

namespace ArkadeHeroes.Tests;

public class BreedingServiceTests
{
    private static Hero MakeHero(string id, int generation = 0, byte cooldownGene = 0)
    {
        var bytes = new byte[32];
        Array.Fill(bytes, (byte)100, 0, 16);
        bytes[13] = cooldownGene;
        var genome = new Genome(bytes);
        return new Hero
        {
            Id = id,
            OwnerId = "tester",
            Name = "Test",
            Genome = genome,
            Generation = generation,
        };
    }

    [Fact]
    public void SelfBreedingIsRejected()
    {
        var hero = MakeHero("x");
        Assert.NotNull(BreedingService.Validate(hero, hero, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void CooldownBlocksBreeding()
    {
        var now = DateTimeOffset.UtcNow;
        var a = MakeHero("a");
        var b = MakeHero("b");
        b.BreedCooldownUntil = now.AddMinutes(5);
        Assert.NotNull(BreedingService.Validate(a, b, now));
        Assert.Null(BreedingService.Validate(a, b, now.AddMinutes(6)));
    }

    [Fact]
    public void BreedProducesDeterministicChildAndCooldowns()
    {
        var a = MakeHero("a", generation: 2);
        var b = MakeHero("b", generation: 4);
        var entropy = SHA256.HashData([5]);

        var o1 = BreedingService.Breed(a, b, entropy);
        var o2 = BreedingService.Breed(a, b, entropy);

        Assert.Equal(o1.ChildGenome, o2.ChildGenome);
        Assert.Equal(5, o1.ChildGeneration);
        Assert.False(string.IsNullOrWhiteSpace(o1.ChildName));
        Assert.True(o1.ParentACooldown > TimeSpan.Zero);
    }

    [Fact]
    public void CooldownGrowsWithBreedCountAndGene()
    {
        var policy = new BreedingPolicy(TimeSpan.FromMinutes(1));
        var fresh = policy.CooldownAfterBreed(0, 0);
        var veteran = policy.CooldownAfterBreed(5, 0);
        var slowGene = policy.CooldownAfterBreed(0, 255);

        Assert.True(veteran > fresh);
        Assert.True(slowGene > fresh);
        // Doubling caps at 2^7.
        Assert.Equal(policy.CooldownAfterBreed(7, 0), policy.CooldownAfterBreed(30, 0));
    }
}
