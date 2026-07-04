using ArkadeHeroes.Chain.Covenants;

namespace ArkadeHeroes.Tests;

/// <summary>
/// The breed escrow's shared, pure pieces: the child metadata list must be
/// deterministic (same inputs → byte-identical list → identical Merkle root),
/// because the breeding oracle signs that root and the on-chain child group
/// carries the same list. Any reordering or key drift would make the covenant
/// reject an honest mint.
/// </summary>
public class BreedEscrowContractsTests
{
    private static (string genome, int gen, string pa, string pb, string seed, string nonce) Sample() =>
        ("cafebabe0001", 1, "aa00", "bb11", "5eed", "n0nce");

    [Fact]
    public void ChildMetadata_IsDeterministic_AndRootStable()
    {
        var s = Sample();
        var a = BreedEscrowContracts.ChildMetadata(s.genome, s.gen, s.pa, s.pb, s.seed, s.nonce);
        var b = BreedEscrowContracts.ChildMetadata(s.genome, s.gen, s.pa, s.pb, s.seed, s.nonce);

        // Same inputs → byte-identical entries in the same order.
        Assert.Equal(a.Count, b.Count);
        for (var i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].Key, b[i].Key);
            Assert.Equal(a[i].Value, b[i].Value);
        }

        // And an identical Merkle root — the exact bytes the oracle signs.
        var rootA = ArkadeCovenants.MetadataMerkleRoot(a);
        var rootB = ArkadeCovenants.MetadataMerkleRoot(b);
        Assert.Equal(Convert.ToHexString(rootA), Convert.ToHexString(rootB));
        Assert.Equal(32, rootA.Length);
    }

    [Fact]
    public void ChildMetadata_CarriesTheGenomeAndLineage()
    {
        var s = Sample();
        var md = BreedEscrowContracts.ChildMetadata(s.genome, s.gen, s.pa, s.pb, s.seed, s.nonce);
        var keys = md.Select(m => System.Text.Encoding.UTF8.GetString(m.Key)).ToList();
        Assert.Equal(["game", "genome", "generation", "parentA", "parentB", "serverSeed", "nonce"], keys);
        Assert.Equal(s.genome, System.Text.Encoding.UTF8.GetString(md[1].Value));
    }

    [Fact]
    public void DifferentGenome_ChangesTheRoot()
    {
        var s = Sample();
        var root1 = ArkadeCovenants.MetadataMerkleRoot(
            BreedEscrowContracts.ChildMetadata(s.genome, s.gen, s.pa, s.pb, s.seed, s.nonce));
        var root2 = ArkadeCovenants.MetadataMerkleRoot(
            BreedEscrowContracts.ChildMetadata("deadbeef9999", s.gen, s.pa, s.pb, s.seed, s.nonce));
        Assert.NotEqual(Convert.ToHexString(root1), Convert.ToHexString(root2));
    }
}
