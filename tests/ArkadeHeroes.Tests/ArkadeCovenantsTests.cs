using ArkadeHeroes.Chain.Covenants;
using NBitcoin;

namespace ArkadeHeroes.Tests;

public class ArkadeCovenantsTests
{
    private static Script SomeP2Tr()
    {
        var program = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        return Script.FromBytesUnsafe([0x51, 0x20, .. program]);
    }

    [Fact]
    public void PayToMatchesTheCoinflipByteLayout()
    {
        // DUP INSPECTOUTPUTSCRIPTPUBKEY 1 EQUALVERIFY <wp32> EQUALVERIFY
        // INSPECTOUTPUTVALUE <amountMinLE> EQUAL
        var script = ArkadeCovenants.PayTo(SomeP2Tr(), 6_000);

        Assert.Equal(0x76, script[0]);            // DUP
        Assert.Equal(0xd1, script[1]);            // INSPECTOUTPUTSCRIPTPUBKEY
        Assert.Equal(0x51, script[2]);            // OP_1 (witness v1)
        Assert.Equal(0x88, script[3]);            // EQUALVERIFY
        Assert.Equal(32, script[4]);              // push 32 (witness program)
        Assert.Equal(0x88, script[37]);           // EQUALVERIFY
        Assert.Equal(0xcf, script[38]);           // INSPECTOUTPUTVALUE
        Assert.Equal(2, script[39]);              // push 2 (6000 = 0x1770 LE)
        Assert.Equal(0x70, script[40]);
        Assert.Equal(0x17, script[41]);
        Assert.Equal(0x87, script[^1]);           // EQUAL
    }

    [Fact]
    public void PayToRejectsNonTaprootAndBadAmounts()
    {
        Assert.Throws<ArgumentException>(() =>
            ArkadeCovenants.PayTo(Script.FromBytesUnsafe([0x00, 0x14]), 1_000));
        Assert.Throws<ArgumentOutOfRangeException>(() => ArkadeCovenants.PayTo(SomeP2Tr(), 0));
    }

    [Fact]
    public void AtomicSweepPrependsTheCrossInputCheck()
    {
        var script = ArkadeCovenants.AtomicSweep(SomeP2Tr(), 10_000, 5_000);

        Assert.Equal(0xc9, script[0]);            // INSPECTINPUTVALUE
        Assert.Equal(2, script[1]);               // push 2 (5000 = 0x1388 LE)
        Assert.Equal(0x88, script[2]);
        Assert.Equal(0x13, script[3]);
        Assert.Equal(0x88, script[4]);            // EQUALVERIFY
        Assert.Equal(0x76, script[5]);            // payTo body starts (DUP)
        Assert.Equal(ArkadeCovenants.PayTo(SomeP2Tr(), 10_000), script[5..]);
    }

    [Fact]
    public void CheckSigFromStackGatePinsMessageAndKey()
    {
        var message = Enumerable.Repeat((byte)3, 32).ToArray();
        var key = Enumerable.Repeat((byte)9, 32).ToArray();
        var script = ArkadeCovenants.CheckSigFromStackGate(message, key);

        Assert.Equal(32, script[0]);              // push message
        Assert.Equal(message, script[1..33]);
        Assert.Equal(32, script[33]);             // push oracle key
        Assert.Equal(key, script[34..66]);
        Assert.Equal(0xcc, script[66]);           // CHECKSIGFROMSTACK
        Assert.Equal(0x69, script[67]);           // VERIFY
    }

    [Fact]
    public void SettleMessageBindsMatchAndBranch()
    {
        var challenger = ArkadeCovenants.SettleMessage("m1", true);
        Assert.Equal(32, challenger.Length);
        Assert.Equal(Convert.ToHexString(challenger),
            Convert.ToHexString(ArkadeCovenants.SettleMessage("m1", true))); // deterministic
        Assert.NotEqual(Convert.ToHexString(challenger),
            Convert.ToHexString(ArkadeCovenants.SettleMessage("m1", false))); // branch-bound
        Assert.NotEqual(Convert.ToHexString(challenger),
            Convert.ToHexString(ArkadeCovenants.SettleMessage("m2", true))); // match-bound
    }

    [Fact]
    public void SettleAuthorizedComposesCsfsThenSeedThenSweep()
    {
        var message = ArkadeCovenants.SettleMessage("m", true);
        var oracle = Enumerable.Repeat((byte)5, 32).ToArray();
        var commitment = Enumerable.Repeat((byte)7, 32).ToArray();
        var script = ArkadeCovenants.SettleAuthorized(message, oracle, commitment, SomeP2Tr(), 10_000, 5_000);

        var gate = ArkadeCovenants.CheckSigFromStackGate(message, oracle);
        Assert.Equal(gate, script[..gate.Length]);
        Assert.Equal(ArkadeCovenants.SettleWithSeed(commitment, SomeP2Tr(), 10_000, 5_000),
            script[gate.Length..]);
    }

    [Fact]
    public void SettleWithSeedComposesGateThenSweep()
    {
        var commitment = Enumerable.Repeat((byte)7, 32).ToArray();
        var script = ArkadeCovenants.SettleWithSeed(commitment, SomeP2Tr(), 10_000, 5_000);

        Assert.Equal(0xa8, script[0]);            // SHA256
        Assert.Equal(32, script[1]);              // push 32 (commitment)
        Assert.Equal(commitment, script[2..34]);
        Assert.Equal(0x88, script[34]);           // EQUALVERIFY
        Assert.Equal(ArkadeCovenants.AtomicSweep(SomeP2Tr(), 10_000, 5_000), script[35..]);
    }

    [Fact]
    public void EncodeIndexIsMinimal()
    {
        Assert.Empty(ArkadeCovenants.EncodeIndex(0));
        Assert.Equal(new byte[] { 0x01 }, ArkadeCovenants.EncodeIndex(1));
        Assert.Equal(new byte[] { 0x80, 0x00 }, ArkadeCovenants.EncodeIndex(128)); // sign-pad
    }

    [Fact]
    public void ArtifactContractBindsEachFunctionToItsOwnTweakedLeaf()
    {
        const string emulatorKey = "02999413c46fa10ada5cbc4bcc79a1d09160c2ba3cfc812705d7a13e5e545fb2a9";
        var server = NBitcoin.Scripting.OutputDescriptor.Parse(
            "tr(" + emulatorKey[2..] + ")", Network.RegTest);

        var contract = new ArkadeArtifactContract("test", server, emulatorKey,
        [
            new ArkadeContractFunction("a", [0x51]),
            new ArkadeContractFunction("b", [0x52]),
        ]);

        Assert.Equal(2, contract.FunctionNames.Count);
        Assert.NotEqual(
            contract.LeafFor("a").BuildScript().ToList()[0].PushData,
            contract.LeafFor("b").BuildScript().ToList()[0].PushData); // distinct tweaks
        Assert.Equal([0x51], contract.ScriptFor("a"));
        Assert.Throws<ArgumentException>(() => contract.ScriptFor("missing"));
    }
}
