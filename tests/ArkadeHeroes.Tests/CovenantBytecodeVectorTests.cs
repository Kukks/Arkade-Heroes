using ArkadeHeroes.Chain.Covenants;
using NArk.Core.Assets;
using NBitcoin;

namespace ArkadeHeroes.Tests;

/// <summary>
/// Golden bytecode vectors for EVERY public builder in <see cref="ArkadeCovenants"/> — the layer that
/// compiles the scripts guarding real bitcoin and real player assets: hero mint/burn, breeding,
/// merge/fusion, death-match (permadeath), marketplace offers, wager escrows, timelocked refunds.
///
/// WHY THIS FILE EXISTS. The SEMANTICS of these scripts were proven by live regtest "teeth" probes —
/// CovenantStructuralBurnProbe, CovenantMintToPlayerProbe, CovenantChildAtOutputProbe,
/// CovenantAssetAtWitnessOutputProbe, CovenantAssetGroupProbe — each of which needs a running arkd +
/// emulator stack that CI does not have. This hand-rolled layer is about to be replaced by upstream
/// NArk.Arkade. Without a byte-level net that swap is unverifiable off-regtest, and a silently
/// different script has exactly two failure modes, both unrecoverable: funds locked in a covenant
/// nobody can spend, or a covenant that quietly stops enforcing what it promises.
///
/// So these vectors pin the CURRENT bytes — the bytes the live probes signed off on. That much is
/// self-blessing by construction. To stop it being ONLY that, the builders whose exact stack contract
/// a probe validated also carry structural assertions further down this file: the introspection
/// opcode, its position in the script, the embedded asset id and its byte order, the index encoding —
/// each read off the builder's own documented contract rather than off its output. A rewrite that
/// moves an opcode therefore fails BOTH the hex and the structure, and cannot be waved through by
/// re-baking the hex.
///
/// Everything here is deterministic and hermetic: fixed keys, fixed asset ids, fixed scripts, fixed
/// amounts. No randomness, no clock, no network, no regtest.
/// </summary>
public class CovenantBytecodeVectorTests
{
    // =============================================================================================
    // Fixtures.
    //
    // Every fixture is an ASCENDING byte run, never a repeated fill. An ascending run is not a
    // palindrome, so a builder that drops (or wrongly adds) the internal-byte-order reversal that
    // in-VM asset txids require produces different bytes and fails. A repeated fill would hide
    // precisely that bug — the one that would make every covenant address shift at once.
    //
    // The runs are also mutually DISJOINT, so an argument swap inside a builder (parentA for parentB,
    // fee recipient for player, commitment for oracle key) changes the vector instead of cancelling out.
    // =============================================================================================

    /// <summary><paramref name="count"/> ascending bytes starting at <paramref name="start"/>.</summary>
    private static byte[] Bytes(int count, byte start) =>
        Enumerable.Range(0, count).Select(i => (byte)(start + i)).ToArray();

    private static byte[] Bytes32(byte start) => Bytes(32, start);

    /// <summary>
    /// A P2TR scriptPubKey (OP_1 + 32-byte witness program) whose program starts at
    /// <paramref name="start"/>. Builders reject anything that is not v1/34 bytes, so every script
    /// fixture must be shaped like this.
    /// </summary>
    private static Script P2tr(byte start) => Script.FromBytesUnsafe([0x51, 0x20, .. Bytes32(start)]);

    private static Script Player() => P2tr(0x81);    // the hero owner: mint recipient, retain target, refund party
    private static Script Treasury() => P2tr(0xa1);  // the breed/merge fee recipient
    private static Script Winner() => P2tr(0xc1);    // the wager-escrow sweep recipient

    /// <summary>
    /// A fixed NArk <see cref="AssetId"/>: 32 ascending txid bytes plus a group index. The four below
    /// use disjoint txid runs AND distinct group indices, so index 0 pins PushScriptNum's OP_0 branch
    /// (0x00) while 1..3 pin its OP_1..OP_16 branch (0x51..0x53) in the same file.
    /// </summary>
    private static AssetId Asset(byte start, ushort groupIndex) =>
        AssetId.Create(Convert.ToHexString(Bytes32(start)), groupIndex);

    private static AssetId Species() => Asset(0x01, 0);   // the control asset every hero must mint under
    private static AssetId ParentA() => Asset(0x21, 1);   // breed parent A / merge base / death-match winner's hero
    private static AssetId ParentB() => Asset(0x41, 2);   // breed parent B / merge sacrifice / loser's hero
    private static AssetId Item() => Asset(0x61, 3);      // a marketplace item / shared fungible gear group

    // Disjoint 32-byte blobs. message/oracle-key/commitment are pairwise distinct so SettleAuthorized,
    // which takes all three, cannot silently reorder them.
    private static byte[] Message32() => Bytes32(0x10);
    private static byte[] OraclePk32() => Bytes32(0x50);
    private static byte[] Commitment32() => Bytes32(0x90);
    private static byte[] ServerSeed32() => Bytes32(0xd0);

