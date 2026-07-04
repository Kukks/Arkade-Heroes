using NBitcoin;

namespace ArkadeHeroes.Chain.Covenants;

/// <summary>
/// Arkade Script covenant bytecode builders — byte-for-byte ports of the
/// coinflip production covenants (packages/contract-workflows-prototype/src/
/// covenants.ts), verified against the emulator's opcode implementations.
/// </summary>
public static class ArkadeCovenants
{
    // Emulator VM introspection opcodes (emulator/pkg/arkade/opcode.go).
    private const byte OpInspectInputValue = 0xc9;
    private const byte OpInspectOutputValue = 0xcf;
    private const byte OpInspectOutputScriptPubkey = 0xd1;

    private const byte OpDup = 0x76;
    private const byte Op1 = 0x51;
    private const byte OpEqual = 0x87;
    private const byte OpEqualVerify = 0x88;

    /// <summary>
    /// The canonical <c>payTo</c> covenant: the spending tx's output at a
    /// witness-supplied index must pay <paramref name="recipientP2tr"/> exactly
    /// <paramref name="amountSats"/>.
    ///
    ///   Witness: [outputIndex]
    ///   Script:  DUP INSPECTOUTPUTSCRIPTPUBKEY 1 EQUALVERIFY &lt;wp32&gt; EQUALVERIFY
    ///            INSPECTOUTPUTVALUE &lt;amountMinLE&gt; EQUAL
    /// </summary>
    public static byte[] PayTo(Script recipientP2tr, long amountSats)
    {
        var pkScript = recipientP2tr.ToBytes();
        if (pkScript.Length != 34 || pkScript[0] != 0x51 || pkScript[1] != 0x20)
            throw new ArgumentException("payTo expects a P2TR (v1 witness) scriptPubKey.", nameof(recipientP2tr));
        if (amountSats <= 0)
            throw new ArgumentOutOfRangeException(nameof(amountSats));

        var witnessProgram = pkScript[2..];
        var amount = EncodeMinimalScriptNum(amountSats);

        var script = new List<byte>
        {
            OpDup,
            OpInspectOutputScriptPubkey,
            Op1,
            OpEqualVerify,
            (byte)witnessProgram.Length,
        };
        script.AddRange(witnessProgram);
        script.Add(OpEqualVerify);
        script.Add(OpInspectOutputValue);
        script.Add((byte)amount.Length);
        script.AddRange(amount);
        script.Add(OpEqual);
        return script.ToArray();
    }

    /// <summary>
    /// Encode a non-negative integer as the minimal script-num byte string the
    /// introspection opcodes read for indices (0 → empty).
    /// </summary>
    public static byte[] EncodeIndex(int index)
    {
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
        return index == 0 ? [] : EncodeMinimalScriptNum(index);
    }

    private static byte[] EncodeMinimalScriptNum(long value)
    {
        if (value <= 0) throw new ArgumentOutOfRangeException(nameof(value));
        var bytes = new List<byte>();
        var n = value;
        while (n > 0)
        {
            bytes.Add((byte)(n & 0xff));
            n >>= 8;
        }
        if ((bytes[^1] & 0x80) != 0) bytes.Add(0x00);
        return bytes.ToArray();
    }
}
