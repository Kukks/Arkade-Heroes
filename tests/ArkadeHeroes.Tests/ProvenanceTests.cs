using ArkadeHeroes.Core.Progression;

namespace ArkadeHeroes.Tests;

/// <summary>The pedigree ladder is a pure function of generation — gen-0 is a Founder (null here), then
/// Scion (1–2) → Heir (3–4) → Dynasty (5+). Deterministic + client-verifiable.</summary>
public class ProvenanceTests
{
    [Theory]
    [InlineData(0, null)]
    [InlineData(1, "Scion")]
    [InlineData(2, "Scion")]
    [InlineData(3, "Heir")]
    [InlineData(4, "Heir")]
    [InlineData(5, "Dynasty")]
    [InlineData(12, "Dynasty")]
    public void Pedigree_LaddersByGeneration(int generation, string? expected)
        => Assert.Equal(expected, Provenance.Pedigree(generation));
}