    // Two distinct 64-byte signature blobs: the absorb-mint witness carries BOTH (one over the
    // outcome, one over the metadata root) and must not transpose them.
    private static byte[] OracleSigRoot64() => Bytes(64, 0x01);
    private static byte[] OracleSigOutcome64() => Bytes(64, 0x41);

    private static AssetMetadata Meta(string key, string value) => AssetMetadata.Create(key, value);

    // Fixed amounts. 6_000 exercises the plain two-byte minimal scriptnum; 128 exercises the
    // sign-padding branch (0x80 has its high bit set, so it MUST be padded to 0x80 0x00 or the VM
    // reads it as negative) — the encoder branch a rewrite is most likely to get wrong.
    private const long PayAmountSats = 6_000;
    private const long SignPadAmountSats = 128;
    private const long PotSats = 10_000;
    private const long StakeSats = 5_000;
    private const long FeeSats = 2_500;

    // =============================================================================================
    // Pinning helpers.
    // =============================================================================================

    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    /// <summary>Pin a script builder's exact bytes as lowercase hex.</summary>
    private static void Pin(string expected, byte[] actual) => Assert.Equal(expected, Hex(actual));

    /// <summary>
    /// Pin a witness builder's exact items, comma-joined. Joining rather than concatenating keeps the
    /// ITEM BOUNDARIES inside the vector, and renders EncodeIndex(0)'s EMPTY push as an empty field —
    /// the subtlest byte in this file, and the one a rewrite is most likely to "helpfully" promote to
    /// a 0x00 (which the VM would read as a one-byte item, not an absent one).
    /// </summary>
    private static void PinWitness(string expected, byte[][] actual) =>
        Assert.Equal(expected, string.Join(",", actual.Select(Hex)));

    // =============================================================================================
    // Primitives — the sats-routing and gating pieces every escrow composes.
    // =============================================================================================

    [Fact]
    public void PayTo_PinsTheCanonicalOutputPayment()
    {
        // Guards: every sats payout in the game — wager pot sweeps, breed/merge treasury fees, refunds.
        Pin("76d15188208182838485868788898a8b8c8d8e8f909192939495969798999a9b9c9d9e9fa088cf02701787", ArkadeCovenants.PayTo(Player(), PayAmountSats));

        // Same covenant at an amount whose minimal encoding needs sign padding. If a rewrite drops the
        // pad, the VM reads the amount as NEGATIVE and the payout can never satisfy the covenant.
        Pin("76d15188208182838485868788898a8b8c8d8e8f909192939495969798999a9b9c9d9e9fa088cf02800087", ArkadeCovenants.PayTo(Player(), SignPadAmountSats));
    }

    [Fact]
    public void RefundTo_PinsTheTimelockedReclaim()
    {
        // Guards: the timelocked refund branch of every escrow — the stake can only come back to its
        // own party, so anyone may trigger it without being able to steal.
        Pin("76d15188208182838485868788898a8b8c8d8e8f909192939495969798999a9b9c9d9e9fa088cf02881387", ArkadeCovenants.RefundTo(Player(), StakeSats));

        // RefundTo is PayTo under another name. Pinned as an identity so a future divergence — a
        // refund that stops pinning the recipient — is loud rather than a quiet extra branch.
        Assert.Equal(Hex(ArkadeCovenants.PayTo(Player(), StakeSats)),
            Hex(ArkadeCovenants.RefundTo(Player(), StakeSats)));
    }

    [Fact]
    public void AtomicSweep_PinsTheCrossInputStakeBinding()
    {
        // Guards: the wager escrow's "one stake can never be swept without the other" rule — both
        // leaves pin the sibling's input value, so a winner cannot walk with half the pot.
        Pin("c90288138876d1518820c1c2c3c4c5c6c7c8c9cacbcccdcecfd0d1d2d3d4d5d6d7d8d9dadbdcdddedfe088cf02102787", ArkadeCovenants.AtomicSweep(Winner(), PotSats, StakeSats));
    }

    [Fact]
    public void Sha256Gate_PinsTheCommitRevealGate()
    {
        // Guards: the fairness commitment on every settle — the pre-committed server seed must be
        // revealed on-chain before any payout branch runs.
        Pin("a820909192939495969798999a9b9c9d9e9fa0a1a2a3a4a5a6a7a8a9aaabacadaeaf88", ArkadeCovenants.Sha256Gate(Commitment32()));
    }

