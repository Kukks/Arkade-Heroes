using System.Security.Cryptography;
using System.Text;

namespace ArkadeHeroes.Core.Fairness;

/// <summary>
/// Commit–reveal helpers for provably fair randomness, following the pattern the
/// ArkadeKitties design doc describes for breeding entropy and coinflip-style
/// games use for match outcomes: the server commits to a secret seed up front,
/// players contribute nonces, and after the action the server reveals the seed
/// so anyone can re-derive the entropy and verify the result.
/// </summary>
public static class CommitReveal
{
    /// <summary>Generates a fresh 32-byte server seed.</summary>
    public static byte[] NewSeed() => RandomNumberGenerator.GetBytes(32);

    /// <summary>The public commitment to a seed: SHA256(seed), hex-encoded.</summary>
    public static string Commit(ReadOnlySpan<byte> seed)
        => Convert.ToHexString(SHA256.HashData(seed)).ToLowerInvariant();

    /// <summary>Verifies a revealed seed against its prior commitment.</summary>
    public static bool Verify(ReadOnlySpan<byte> revealedSeed, string commitment)
        => string.Equals(Commit(revealedSeed), commitment, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Derives 32 bytes of entropy from the revealed server seed plus any number
    /// of context parts (parent asset ids, player nonces, match ids…).
    /// Parts are length-prefixed so distinct part lists can't collide.
    /// </summary>
    public static byte[] DeriveEntropy(ReadOnlySpan<byte> serverSeed, params string[] parts)
    {
        using var buffer = new MemoryStream();
        buffer.Write(serverSeed);
        foreach (var part in parts)
        {
            var bytes = Encoding.UTF8.GetBytes(part);
            Span<byte> len = stackalloc byte[4];
            BitConverter.TryWriteBytes(len, bytes.Length);
            buffer.Write(len);
            buffer.Write(bytes);
        }
        return SHA256.HashData(buffer.ToArray());
    }
}
