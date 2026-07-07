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
    /// STRUCTURAL (covenant-v2): the <paramref name="asset"/> (amount <paramref name="amount"/>,
    /// default 1) sits at output <paramref name="outputIndex"/> AND that output pays
    /// <paramref name="script"/> (P2TR). Baked output index (not witness-supplied) — no witness
    /// freedom. Mirrors the breed 0xf2 lookup + PayTo's 0xd1 output-script check. Consumes NO
    /// witness (all baked). At amount 1 the script is BYTE-IDENTICAL to the pre-amount version
    /// (PushScriptNum(1) == OP_1), so shipped covenant addresses are unchanged.
    /// EXACT stack contract validated by CovenantStructuralBurnProbe.
    /// </summary>
    public static byte[] AssetAtOutput(int outputIndex, global::NArk.Core.Assets.AssetId asset, Script script, int amount = 1)
    {
        var pkScript = script.ToBytes();
        if (pkScript.Length != 34 || pkScript[0] != 0x51 || pkScript[1] != 0x20)
            throw new ArgumentException("AssetAtOutput expects a P2TR (v1 witness) scriptPubKey.", nameof(script));
        var witnessProgram = pkScript[2..];
        return
        [
            // the asset is present at output o with the expected amount (0xef → amount, flag).
            // The output index is BAKED as a script constant — PushScriptNum, NOT
            // EncodeIndex (which emits an empty push for 0 / a bare scriptnum that
            // the parser misreads as a data-push opcode; EncodeIndex is for witness
            // items). Mirrors breed ParentPresent baking its gidx with PushScriptNum.
            .. PushScriptNum(outputIndex),
            32, .. asset.Txid.Reverse(), .. PushScriptNum(asset.GroupIndex),
            OpInspectOutAssetLookup, OpVerify, .. PushScriptNum(amount), OpEqualVerify88,
            // ...and output o pays `script` (P2TR: version 1 + the 32-byte program) — mirror PayTo
            .. PushScriptNum(outputIndex),
            OpInspectOutputScriptPubkey, Op1, OpEqualVerify,
            (byte)witnessProgram.Length, .. witnessProgram, OpEqualVerify,
        ];
    }

    /// <summary>
    /// STRUCTURAL (covenant-v2): the <paramref name="asset"/> is BURNED — absent (flag 0) from
    /// every output 0..<paramref name="outputCount"/>-1. The settle tx's output set is fixed by
    /// the spender, so outputCount is known. If the arkade VM can't express clean absence (the
    /// 0xef flag being empty-bytes vs scriptnum-0), the probe iterates to the 0xed
    /// "exactly-one-hero-output = winner's" framing. Consumes NO witness.
    /// </summary>
    public static byte[] AssetBurned(global::NArk.Core.Assets.AssetId asset, int outputCount)
    {
        var s = new List<byte>();
        for (var o = 0; o < outputCount; o++)
        {
            s.AddRange(PushScriptNum(o)); // baked script constant — NOT EncodeIndex (see AssetAtOutput)
            s.Add(32); s.AddRange(asset.Txid.Reverse());
            s.AddRange(PushScriptNum(asset.GroupIndex));
            s.Add(OpInspectOutAssetLookup); // → amount, flag  (flag on top)
            s.Add(Op0);                     // push 0
            s.Add(OpEqualVerify);           // flag == 0 (absent)
            s.Add(OpDrop);                  // drop the amount
        }
        return [.. s];
    }

    /// <summary>
    /// STRUCTURAL (covenant-v2): output 0 carries EXACTLY ONE asset (0xed count == 1)
    /// AND pays <paramref name="playerP2tr"/> (0xd1 output-script). Used with AssetBurned
    /// on the inputs + the species-pin: when every input asset is burned and the fee is
    /// sats-only, the lone asset at output 0 MUST be the fresh mint — so this binds a
    /// tx-derived mint to the baked player WITHOUT its (unknowable) asset id. Consumes NO
    /// witness (output index baked to 0). EXACT stack contract validated by CovenantMintToPlayerProbe.
    /// </summary>
    public static byte[] MintToPlayer(Script playerP2tr)
    {
        var pkScript = playerP2tr.ToBytes();
        if (pkScript.Length != 34 || pkScript[0] != 0x51 || pkScript[1] != 0x20)
            throw new ArgumentException("MintToPlayer expects a P2TR (v1 witness) scriptPubKey.", nameof(playerP2tr));
        var witnessProgram = pkScript[2..];
        return
        [
            // output 0 has exactly one asset entry (the fresh mint — inputs are burned, fee is sats-only)
            .. PushScriptNum(0),
            OpInspectOutAssetCount, Op1, OpEqualVerify,
            // ...and output 0 pays `player` (P2TR: version 1 + the 32-byte program) — mirror PayTo
            .. PushScriptNum(0),
            OpInspectOutputScriptPubkey, Op1, OpEqualVerify,
            (byte)witnessProgram.Length, .. witnessProgram, OpEqualVerify,
        ];
    }

    /// <summary>
    /// STRUCTURAL (covenant-v2): the child asset GROUP (index childK, ALREADY on the stack from
    /// the witness) has its first output at tx-output <paramref name="expectedVout"/>. Because
    /// childK is the SAME group BreedAuthorized forces to be under-species + oracle-signed, this
    /// binds the REAL child's output — no need for its (tx-derived) asset id. 0xeb pops source,
    /// j, k and (source=1) pushes (1, vout, amount); we drop the amount, verify vout, drop the
    /// marker. Consumes the childK witness item. EXACT stack contract validated by CovenantChildAtOutputProbe.
    /// </summary>
    public static byte[] ChildAtOutput(int expectedVout) =>
    [
        .. PushScriptNum(0),               // j = 0 (the child group's first output)
        .. PushScriptNum(1),               // source = 1 (outputs)
        OpInspectAssetGroup,               // 0xeb → 1, vout, amount  (amount on top)
        OpDrop,                            // drop amount → 1, vout
        .. PushScriptNum(expectedVout),    // expected vout
        OpEqualVerify,                     // vout == expectedVout → leaves the source-marker 1
        OpDrop,                            // drop the marker → clean
    ];

    /// <summary>
    /// STRUCTURAL (covenant-v2): the <paramref name="asset"/> is PRESENT (amount 1) at a
    /// WITNESS-SUPPLIED output index — the `.ark` spec's `tx.outputs[i].assets.lookup == 1`
    /// intent. Presence/conservation only, NO script pin: used where the recipient cannot be
    /// baked (an offer's fulfiller routes the item to themselves out of self-interest; the
    /// covenant forbids destruction/omission). Byte-wise this is breed ParentPresent with the
    /// OUT-asset lookup 0xef instead of the IN-asset lookup 0xf2. Consumes ONE witness item
    /// (the output index). EXACT stack contract validated by CovenantAssetAtWitnessOutputProbe.
    /// </summary>
    public static byte[] AssetAtWitnessOutput(global::NArk.Core.Assets.AssetId asset) =>
    [
        32, .. asset.Txid.Reverse(),
        .. PushScriptNum(asset.GroupIndex),
        OpInspectOutAssetLookup,   // pops gidx, txid, outIdx(witness) → amount, flag
        OpVerify,                  // flag == 1 (present at that output)
        Op1, OpEqualVerify88,      // amount == 1
    ];

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

    /// <summary>The death-match settle message the oracle signs for the winning branch — a DISTINCT tag from wager settles (no cross-protocol replay). Byte-identical in the InMemory sim, the server signer, and the rung-2 covenant.</summary>
    public static byte[] DeathMatchSettleMessage(string deathMatchId, bool challengerWon)
        => System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(
                $"arkade-heroes-deathmatch-v1|{deathMatchId}|{(challengerWon ? "challenger" : "defender")}"));

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
    /// A NO-POT settle branch (death-match): oracle-authorize the winning branch +
    /// reveal the committed seed, but NO sats sweep — the stakes are heroes, routed
    /// by the asset packet (winner's hero retained, loser's hero burned). The
    /// AtomicSweep's output/other-input introspection is dropped, so the witness has
    /// no index args.
    ///
    ///   Witness (bottom→top): [serverSeed, oracleSig]  (sig on top)
    /// </summary>
    public static byte[] SettleAuthorizedNoPot(byte[] settleMessage32, byte[] oraclePk32, byte[] commitment32)
        =>
        [
            .. CheckSigFromStackGate(settleMessage32, oraclePk32),
            .. Sha256Gate(commitment32),
            Op1, // both gates end in *VERIFY (consume their result); the arkade VM requires the
                 // script to leave EXACTLY ONE truthy item, so push OP_1 as the success result.
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

    private const byte OpDrop = 0x75;
    private const byte OpInspectAssetGroupCtrl = 0xe7;
    private const byte OpInspectAssetGroupMetadataHash = 0xe9;
    private const byte OpInspectAssetGroup = 0xeb;      // source j k → (source=1: 1, vout, amount)
    private const byte OpInspectInAssetLookup = 0xf2;
    private const byte OpInspectOutAssetCount = 0xed;   // o → n (asset entries at output o)
    private const byte OpInspectOutAssetLookup = 0xef;  // o txid gidx → (amount, flag); flag 1 found / 0 absent
    private const byte OpEqualVerify88 = 0x88;
    private const byte Op0 = 0x00;

    /// <summary>
    /// The full covenant-breeding gate. An invalid breed is UNSIGNABLE:
    ///  1. each parent asset (baked canonical id) must be PRESENT at a
    ///     witness-named input with amount 1 (OP_INSPECTINASSETLOOKUP; arkd's
    ///     input-conservation rule then forces the retention passthroughs);
    ///  2. the child group's CONTROL asset must equal the species (baked) —
    ///     mandatory because arkd itself lets ANYONE mint under a foreign
    ///     control asset (proven live, rung 3);
    ///  3. the breeding fee output (witness-named index) pays the treasury
    ///     exactly <paramref name="feeSats"/> (payTo);
    ///  4. the breeding oracle's BIP340 signature over the child group's
    ///     metadata Merkle root — READ FROM THE TX via
    ///     OP_INSPECTASSETGROUPMETADATAHASH — must verify (CSFS). The genome
    ///     and breed-context entries live inside that metadata, so the signed
    ///     root binds them without baking the (commit-reveal) genome upfront.
    ///
    /// BYTE ORDER: in-VM asset txids are INTERNAL byte order — the REVERSE of
    /// NArk's AssetId.Txid (display-decoded). This builder reverses
    /// internally; pass NArk AssetIds as-is.
    ///
    ///   Witness (bottom→top): [oracleSig64, childK, feeOutIdx, childK, iB, iA]
    ///   — build it with <see cref="BreedWitness"/>.
    /// </summary>
    public static byte[] BreedAuthorized(
        global::NArk.Core.Assets.AssetId species,
        global::NArk.Core.Assets.AssetId parentA,
        global::NArk.Core.Assets.AssetId parentB,
        byte[] oraclePk32, Script feeP2tr, long feeSats)
    {
        if (oraclePk32.Length != 32) throw new ArgumentException("Oracle key must be 32 bytes (x-only).", nameof(oraclePk32));

        // Per parent (stack top: i): push txid(internal), gidx; 0xf2 pops
        // gidx, txid, i → pushes amount, found; VERIFY found; amount==1.
        static byte[] ParentPresent(global::NArk.Core.Assets.AssetId parent) =>
        [
            32, .. parent.Txid.Reverse(),
            .. PushScriptNum(parent.GroupIndex),
            OpInspectInAssetLookup,
            OpVerify,
            Op1, OpEqualVerify88,
        ];

        return
        [
            // Parents (consume iA, then iB from the witness top).
            .. ParentPresent(parentA),
            .. ParentPresent(parentB),
            // Species pin (consumes childK): 0xe7 pushes ctrl_txid, ctrl_gidx,
            // found (top). VERIFY found; DROP gidx; txid EQUALVERIFY. The txid
            // uniquely identifies the species; gidx pinning is dropped because
            // the VM pushes a canonical-empty scriptNum for index 0, which a
            // data push of 0x00 does not equal. ctrl_txid is REVERSED
            // (internal) order — SAME as the 0xf1/0xf2 family (proven live,
            // CtrlTxidFormat_Resolves).
            OpInspectAssetGroupCtrl,
            OpVerify,
            OpDrop,
            32, .. species.Txid.Reverse(), OpEqualVerify88,
            // Fee (consumes feeOutIdx): payTo ends with EQUAL — VERIFY it. The
            // fee output MUST pay a different address than the change, or the
            // builder coalesces same-script outputs and the fee vanishes.
            .. PayTo(feeP2tr, feeSats),
            OpVerify,
            // Oracle (consumes childK then oracleSig): root from the tx, key
            // baked, CSFS pops pk, msg, sig and leaves the verdict.
            OpInspectAssetGroupMetadataHash,
            32, .. oraclePk32,
            OpCheckSigFromStack,
        ];
    }

    /// <summary>
    /// The FULL merge covenant gate (covenant-v2): everything <see cref="BreedAuthorized"/>
    /// enforces (both inputs present, mint under the species, fee to the treasury, oracle sig
    /// over the fused metadata root) PLUS the structural asset consequences merge previously
    /// left to the packet — base + sacrifice BURNED (absent from every output) and the fused
    /// hero MINTED TO THE PLAYER (the lone output-0 asset). An invalid merge is UNSIGNABLE.
    /// Witness is IDENTICAL to BreedAuthorized (<see cref="BreedWitness"/>) — the added
    /// checks are fully baked.
    /// </summary>
    public static byte[] MergeAuthorized(
        global::NArk.Core.Assets.AssetId species,
        global::NArk.Core.Assets.AssetId baseAsset,
        global::NArk.Core.Assets.AssetId sacrificeAsset,
        byte[] oraclePk32, Script feeP2tr, long feeSats,
        Script playerP2tr, int outputSweep)
        =>
        [
            .. BreedAuthorized(species, baseAsset, sacrificeAsset, oraclePk32, feeP2tr, feeSats),
            OpVerify,                                    // consume the oracle CSFS verdict
            .. AssetBurned(baseAsset, outputSweep),      // base provably destroyed
            .. AssetBurned(sacrificeAsset, outputSweep), // sacrifice provably destroyed
            .. MintToPlayer(playerP2tr),                 // the lone output-0 asset → the player
            Op1,                                         // leave EXACTLY one truthy item
        ];

    /// <summary>
    /// The FULL breed covenant gate (covenant-v2): everything <see cref="BreedAuthorized"/>
    /// enforces (both parents present, child under the species, fee to the treasury, oracle sig
    /// over the child metadata root) PLUS the structural asset consequences breed previously left
    /// to the packet — both parents RETAINED to the player (at output 0) and the oracle-signed
    /// CHILD minted to the player (its group's output at output 0). All three assets share
    /// output 0 (two player-paying outputs would coalesce). Witness = <see cref="BreedRetainWitness"/>.
    /// </summary>
    public static byte[] BreedRetainAuthorized(
        global::NArk.Core.Assets.AssetId species,
        global::NArk.Core.Assets.AssetId parentA,
        global::NArk.Core.Assets.AssetId parentB,
        byte[] oraclePk32, Script feeP2tr, long feeSats, Script playerP2tr)
        =>
        [
            .. BreedAuthorized(species, parentA, parentB, oraclePk32, feeP2tr, feeSats),
            OpVerify,                                    // consume the oracle CSFS verdict
            .. AssetAtOutput(0, parentA, playerP2tr),    // parentA retained → player at output 0
            .. AssetAtOutput(0, parentB, playerP2tr),    // parentB retained → player at output 0
            .. ChildAtOutput(0),                         // the oracle-signed child group → output 0
            Op1,                                         // leave EXACTLY one truthy item
        ];

    /// <summary>The witness for <see cref="BreedAuthorized"/> — see its stack contract.</summary>
    public static byte[][] BreedWitness(
        byte[] oracleSig64, int childGroupIndex, int feeOutputIndex,
        int parentAInputIndex, int parentBInputIndex) =>
    [
        oracleSig64,
        EncodeIndex(childGroupIndex),
        EncodeIndex(feeOutputIndex),
        EncodeIndex(childGroupIndex),
        EncodeIndex(parentBInputIndex),
        EncodeIndex(parentAInputIndex),
    ];

    /// <summary>
    /// The witness for <see cref="BreedRetainAuthorized"/> — <see cref="BreedWitness"/> plus ONE
    /// extra child-group index at the BOTTOM (array index 0, pushed first / consumed last). The
    /// breed gate consumes the six BreedWitness items first; the extra childK is left for
    /// <see cref="ChildAtOutput"/> after the two baked (net-empty) AssetAtOutput checks.
    /// </summary>
    public static byte[][] BreedRetainWitness(
        byte[] oracleSig64, int childGroupIndex, int feeOutputIndex,
        int parentAInputIndex, int parentBInputIndex) =>
    [
        EncodeIndex(childGroupIndex), // the EXTRA childK for ChildAtOutput (witness bottom)
        .. BreedWitness(oracleSig64, childGroupIndex, feeOutputIndex, parentAInputIndex, parentBInputIndex),
    ];

    /// <summary>Minimal script-number PUSH opcodes for small non-negative values (0 → OP_0, 1..16 → OP_1..OP_16, else a minimal data push).</summary>
    private static byte[] PushScriptNum(int value)
    {
        if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
        if (value == 0) return [0x00];
        if (value <= 16) return [(byte)(0x50 + value)];
        var bytes = EncodeMinimalScriptNum(value);
        return [(byte)bytes.Length, .. bytes];
    }

    /// <summary>
    /// The Merkle root of an asset group's metadata, exactly as the emulator's
    /// OP_INSPECTASSETGROUPMETADATAHASH computes it (emulator
    /// asset_opcodes.go computeMetadataMerkleRoot): leaf = SHA256 of the
    /// serialized entry (LEB128 var-slices of key then value — NArk's
    /// BufferWriter matches ark-lib here; the CompactSize divergence is
    /// EmulatorPacket-only), pairwise SHA256(left||right), odd node promoted
    /// unhashed. Empty metadata → 32 zero bytes. This is what the breeding
    /// oracle signs: the root binds the child's genome (and the breed-context
    /// entries) to the covenant-checked mint.
    /// </summary>
    public static byte[] MetadataMerkleRoot(IReadOnlyList<global::NArk.Core.Assets.AssetMetadata> metadata)
    {
        if (metadata.Count == 0) return new byte[32];

        var hashes = new List<byte[]>(metadata.Count);
        foreach (var entry in metadata)
        {
            var writer = new global::NArk.Core.Assets.BufferWriter();
            entry.SerializeTo(writer);
            hashes.Add(System.Security.Cryptography.SHA256.HashData(writer.ToBytes()));
        }

        while (hashes.Count > 1)
        {
            var next = new List<byte[]>((hashes.Count + 1) / 2);
            for (var i = 0; i < hashes.Count; i += 2)
            {
                if (i + 1 < hashes.Count)
                {
                    var combined = new byte[64];
                    hashes[i].CopyTo(combined, 0);
                    hashes[i + 1].CopyTo(combined, 32);
                    next.Add(System.Security.Cryptography.SHA256.HashData(combined));
                }
                else
                {
                    next.Add(hashes[i]);
                }
            }
            hashes = next;
        }
        return hashes[0];
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