    [Fact]
    public void CheckSigFromStackGate_PinsTheOracleAuthorization()
    {
        // Guards: every oracle-authorized branch. The EXPECTED message is baked, so a signature over
        // the other branch's message can never satisfy this one — this is what stops a loser settling
        // as the winner.
        Pin("20101112131415161718191a1b1c1d1e1f202122232425262728292a2b2c2d2e2f20505152535455565758595a5b5c5d5e5f606162636465666768696a6b6c6d6e6fcc69", ArkadeCovenants.CheckSigFromStackGate(Message32(), OraclePk32()));
    }

    [Fact]
    public void EncodeIndex_PinsTheMinimalIndexEncoding()
    {
        // Guards: every witness index in the game (input, output and asset-group selectors). Index 0
        // is an EMPTY push, not 0x00 — get this wrong and every covenant that reads index 0 breaks.
        Pin("", ArkadeCovenants.EncodeIndex(0)); // NOT a stub: the empty string IS the vector — zero bytes
        Pin("01", ArkadeCovenants.EncodeIndex(1));
        Pin("10", ArkadeCovenants.EncodeIndex(16));
        Pin("11", ArkadeCovenants.EncodeIndex(17));
        Pin("7f", ArkadeCovenants.EncodeIndex(127));
        Pin("8000", ArkadeCovenants.EncodeIndex(128)); // sign-pad boundary
        Pin("ff00", ArkadeCovenants.EncodeIndex(255));
        Pin("0001", ArkadeCovenants.EncodeIndex(256));
    }

    // =============================================================================================
    // Settle branches and their oracle messages.
    // =============================================================================================

    [Fact]
    public void SettleWithSeed_PinsTheWagerSettleBranch()
    {
        // Guards: the wager escrow's settle leaf — reveal the committed seed, then sweep both stakes
        // to the winner atomically.
        Pin("a820909192939495969798999a9b9c9d9e9fa0a1a2a3a4a5a6a7a8a9aaabacadaeaf88c90288138876d1518820c1c2c3c4c5c6c7c8c9cacbcccdcecfd0d1d2d3d4d5d6d7d8d9dadbdcdddedfe088cf02102787", ArkadeCovenants.SettleWithSeed(Commitment32(), Winner(), PotSats, StakeSats));
    }

    [Fact]
    public void SettleAuthorized_PinsTheFullWagerSettleBranch()
    {
        // Guards: the complete wager settle leaf — oracle authorization for THIS branch, seed reveal,
        // then the atomic two-stake sweep.
        Pin("20101112131415161718191a1b1c1d1e1f202122232425262728292a2b2c2d2e2f20505152535455565758595a5b5c5d5e5f606162636465666768696a6b6c6d6e6fcc69a820909192939495969798999a9b9c9d9e9fa0a1a2a3a4a5a6a7a8a9aaabacadaeaf88c90288138876d1518820c1c2c3c4c5c6c7c8c9cacbcccdcecfd0d1d2d3d4d5d6d7d8d9dadbdcdddedfe088cf02102787", ArkadeCovenants.SettleAuthorized(
            Message32(), OraclePk32(), Commitment32(), Winner(), PotSats, StakeSats));
    }

    // (SettleAuthorizedNoPot's vector was dropped with the builder itself: nothing constructed it — the
    //  death-match settle leaf is built by DeathMatchEscrowContracts from SettleAuthorized + the asset
    //  packet. A vector pinning a script no covenant can emit guards nothing.)

    [Fact]
    public void SettleMessage_PinsBothWagerBranchTags()
    {
        // Guards: what the game key signs to authorize a wager settle. These 32 bytes are baked into
        // the covenant, so a tag change makes every already-funded escrow unsettleable.
        Pin("928716d2c3a204051e5803d1833124e40a9edc7769c11cb1ae840780924dd0fb", ArkadeCovenants.SettleMessage("match-vector-1", true));
        Pin("1afefb30b7111951cefaae9a3062416dd859c18812a7bbeb63e9fe6ee0ea5980", ArkadeCovenants.SettleMessage("match-vector-1", false));
    }

    [Fact]
    public void DeathMatchSettleMessage_PinsBothDeathMatchBranchTags()
    {
        // Guards: the death-match settle authorization — a DISTINCT tag from wager settles, which is
        // what prevents a wager signature being replayed to kill a hero.
        Pin("de48e907fddc7479cb53994becd68f9c107ed721ae29cb5b9305243c08b77fee", ArkadeCovenants.DeathMatchSettleMessage("dm-vector-1", true));
        Pin("ebdd7bd4509a148236260effca4f5f4ee782e4397b2f2277589204ce741d7863", ArkadeCovenants.DeathMatchSettleMessage("dm-vector-1", false));
    }

