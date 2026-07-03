using System.Buffers.Binary;

namespace ArkadeHeroes.Core;

/// <summary>
/// xoshiro256** seeded from 32 bytes. Used everywhere gameplay needs randomness
/// (combat rolls, crits, dodges) so that a match seeded by commit–reveal entropy
/// replays identically on any machine — <see cref="Random"/> is avoided because
/// its seeded algorithm is not guaranteed stable across .NET versions.
/// </summary>
public sealed class DeterministicRng
{
    private ulong _s0, _s1, _s2, _s3;

    public DeterministicRng(ReadOnlySpan<byte> seed)
    {
        if (seed.Length != 32)
            throw new ArgumentException("Seed must be exactly 32 bytes.", nameof(seed));
        _s0 = BinaryPrimitives.ReadUInt64LittleEndian(seed[..8]);
        _s1 = BinaryPrimitives.ReadUInt64LittleEndian(seed[8..16]);
        _s2 = BinaryPrimitives.ReadUInt64LittleEndian(seed[16..24]);
        _s3 = BinaryPrimitives.ReadUInt64LittleEndian(seed[24..32]);
        // xoshiro must not start from the all-zero state.
        if ((_s0 | _s1 | _s2 | _s3) == 0)
            _s3 = 0x9E3779B97F4A7C15UL;
    }

    public ulong NextUInt64()
    {
        var result = System.Numerics.BitOperations.RotateLeft(_s1 * 5, 7) * 9;
        var t = _s1 << 17;
        _s2 ^= _s0;
        _s3 ^= _s1;
        _s1 ^= _s2;
        _s0 ^= _s3;
        _s2 ^= t;
        _s3 = System.Numerics.BitOperations.RotateLeft(_s3, 45);
        return result;
    }

    /// <summary>Uniform integer in [0, exclusiveMax) via rejection sampling (no modulo bias).</summary>
    public int Next(int exclusiveMax)
    {
        if (exclusiveMax <= 0) throw new ArgumentOutOfRangeException(nameof(exclusiveMax));
        var bound = (ulong)exclusiveMax;
        var threshold = ulong.MaxValue - ulong.MaxValue % bound;
        ulong roll;
        do { roll = NextUInt64(); } while (roll >= threshold);
        return (int)(roll % bound);
    }

    /// <summary>Percentage roll: true with probability <paramref name="percent"/>/100.</summary>
    public bool Chance(int percent) => Next(100) < Math.Clamp(percent, 0, 100);
}
