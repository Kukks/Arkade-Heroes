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

    private const byte OpSha256 = 0xa8;

    /// <summary>
    /// The <c>atomicSweep</c> covenant (coinflip R1): strengthens <see cref="PayTo"/>
    /// with a cross-input check — the spending tx must also contain another input
    /// (index witness-supplied) whose value equals <paramref name="otherInputValueSats"/>.
    /// Both escrow leaves pin the sibling's stake, so one stake can never be
    /// swept without the other.
    ///
    ///   Witness: [outputIndex, otherInputIndex]  (otherInputIndex on top)
    ///   Script:  INSPECTINPUTVALUE &lt;otherValueMinLE&gt; EQUALVERIFY + payTo body
    /// </summary>
    public static byte[] AtomicSweep(Script recipientP2tr, long potSats, long otherInputValueSats)
    {
        if (otherInputValueSats <= 0)
            throw new ArgumentOutOfRangeException(nameof(otherInputValueSats));
        var other = EncodeMinimalScriptNum(otherInputValueSats);
        var body = PayTo(recipientP2tr, potSats);
        return
        [
            OpInspectInputValue,
            (byte)other.Length, .. other,
            OpEqualVerify,
            .. body,
        ];
    }

    /// <summary>
    /// Commit–reveal gate: the witness must reveal the pre-committed server
    /// seed before anything else runs.
    ///
    ///   Witness: [... , serverSeed]  (seed on top)
    ///   Script:  SHA256 &lt;commit32&gt; EQUALVERIFY
    /// </summary>
    public static byte[] Sha256Gate(byte[] commitment32)
    {
        if (commitment32.Length != 32)
            throw new ArgumentException("Commitment must be 32 bytes.", nameof(commitment32));
        return
        [
            OpSha256,
            32, .. commitment32,
            OpEqualVerify,
        ];
    }

    /// <summary>
    /// A wager-escrow settle branch: reveal the committed seed, then sweep both
    /// stakes atomically to the winner.
    ///
    ///   Witness: [outputIndex, otherInputIndex, serverSeed]  (seed on top)
    /// </summary>
    public static byte[] SettleWithSeed(byte[] commitment32, Script winnerP2tr, long potSats, long stakeSats)
        => [.. Sha256Gate(commitment32), .. AtomicSweep(winnerP2tr, potSats, stakeSats)];

    private const byte OpCheckSigFromStack = 0xcc;
    private const byte OpVerify = 0x69;

    /// <summary>
    /// Oracle-authorization gate via OP_CHECKSIGFROMSTACK: the witness supplies
    /// a BIP340 signature; the EXPECTED message and the oracle key are baked
    /// into the script, so a signature over any other message (e.g. the other
    /// settle branch) can never satisfy this branch.
    ///
    ///   Witness: [... , oracleSig]  (sig on top)
    ///   Script:  &lt;message32&gt; &lt;oraclePk32&gt; CHECKSIGFROMSTACK VERIFY
    ///   (CSFS pops pk, msg, sig — emulator opcode.go:2551)
    /// </summary>
    public static byte[] CheckSigFromStackGate(byte[] message32, byte[] oraclePk32)
    {
        if (message32.Length != 32) throw new ArgumentException("Message must be 32 bytes.", nameof(message32));
        if (oraclePk32.Length != 32) throw new ArgumentException("Oracle key must be 32 bytes (x-only).", nameof(oraclePk32));
        return
        [
            32, .. message32,
            32, .. oraclePk32,
            OpCheckSigFromStack,
            OpVerify,
        ];
    }

    /// <summary>
    /// The canonical per-branch settle message: binds the oracle's authorization
    /// to ONE match and ONE winner branch. This is what the game key signs at
    /// settlement (the same key that signs the players' progression receipts).
    /// </summary>
    public static byte[] SettleMessage(string matchId, bool challengerWon)
        => System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(
                $"arkade-heroes-settle-v1|{matchId}|{(challengerWon ? "challenger" : "defender")}"));

    /// <summary>
    /// The full oracle-authorized settle branch:
    /// oracle signature over THIS branch's message + revealed seed + atomic sweep.
    ///
    ///   Witness: [outputIndex, otherInputIndex, serverSeed, oracleSig]  (sig on top)
    /// </summary>
    public static byte[] SettleAuthorized(
        byte[] settleMessage32, byte[] oraclePk32,
        byte[] commitment32, Script winnerP2tr, long potSats, long stakeSats)
        =>
        [
            .. CheckSigFromStackGate(settleMessage32, oraclePk32),
            .. SettleWithSeed(commitment32, winnerP2tr, potSats, stakeSats),
        ];

    /// <summary>
    /// Refund covenant: the stake can be reclaimed ONLY to the party's own
    /// address (payTo pins it), so anyone (the party, a watchtower) may
    /// trigger the refund without being able to steal. The TIME gate lives in
    /// the tapleaf (a CLTV condition on the leaf, enforced on the checkpoint
    /// spend by the operator) — pass the expiry as the function's LockTime.
    /// Arkade-script CLTV cannot gate the ark tx: arkd derives the canonical
    /// ark transaction with locktime 0 (ARK_TX_MISMATCH otherwise).
    ///
    /// SUBMIT-ONCE DISCIPLINE: the canonical refund tx is fully deterministic
    /// (arkd rebuilds it with locktime = the leaf's CLTV and sequence
    /// 0xFFFFFFFE — zero degrees of freedom). arkd records a failure event for
    /// every refused submission under the submitted txid, and its event replay
    /// treats the failed flag as sticky: a later ACCEPTED resubmission of the
    /// same txid finalizes at the RPC level but is never projected into the
    /// VTXO set (arkd v0.9.9-rc.1, internal/core/domain/offchain_tx.go). So
    /// wait for the CHAIN's clock to pass the expiry, then submit exactly once
    /// — never submit early and retry.
    ///
    ///   Witness: [outputIndex]
    /// </summary>
    public static byte[] RefundTo(Script partyP2tr, long stakeSats)
        => PayTo(partyP2tr, stakeSats);

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