    [Fact]
    public void DeathMatchAbsorbMintMessage_PinsBothAbsorbBranchTags()
    {
        // Guards: the absorb-mint settle authorization — distinct again from the keep/passthrough tag,
        // so the seed-determined outcome is the ONLY signable branch and neither player can force
        // keep-vs-mint.
        Pin("7985df767bc5bc79cc6ecaa19abdb1b3f29f51f681acb316642723cb633c6418", ArkadeCovenants.DeathMatchAbsorbMintMessage("dm-vector-1", true));
        Pin("f9de5ac33d089433d4f1faa934aefaaad280c0b4023dc1ce1a8d73ca24a72095", ArkadeCovenants.DeathMatchAbsorbMintMessage("dm-vector-1", false));
    }

    [Fact]
    public void SettleMessages_AreMutuallyDistinctAcrossProtocols()
    {
        // Cross-protocol replay is the failure this separation exists to prevent: the same match id
        // and the same branch must hash differently per protocol, or an oracle signature issued for
        // one settle authorizes another.
        var wager = Hex(ArkadeCovenants.SettleMessage("x", true));
        var death = Hex(ArkadeCovenants.DeathMatchSettleMessage("x", true));
        var absorb = Hex(ArkadeCovenants.DeathMatchAbsorbMintMessage("x", true));
        Assert.Equal(3, new HashSet<string> { wager, death, absorb }.Count);
    }

    // =============================================================================================
    // Structural asset covenants (covenant-v2) — the introspection layer that routes heroes and items.
    // =============================================================================================

    [Fact]
    public void AssetAtOutput_PinsTheAssetPlusScriptBinding()
    {
        // Guards: breed parent retention — the parent asset must land at a baked output that pays the
        // player, so a breed cannot quietly consume the parents.
        Pin("0020403f3e3d3c3b3a393837363534333231302f2e2d2c2b2a29282726252423222151ef69518800d15188208182838485868788898a8b8c8d8e8f909192939495969798999a9b9c9d9e9fa088", ArkadeCovenants.AssetAtOutput(0, ParentA(), Player()));

        // The non-default path: a baked non-zero output index and an amount above 1 (fungible gear).
        Pin("5220807f7e7d7c7b7a797877767574737271706f6e6d6c6b6a69686766656463626153ef69558852d15188208182838485868788898a8b8c8d8e8f909192939495969798999a9b9c9d9e9fa088", ArkadeCovenants.AssetAtOutput(2, Item(), Player(), 5));
    }

    [Fact]
    public void AssetBurned_PinsTheAbsenceSweep()
    {
        // Guards: permadeath and merge — the loser's/sacrificed hero must be absent from EVERY output.
        // This is the covenant that makes a burn provable rather than promised.
        Pin("0020605f5e5d5c5b5a595857565554535251504f4e4d4c4b4a49484746454443424152ef0088755120605f5e5d5c5b5a595857565554535251504f4e4d4c4b4a49484746454443424152ef0088755220605f5e5d5c5b5a595857565554535251504f4e4d4c4b4a49484746454443424152ef008875", ArkadeCovenants.AssetBurned(ParentB(), 3));
    }

    [Fact]
    public void NumAssetGroupsIs_PinsTheGroupCountGate()
    {
        // Guards: the death-match reclaim — an exact group count forbids the counterparty's exclusive
        // assets riding along, since each would show up as an extra group.
        Pin("e55288", ArkadeCovenants.NumAssetGroupsIs(2));

        // Above 16 PushScriptNum leaves the OP_1..OP_16 range for a length-prefixed data push.
        Pin("e5011188", ArkadeCovenants.NumAssetGroupsIs(17));
    }

    [Fact]
    public void AssetInputSumIs_PinsTheSharedGroupInputSum()
    {
        // Guards: shared fungible gear — binds a group to MY staked units only; the counterparty's
        // units would push the input sum past the baked amount and be refused.
        Pin("20807f7e7d7c7b7a797877767574737271706f6e6d6c6b6a69686766656463626153e86900ec5388", ArkadeCovenants.AssetInputSumIs(Item(), 3));
    }

    [Fact]
    public void MintToPlayer_PinsTheFreshMintBinding()
    {
        // Guards: every mint whose asset id cannot be known in advance (merge fusion, absorb) — the
        // lone asset at output 0 must go to the baked player.
        Pin("00ed518800d15188208182838485868788898a8b8c8d8e8f909192939495969798999a9b9c9d9e9fa088", ArkadeCovenants.MintToPlayer(Player()));
    }

    [Fact]
    public void ChildAtOutput_PinsTheChildGroupOutputBinding()
    {
        // Guards: breeding — the oracle-signed child group's first output must be the expected vout,
        // binding the real child without needing its (tx-derived) asset id.
        Pin("0051eb75008875", ArkadeCovenants.ChildAtOutput(0));
        Pin("0051eb75518875", ArkadeCovenants.ChildAtOutput(1));
    }

