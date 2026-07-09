using System.Security.Cryptography;
using ArkadeHeroes.Core.Genetics;

namespace ArkadeHeroes.Core.Progression;

/// <summary>
/// Rarity-derived sterility: the rarer a hero, the more likely it is born sterile, so
/// the rarest lines are self-limiting in supply — a hero sink that needs no genome or
/// covenant change. A pure, DETERMINISTIC function of the genome (which is committed
/// on-chain), so anyone can verify a hero's fertility. Common heroes — including every
/// gen-0 — are always fertile.
/// </summary>
public static class Sterility
{
    /// <summary>Sterility chance (percent) by rarity tier.</summary>
    public static int ChancePercent(RarityTier tier, GameConfig? config = null)
    {
        var s = (config ?? GameConfig.Default).Sterility;
        return tier switch
        {
            RarityTier.Legendary => s.Legendary,
            RarityTier.Epic => s.Epic,
            RarityTier.Rare => s.Rare,
            RarityTier.Uncommon => s.Uncommon,
            _ => 0, // Common (incl. all gen-0) → always fertile
        };
    }

    /// <summary>Whether the hero with this genome is born sterile (cannot breed) — deterministic and verifiable from the committed genome.</summary>
    public static bool IsSterile(Genome genome, GameConfig? config = null)
    {
        var chance = ChancePercent(Rarity.Of(genome, config).Tier, config);
        if (chance == 0) return false;
        Span<byte> preimage = stackalloc byte[Genome.Size + 1];
        genome.Bytes.CopyTo(preimage);
        preimage[^1] = 0x53; // 'S' — domain-separate from other genome-derived rolls
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(preimage, hash);
        return BitConverter.ToUInt32(hash[..4]) % 100 < (uint)chance;
    }
}
