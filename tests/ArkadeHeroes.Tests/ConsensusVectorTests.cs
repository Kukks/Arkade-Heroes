using ArkadeHeroes.Core.Fairness;

namespace ArkadeHeroes.Tests;

/// <summary>
/// Golden vectors for the consensus artifacts every verifier re-derives. These pin the exact BYTES a
/// commitment/entropy derivation produces, so any future change to the preimage — a reordered field, a
/// different length prefix, an endianness flip — fails loudly here instead of silently splitting the server
/// from the clients that must reproduce it (and invalidating every receipt and replay already issued).
/// </summary>
public class ConsensusVectorTests
{
    // A fixed 32-byte seed: bytes 0..31. Any deterministic seed works; the point is that the OUTPUT is pinned.
    private static byte[] Seed()
    {
        var s = new byte[32];
        for (var i = 0; i < s.Length; i++) s[i] = (byte)i;
        return s;
    }

    [Fact]
    public void DeriveEntropy_MatchesItsGoldenVector()
    {
        // Multi-part derivation exercises the length prefix between parts — the field whose byte order must be
        // fixed (little-endian) rather than platform-dependent, since a big-endian verifier would otherwise
        // derive different entropy and reject every honest match.
        // The expected value was derived INDEPENDENTLY of this code (seed ‖ [4-byte LE length ‖ UTF-8 part]…,
        // then SHA-256), so it pins the wire format rather than blessing whatever the implementation emits.
        var entropy = CommitReveal.DeriveEntropy(Seed(), "match-1", "hero-a", "hero-b", "nonce-xyz");
        Assert.Equal("8d3583f6ee2801f25f1343ce54082aa8a8d3e245ff3c8a5944b727a35d5b2be2",
            Convert.ToHexString(entropy).ToLowerInvariant());
    }

    [Fact]
    public void DeriveEntropy_LengthPrefixSeparatesParts()
    {
        // The prefix exists so distinct part lists can't collide: ("ab","c") and ("a","bc") concatenate to the
        // same bytes but MUST derive different entropy. This is what the length field buys — pinned here so a
        // "simplification" that drops it is caught.
        var seed = Seed();
        Assert.NotEqual(
            Convert.ToHexString(CommitReveal.DeriveEntropy(seed, "ab", "c")),
            Convert.ToHexString(CommitReveal.DeriveEntropy(seed, "a", "bc")));
    }

    [Fact]
    public void Commit_MatchesItsGoldenVector()
    {
        // The commitment a player is shown BEFORE the reveal — the anchor of the whole fairness story.
        // SHA-256 of the 32 seed bytes, lowercase hex — computed independently, like the vector above.
        Assert.Equal("630dcd2966c4336691125448bbb25b4ff412a49c732db2c8abc1b8581bd710dd",
            CommitReveal.Commit(Seed()));
        Assert.True(CommitReveal.Verify(Seed(), CommitReveal.Commit(Seed())));   // and it round-trips
    }
}