    [Fact]
    public void AssetAtWitnessOutput_PinsThePresenceOnlyCheck()
    {
        // Guards: marketplace offers — the item must survive the fulfilment at a witness-chosen output.
        // Deliberately NO script pin: the fulfiller routes it to themselves out of self-interest; the
        // covenant only forbids destroying or omitting it.
        Pin("20807f7e7d7c7b7a797877767574737271706f6e6d6c6b6a69686766656463626153ef695188", ArkadeCovenants.AssetAtWitnessOutput(Item()));
    }

    // =============================================================================================
    // Composed mint gates — the full breed / merge / absorb covenants.
    // =============================================================================================

    [Fact]
    public void BreedAuthorized_PinsTheBreedGate()
    {
        // Guards: breeding. Both parents present, child minted under the species control asset, fee to
        // the treasury, oracle signature over the child's metadata root read FROM the tx.
        Pin("20403f3e3d3c3b3a393837363534333231302f2e2d2c2b2a29282726252423222151f269518820605f5e5d5c5b5a595857565554535251504f4e4d4c4b4a49484746454443424152f2695188e7697520201f1e1d1c1b1a191817161514131211100f0e0d0c0b0a0908070605040302018876d1518820a1a2a3a4a5a6a7a8a9aaabacadaeafb0b1b2b3b4b5b6b7b8b9babbbcbdbebfc088cf02c4098769e920505152535455565758595a5b5c5d5e5f606162636465666768696a6b6c6d6e6fcc", ArkadeCovenants.BreedAuthorized(
            Species(), ParentA(), ParentB(), OraclePk32(), Treasury(), FeeSats));
    }

    [Fact]
    public void MintUnderSpeciesAuthorized_PinsTheFeelessAbsorbMintGate()
    {
        // Guards: the death-match ABSORB settle — burn both staked heroes and mint one absorbed hero
        // under the species, with no treasury fee.
        Pin("20403f3e3d3c3b3a393837363534333231302f2e2d2c2b2a29282726252423222151f269518820605f5e5d5c5b5a595857565554535251504f4e4d4c4b4a49484746454443424152f2695188e7697520201f1e1d1c1b1a191817161514131211100f0e0d0c0b0a09080706050403020188e920505152535455565758595a5b5c5d5e5f606162636465666768696a6b6c6d6e6fcc", ArkadeCovenants.MintUnderSpeciesAuthorized(
            Species(), ParentA(), ParentB(), OraclePk32()));
    }

    [Fact]
    public void MergeAuthorized_PinsTheFullMergeGate()
    {
        // Guards: merge/fusion end to end — the breed gate plus the structural consequences: base and
        // sacrifice provably burned, fused hero minted to the player.
        Pin("20403f3e3d3c3b3a393837363534333231302f2e2d2c2b2a29282726252423222151f269518820605f5e5d5c5b5a595857565554535251504f4e4d4c4b4a49484746454443424152f2695188e7697520201f1e1d1c1b1a191817161514131211100f0e0d0c0b0a0908070605040302018876d1518820a1a2a3a4a5a6a7a8a9aaabacadaeafb0b1b2b3b4b5b6b7b8b9babbbcbdbebfc088cf02c4098769e920505152535455565758595a5b5c5d5e5f606162636465666768696a6b6c6d6e6fcc690020403f3e3d3c3b3a393837363534333231302f2e2d2c2b2a29282726252423222151ef0088755120403f3e3d3c3b3a393837363534333231302f2e2d2c2b2a29282726252423222151ef0088750020605f5e5d5c5b5a595857565554535251504f4e4d4c4b4a49484746454443424152ef0088755120605f5e5d5c5b5a595857565554535251504f4e4d4c4b4a49484746454443424152ef00887500ed518800d15188208182838485868788898a8b8c8d8e8f909192939495969798999a9b9c9d9e9fa08851", ArkadeCovenants.MergeAuthorized(
            Species(), ParentA(), ParentB(), OraclePk32(), Treasury(), FeeSats, Player(), 2));
    }

    [Fact]
    public void BreedRetainAuthorized_PinsTheFullBreedGate()
    {
        // Guards: breeding end to end — the breed gate plus both parents retained to the player and
        // the oracle-signed child minted to the player, all at output 0.
        Pin("20403f3e3d3c3b3a393837363534333231302f2e2d2c2b2a29282726252423222151f269518820605f5e5d5c5b5a595857565554535251504f4e4d4c4b4a49484746454443424152f2695188e7697520201f1e1d1c1b1a191817161514131211100f0e0d0c0b0a0908070605040302018876d1518820a1a2a3a4a5a6a7a8a9aaabacadaeafb0b1b2b3b4b5b6b7b8b9babbbcbdbebfc088cf02c4098769e920505152535455565758595a5b5c5d5e5f606162636465666768696a6b6c6d6e6fcc690020403f3e3d3c3b3a393837363534333231302f2e2d2c2b2a29282726252423222151ef69518800d15188208182838485868788898a8b8c8d8e8f909192939495969798999a9b9c9d9e9fa0880020605f5e5d5c5b5a595857565554535251504f4e4d4c4b4a49484746454443424152ef69518800d15188208182838485868788898a8b8c8d8e8f909192939495969798999a9b9c9d9e9fa0880051eb7500887551", ArkadeCovenants.BreedRetainAuthorized(
            Species(), ParentA(), ParentB(), OraclePk32(), Treasury(), FeeSats, Player()));
    }

