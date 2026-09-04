using System.Text;
using ArkadeHeroes.Chain.Covenants;
using NArk.Abstractions.Extensions;
using NArk.Arkade.Crypto;
using NBitcoin;
using NBitcoin.Scripting;
using NBitcoin.Secp256k1;
using SHA256 = System.Security.Cryptography.SHA256;

namespace ArkadeHeroes.Tests;

/// <summary>
/// The covenant key binding, checked against the group math rather than against the SDK's
/// own view of it. Every covenant address in the game is this tweak: if it drifts, funds
/// go to a key the emulator will not sign for.
/// </summary>
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

        var actual = ArkadeTweak.ComputeScriptHash(message);
        Assert.Equal(Convert.ToHexString(expected), Convert.ToHexString(actual));
    }

    [Fact]
    public void TweakedKeyEqualsBaseKeyPlusHashTimesG()
    {
        // Cross-check against the raw group math: lift_x(P) + t·G.
        var script = "some covenant"u8.ToArray();
        var baseKey = ECPubKey.Create(Convert.FromHexString(EmulatorKeyHex)).ToXOnlyPubKey();
        var expected = baseKey.AddTweak(ArkadeTweak.ComputeScriptHash(script));

        var actual = ArkadeTweak.Tweak(ECPubKey.Create(Convert.FromHexString(EmulatorKeyHex)), script);
        Assert.Equal(expected.ToXOnlyPubKey().ToBytes(), actual.ToBytes());
    }

    [Fact]
    public void ContractAddressIsDeterministicAndScriptBound()
    {
        var a1 = Contract("OP_TRUE"u8.ToArray()).GetArkAddress().ToString(false);
        var a2 = Contract("OP_TRUE"u8.ToArray()).GetArkAddress().ToString(false);
        var b = Contract("OP_FALSE"u8.ToArray()).GetArkAddress().ToString(false);

        Assert.Equal(a1, a2);
        Assert.NotEqual(a1, b);
    }

    [Fact]
    public void ContractAcceptsXOnlyAndCompressedEmulatorKeys()
    {
        var script = "x"u8.ToArray();
        Assert.Equal(
            Contract(script, EmulatorKeyHex).GetArkAddress().ToString(false),
            Contract(script, EmulatorKeyHex[2..]).GetArkAddress().ToString(false));

        Assert.Throws<ArgumentException>(() => Contract(script, "abcd"));
    }

    private static ArkadeArtifactContract Contract(byte[] script, string emulatorKeyHex = EmulatorKeyHex)
        => new("test", ServerKey(), emulatorKeyHex, [new ArkadeContractFunction("fn", script)]);

    private static OutputDescriptor ServerKey() => KeyExtensions.ParseOutputDescriptor(
        "03aad52d58162e9eefeafc7ad8a1cdca8060b5f01df1e7583362d052e266208f88", Network.RegTest);
}
