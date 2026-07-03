using System.Text;
using ArkadeHeroes.Chain.Covenants;
using NBitcoin.Secp256k1;
using SHA256 = System.Security.Cryptography.SHA256;

namespace ArkadeHeroes.Tests;

public class ArkadeScriptTweakTests
{
    // The regtest emulator's signer key (any valid x-only key works for these tests).
    private const string EmulatorKeyHex = "02999413c46fa10ada5cbc4bcc79a1d09160c2ba3cfc812705d7a13e5e545fb2a9";

    [Fact]
    public void TaggedHashMatchesIndependentComputation()
    {
        // Independent BIP340 tagged-hash: SHA256(SHA256(tag) ‖ SHA256(tag) ‖ msg).
        var message = "arkade heroes covenant"u8.ToArray();
        var tagHash = SHA256.HashData(Encoding.UTF8.GetBytes("ArkScriptHash"));
        var expected = SHA256.HashData(tagHash.Concat(tagHash).Concat(message).ToArray());

        var actual = ArkadeScriptTweak.ComputeScriptHash(message);
        Assert.Equal(Convert.ToHexString(expected), Convert.ToHexString(actual));
    }

    [Fact]
    public void CovenantKeyIsDeterministicAndScriptBound()
    {
        var scriptA = "OP_TRUE"u8.ToArray();
        var scriptB = "OP_FALSE"u8.ToArray();

        var keyA1 = ArkadeScriptTweak.ComputeCovenantPublicKey(EmulatorKeyHex, scriptA);
        var keyA2 = ArkadeScriptTweak.ComputeCovenantPublicKey(EmulatorKeyHex, scriptA);
        var keyB = ArkadeScriptTweak.ComputeCovenantPublicKey(EmulatorKeyHex, scriptB);

        Assert.Equal(keyA1.ToBytes(), keyA2.ToBytes());          // deterministic
        Assert.NotEqual(keyA1.ToBytes(), keyB.ToBytes());        // binds the script
    }

    [Fact]
    public void TweakedKeyEqualsBaseKeyPlusHashTimesG()
    {
        // Cross-check against the raw group math: lift_x(P) + t·G.
        var script = "some covenant"u8.ToArray();
        var scriptHash = ArkadeScriptTweak.ComputeScriptHash(script);

        var baseKey = ECPubKey.Create(Convert.FromHexString(EmulatorKeyHex)).ToXOnlyPubKey();
        var expected = baseKey.AddTweak(scriptHash);

        var actual = ArkadeScriptTweak.ComputeCovenantPublicKey(EmulatorKeyHex, script);
        Assert.Equal(expected.ToBytes(), actual.ToBytes());
    }

    [Fact]
    public void AcceptsXOnlyAndCompressedKeys()
    {
        var script = "x"u8.ToArray();
        var compressed = ArkadeScriptTweak.ComputeCovenantPublicKey(EmulatorKeyHex, script);
        var xOnly = ArkadeScriptTweak.ComputeCovenantPublicKey(EmulatorKeyHex[2..], script);
        Assert.Equal(compressed.ToBytes(), xOnly.ToBytes());
        Assert.Throws<ArgumentException>(() =>
            ArkadeScriptTweak.ComputeCovenantPublicKey("abcd", script));
    }
}