    // =============================================================================================
    // Witness builders — the stack ORDER that makes the gates above satisfiable.
    // A gate and its witness are a matched pair: pinning one without the other pins nothing.
    // Every index below is DISTINCT, and one is 0, so both a transposition and a lost empty push fail.
    // =============================================================================================

    [Fact]
    public void BreedWitness_PinsTheBreedStackOrder()
    {
        // Guards: spendability of the breed leaf. Wrong order here and an honest breed is unspendable.
        PinWitness("0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f202122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f40,03,,03,02,01", ArkadeCovenants.BreedWitness(
            OracleSigRoot64(), childGroupIndex: 3, feeOutputIndex: 0,
            parentAInputIndex: 1, parentBInputIndex: 2));
    }

    [Fact]
    public void BreedRetainWitness_PinsTheExtraChildGroupIndex()
    {
        // Guards: spendability of the retain-breed leaf — BreedWitness plus one extra child-group
        // index at the BOTTOM, consumed last by ChildAtOutput.
        PinWitness("03,0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f202122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f40,03,,03,02,01", ArkadeCovenants.BreedRetainWitness(
            OracleSigRoot64(), childGroupIndex: 3, feeOutputIndex: 0,
            parentAInputIndex: 1, parentBInputIndex: 2));

        // The retain witness must be exactly the breed witness with one item prepended.
        var breed = ArkadeCovenants.BreedWitness(OracleSigRoot64(), 3, 0, 1, 2);
        var retain = ArkadeCovenants.BreedRetainWitness(OracleSigRoot64(), 3, 0, 1, 2);
        Assert.Equal(breed.Length + 1, retain.Length);
        Assert.Equal(string.Join(",", breed.Select(Hex)), string.Join(",", retain[1..].Select(Hex)));
    }

    [Fact]
    public void DeathMatchAbsorbMintWitness_PinsTheAbsorbStackOrder()
    {
        // Guards: spendability of the absorb-mint settle leaf — two DIFFERENT oracle signatures (one
        // over the outcome, one over the metadata root) plus the seed reveal, in the one order the
        // gate consumes them.
        PinWitness("0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f202122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f40,03,03,,02,d0d1d2d3d4d5d6d7d8d9dadbdcdddedfe0e1e2e3e4e5e6e7e8e9eaebecedeeef,4142434445464748494a4b4c4d4e4f505152535455565758595a5b5c5d5e5f606162636465666768696a6b6c6d6e6f707172737475767778797a7b7c7d7e7f80", ArkadeCovenants.DeathMatchAbsorbMintWitness(
            OracleSigRoot64(), childGroupIndex: 3, loserInputIndex: 0, winnerInputIndex: 2,
            ServerSeed32(), OracleSigOutcome64()));
    }

    // =============================================================================================
    // Metadata Merkle root — the 32 bytes the breeding oracle actually signs.
    // =============================================================================================

    [Fact]
    public void MetadataMerkleRoot_PinsTheOracleSignedRoot()
    {
        // Guards: every oracle-signed mint. The root binds the child's genome and breed context into
        // the covenant, and the emulator recomputes it in-VM — so this must match the emulator's
        // construction byte for byte or no honest mint verifies.

        // Empty metadata is the documented 32 zero bytes, not a hash of nothing.
        Pin("0000000000000000000000000000000000000000000000000000000000000000", ArkadeCovenants.MetadataMerkleRoot([]));

        // One entry: the root is the leaf itself (no pairing round).
        Pin("05ee4904ef2304e58ab603330d320268980c96a23bfcc254a75f8a0b4641ca1c", ArkadeCovenants.MetadataMerkleRoot([Meta("game", "arkade-heroes")]));

        // Two entries: exactly one pairing round.
        Pin("f420fdc82c000217a5e9153542dc33094da4ad57f34eb10675cefb9fe633f896", ArkadeCovenants.MetadataMerkleRoot(
            [Meta("game", "arkade-heroes"), Meta("genome", "cafebabe0001")]));

        // Three entries: the ODD-NODE branch, where the unpaired node is promoted UNHASHED. This is
        // the rule most likely to differ in a reimplementation (many Merkle variants duplicate the
        // odd node instead), and the child metadata list is odd-length in production.
        Pin("32ecee8a94b315ebc1e4fa5a3c15ecdb3c53070f50081cdcf6bfc35a50ce56bc", ArkadeCovenants.MetadataMerkleRoot(
            [Meta("game", "arkade-heroes"), Meta("genome", "cafebabe0001"), Meta("generation", "1")]));
    }

