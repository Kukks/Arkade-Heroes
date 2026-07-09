using System.Text;
using ArkadeHeroes.Core;
using ArkadeHeroes.Core.Fairness;
using ArkadeHeroes.Core.Progression;
using NBitcoin.Secp256k1;
using SHA256 = System.Security.Cryptography.SHA256;

namespace ArkadeHeroes.Shared;

/// <summary>
/// A server-signed, player-held progression fact. Receipts make progression
/// portable and independently verifiable: the signature binds the outcome to
/// the game's public key, the embedded commit–reveal proof lets anyone re-run
/// the derivation, and a hero's level is recomputable from its receipt chain —
/// the server database is just a cache of receipt-provable state.
/// </summary>
public record ProgressionReceiptDto(
    string Type,            // "match" | "breeding" | "merge"
    string Id,              // matchId / breedingId / mergeId
    string HeroAId,         // challenger / parentA / base
    string HeroBId,         // defender / parentB / sacrifice
    string? ResultHeroId,   // winner (match) / child (breeding) / fused (merge)
    string ServerSeedHex,
    string Nonce,
    string CommitmentHex,
    long XpAwardA,
    long XpAwardB,
    int LevelA,             // resulting levels after the event
    int LevelB,
    long UnixSeconds,
    string GameSignerKeyHex,
    string SignatureHex,
    int ConfigVersion = 0);  // PINNED config version this fact was made under — ReplayLevel folds it under this version's curve

/// <summary>Canonical payload + BIP340 signing/verification + level replay for receipts.</summary>
public static class ReceiptVerifier
{
    public const string PayloadTag = "arkade-heroes-receipt-v1";

    /// <summary>The exact bytes the signature covers — order and framing are part of the protocol.</summary>
    public static byte[] CanonicalPayload(ProgressionReceiptDto receipt)
    {
        var text = string.Join('|',
            PayloadTag, receipt.Type, receipt.Id,
            receipt.HeroAId, receipt.HeroBId, receipt.ResultHeroId ?? "",
            receipt.ServerSeedHex, receipt.Nonce, receipt.CommitmentHex,
            receipt.XpAwardA, receipt.XpAwardB, receipt.LevelA, receipt.LevelB,
            receipt.UnixSeconds, receipt.ConfigVersion);
        return SHA256.HashData(Encoding.UTF8.GetBytes(text));
    }

    public static string Sign(ProgressionReceiptDto unsigned, ECPrivKey gameKey)
    {
        var signature = gameKey.SignBIP340(CanonicalPayload(unsigned));
        Span<byte> bytes = stackalloc byte[64];
        signature.WriteToSpan(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>Full verification: signature by the advertised game key, and the commit–reveal chain holds.</summary>
    public static (bool Ok, string Detail) Verify(ProgressionReceiptDto receipt)
    {
        try
        {
            var keyBytes = Convert.FromHexString(receipt.GameSignerKeyHex);
            var xOnly = keyBytes.Length == 33 ? keyBytes[1..] : keyBytes;
            if (!ECXOnlyPubKey.TryCreate(xOnly, out var pubKey) || pubKey is null)
                return (false, "invalid game signer key");
            if (!SecpSchnorrSignature.TryCreate(Convert.FromHexString(receipt.SignatureHex), out var signature) || signature is null)
                return (false, "malformed signature");
            if (!pubKey.SigVerifyBIP340(signature, CanonicalPayload(receipt)))
                return (false, "signature does not verify — receipt was tampered with or forged");

            if (!CommitReveal.Verify(Convert.FromHexString(receipt.ServerSeedHex), receipt.CommitmentHex))
                return (false, "revealed seed does not match the commitment");

            return (true, "signature and commit–reveal both verify");
        }
        catch (Exception ex)
        {
            return (false, $"verification error: {ex.Message}");
        }
    }

    /// <summary>
    /// Replays a hero's match receipts (timestamp order) into its expected
    /// level — progression recomputed from player-held facts alone.
    /// </summary>
    public static int ReplayLevel(string heroId, IEnumerable<ProgressionReceiptDto> receipts,
        IReadOnlyDictionary<int, GameConfig>? configsByVersion = null)
    {
        var all = receipts as IReadOnlyCollection<ProgressionReceiptDto> ?? receipts.ToList();
        // A merged hero inherits its base's level at genesis, attested by its merge receipt
        // (LevelA = the base's level). Non-merged heroes have no such receipt and start at 1.
        var genesis = all.FirstOrDefault(r => (r.Type == "merge" || r.Type == "absorb") && r.ResultHeroId == heroId);
        var level = genesis?.LevelA ?? 1;
        long xp = 0;
        foreach (var receipt in all
                     .Where(r => r.Type == "match" && (r.HeroAId == heroId || r.HeroBId == heroId))
                     .OrderBy(r => r.UnixSeconds)
                     .ThenBy(r => r.Id, StringComparer.Ordinal))
        {
            var award = receipt.HeroAId == heroId ? receipt.XpAwardA : receipt.XpAwardB;
            // Fold each receipt under the CURVE it was created with — pinned per receipt, so a
            // later curve retune can't retroactively re-level heroes. Unknown version → Default.
            var cfg = configsByVersion is not null && configsByVersion.TryGetValue(receipt.ConfigVersion, out var c)
                ? c : GameConfig.Default;
            (level, xp, _) = Leveling.Apply(level, xp, award, cfg);
        }
        return level;
    }
}
