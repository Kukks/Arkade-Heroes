using System.Security.Cryptography;
using System.Text;
using NBitcoin.Secp256k1;

namespace ArkadeHeroes.Chain.Covenants;

/// <summary>
/// The Arkade Script covenant key-binding primitive, ported from
/// arkade-os/emulator <c>pkg/arkade/tweak.go</c>:
///
///   scriptHash  = tagged_hash("ArkScriptHash", script)
///   tweakedKey  = lift_x(emulatorKey) + scriptHash·G
///
/// A covenant tapleaf is an ordinary multisig over the <em>tweaked</em>
/// emulator key; because only the emulator can derive the matching private
/// key (and it only signs after executing the script successfully), the
/// tweak is what binds the leaf to one specific Arkade Script.
/// </summary>
public static class ArkadeScriptTweak
{
    private const string ScriptHashTag = "ArkScriptHash";

    /// <summary>BIP340-style tagged hash: SHA256(SHA256(tag) ‖ SHA256(tag) ‖ data).</summary>
    public static byte[] TaggedHash(string tag, ReadOnlySpan<byte> data)
    {
        var tagHash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(tag));
        var buffer = new byte[tagHash.Length * 2 + data.Length];
        tagHash.CopyTo(buffer, 0);
        tagHash.CopyTo(buffer, tagHash.Length);
        data.CopyTo(buffer.AsSpan(tagHash.Length * 2));
        return System.Security.Cryptography.SHA256.HashData(buffer);
    }

    /// <summary>Hash of an Arkade Script under the covenant tag.</summary>
    public static byte[] ComputeScriptHash(ReadOnlySpan<byte> arkadeScript)
        => TaggedHash(ScriptHashTag, arkadeScript);

    /// <summary>
    /// Computes the script-bound covenant public key:
    /// <c>lift_x(emulatorKey) + scriptHash·G</c>.
    /// </summary>
    public static ECPubKey ComputeCovenantPublicKey(ECXOnlyPubKey emulatorKey, ReadOnlySpan<byte> arkadeScript)
    {
        var scriptHash = ComputeScriptHash(arkadeScript);
        // ECXOnlyPubKey.AddTweak is exactly lift_x(P) + t·G (taproot-style tweak-add).
        return emulatorKey.AddTweak(scriptHash);
    }

    /// <summary>Convenience overload for a 33-byte compressed or 32-byte x-only emulator key (hex).</summary>
    public static ECPubKey ComputeCovenantPublicKey(string emulatorKeyHex, ReadOnlySpan<byte> arkadeScript)
    {
        var keyBytes = Convert.FromHexString(emulatorKeyHex);
        var xOnly = keyBytes.Length switch
        {
            32 => ECXOnlyPubKey.Create(keyBytes),
            33 => ECPubKey.Create(keyBytes).ToXOnlyPubKey(),
            _ => throw new ArgumentException($"Expected 32 or 33 byte key, got {keyBytes.Length}.", nameof(emulatorKeyHex)),
        };
        return ComputeCovenantPublicKey(xOnly, arkadeScript);
    }
}