    // =============================================================================================
    // STRUCTURAL ASSERTIONS.
    //
    // Everything above pins bytes the current implementation produced. The assertions below instead
    // pin facts taken from each builder's DOCUMENTED stack contract — the same contract the named
    // live regtest probe validated against a real emulator. They are what stops this file being
    // purely self-blessing: a migration that changes an introspection opcode, moves it, or drops the
    // asset-id byte reversal fails here as well as on the hex, and re-baking the hex will not help.
    // =============================================================================================

    [Fact]
    public void AssetAtOutput_StructurePinsTheDocumentedOpcodes()
    {
        // Contract (validated live by CovenantStructuralBurnProbe):
        //   <outIdx> <32:txid-internal> <gidx> 0xef(OUTASSETLOOKUP) 0x69(VERIFY) <amount> 0x88
        //   <outIdx> 0xd1(OUTPUTSCRIPTPUBKEY) 0x51 0x88 <32:witness-program> 0x88
        var asset = ParentA();
        var script = ArkadeCovenants.AssetAtOutput(0, asset, Player());

        Assert.Equal(0x00, script[0]);                       // output index 0 BAKED as OP_0 — not an empty push
        Assert.Equal(32, script[1]);                         // push the 32-byte asset txid
        Assert.Equal(asset.Txid.Reverse(), script[2..34]);   // …in INTERNAL (reversed) byte order
        Assert.Equal(0x51, script[34]);                      // group index 1 → OP_1
        Assert.Equal(0xef, script[35]);                      // OP_INSPECTOUTASSETLOOKUP → (amount, flag)
        Assert.Equal(0x69, script[36]);                      // VERIFY the found-flag
        Assert.Equal(0x51, script[37]);                      // amount 1 → OP_1
        Assert.Equal(0x88, script[38]);                      // EQUALVERIFY the amount
        Assert.Equal(0x00, script[39]);                      // the same output index again, for the script check
        Assert.Equal(0xd1, script[40]);                      // OP_INSPECTOUTPUTSCRIPTPUBKEY
        Assert.Equal(0x51, script[41]);                      // witness version 1
        Assert.Equal(0x88, script[42]);                      // EQUALVERIFY the version
        Assert.Equal(32, script[43]);                        // push the 32-byte witness program
        Assert.Equal(Player().ToBytes()[2..], script[44..76]);
        Assert.Equal(0x88, script[76]);                      // EQUALVERIFY the program
        Assert.Equal(77, script.Length);                     // …and nothing else: the script is fully baked

        // The asset id must NOT appear in NArk's display order — that is the reversal bug this catches.
        Assert.DoesNotContain(Hex(asset.Txid), Hex(script));
    }

    [Fact]
    public void MintToPlayer_StructurePinsTheDocumentedOpcodes()
    {
        // Contract (validated live by CovenantMintToPlayerProbe):
        //   <0> 0xed(OUTASSETCOUNT) 0x51 0x88   <0> 0xd1 0x51 0x88 <32:witness-program> 0x88
        var script = ArkadeCovenants.MintToPlayer(Player());

        Assert.Equal(0x00, script[0]);                       // output 0 baked
        Assert.Equal(0xed, script[1]);                       // OP_INSPECTOUTASSETCOUNT
        Assert.Equal(0x51, script[2]);                       // …must equal exactly ONE asset entry
        Assert.Equal(0x88, script[3]);                       // EQUALVERIFY
        Assert.Equal(0x00, script[4]);                       // output 0 again, for the script check
        Assert.Equal(0xd1, script[5]);                       // OP_INSPECTOUTPUTSCRIPTPUBKEY
        Assert.Equal(0x51, script[6]);                       // witness version 1
        Assert.Equal(0x88, script[7]);                       // EQUALVERIFY the version
        Assert.Equal(32, script[8]);                         // push the 32-byte witness program
        Assert.Equal(Player().ToBytes()[2..], script[9..41]);
        Assert.Equal(0x88, script[41]);                      // EQUALVERIFY the program
        Assert.Equal(42, script.Length);                     // consumes NO witness — every operand is baked

        // The whole point of this covenant is that it names NO asset id — the mint's id is unknowable
        // when the covenant is built, which is why it identifies the mint by COUNT. A rewrite that
        // reached for the by-id lookup (0xef) instead would defeat that and is caught here.
        Assert.DoesNotContain((byte)0xef, script);
    }

    [Fact]
    public void ChildAtOutput_StructurePinsTheDocumentedOpcodes()
    {
        // Contract (validated live by CovenantChildAtOutputProbe):
        //   <j=0> <source=1> 0xeb(INSPECTASSETGROUP) 0x75(DROP amount) <vout> 0x88 0x75(DROP marker)
        var script = ArkadeCovenants.ChildAtOutput(1);

        Assert.Equal(0x00, script[0]);   // j = 0 → the group's FIRST output
        Assert.Equal(0x51, script[1]);   // source = 1 → outputs (0 would inspect inputs: wrong side of the tx)
        Assert.Equal(0xeb, script[2]);   // OP_INSPECTASSETGROUP → (1, vout, amount)
        Assert.Equal(0x75, script[3]);   // DROP the amount
        Assert.Equal(0x51, script[4]);   // the expected vout (1 → OP_1)
        Assert.Equal(0x88, script[5]);   // EQUALVERIFY the vout
        Assert.Equal(0x75, script[6]);   // DROP the source marker, leaving a clean stack
        Assert.Equal(7, script.Length);

        // vout 0 must encode as OP_0 (a baked script constant), never as EncodeIndex's empty push.
        Assert.Equal(0x00, ArkadeCovenants.ChildAtOutput(0)[4]);
    }

    [Fact]
    public void AssetAtWitnessOutput_StructurePinsTheDocumentedOpcodes()
    {
        // Contract (validated live by CovenantAssetAtWitnessOutputProbe):
        //   <32:txid-internal> <gidx> 0xef(OUTASSETLOOKUP) 0x69(VERIFY) 0x51 0x88
        var asset = Item();
        var script = ArkadeCovenants.AssetAtWitnessOutput(asset);

        // No baked output index at the front: the index comes from the WITNESS. That absence is the
        // whole difference from AssetAtOutput, so it is asserted as position 0 being the txid push.
        Assert.Equal(32, script[0]);
        Assert.Equal(asset.Txid.Reverse(), script[1..33]);   // INTERNAL (reversed) byte order
        Assert.Equal(0x53, script[33]);                      // group index 3 → OP_3
        Assert.Equal(0xef, script[34]);                      // OP_INSPECTOUTASSETLOOKUP → (amount, flag)
        Assert.Equal(0x69, script[35]);                      // VERIFY the found-flag: present at that output
        Assert.Equal(0x51, script[36]);                      // amount…
        Assert.Equal(0x88, script[37]);                      // …EQUALVERIFY 1
        Assert.Equal(38, script.Length);

        // Presence only — this covenant must NOT pin a scriptPubKey, or an offer becomes unfulfillable.
        Assert.DoesNotContain((byte)0xd1, script);
        Assert.DoesNotContain(Hex(asset.Txid), Hex(script));
    }

    [Fact]
    public void NumAssetGroupsIs_StructurePinsTheDocumentedOpcodes()
    {
        // Contract (validated live by CovenantAssetGroupProbe): 0xe5(NUMASSETGROUPS) <n> 0x88
        var script = ArkadeCovenants.NumAssetGroupsIs(2);

        Assert.Equal(0xe5, script[0]);   // OP_INSPECTNUMASSETGROUPS → the packet's group count
        Assert.Equal(0x52, script[1]);   // n = 2 → OP_2
        Assert.Equal(0x88, script[2]);   // EQUALVERIFY — EXACTLY n, so an extra group cannot ride along
        Assert.Equal(3, script.Length);

        // Above OP_16 the count becomes a length-prefixed data push (0x01 0x11), not an opcode.
        Assert.Equal(new byte[] { 0xe5, 0x01, 0x11, 0x88 }, ArkadeCovenants.NumAssetGroupsIs(17));
    }

    [Fact]
    public void AssetInputSumIs_StructurePinsTheDocumentedOpcodes()
    {
        // Contract (validated live by CovenantAssetGroupProbe):
        //   <32:txid-internal> <gidx> 0xe8(FINDASSETGROUP) 0x69(VERIFY) <source=0> 0xec(GROUPSUM) <n> 0x88
        var asset = Item();
        var script = ArkadeCovenants.AssetInputSumIs(asset, 3);

        Assert.Equal(32, script[0]);
        Assert.Equal(asset.Txid.Reverse(), script[1..33]);   // INTERNAL (reversed) byte order
        Assert.Equal(0x53, script[33]);                      // group index 3 → OP_3
        Assert.Equal(0xe8, script[34]);                      // OP_FINDASSETGROUPBYASSETID → (k, found)
        Assert.Equal(0x69, script[35]);                      // VERIFY found, leaving k
        Assert.Equal(0x00, script[36]);                      // source = 0 → INPUT sum (1 would sum outputs)
        Assert.Equal(0xec, script[37]);                      // OP_INSPECTASSETGROUPSUM → the sum
        Assert.Equal(0x53, script[38]);                      // the baked expected amount, 3 → OP_3
        Assert.Equal(0x88, script[39]);                      // EQUALVERIFY
        Assert.Equal(40, script.Length);

        Assert.DoesNotContain(Hex(asset.Txid), Hex(script));
    }
}
