using ArkadeHeroes.Chain.Covenants;
using ArkadeHeroes.Chain.NArk;
using NArk.Abstractions;
using NArk.Abstractions.Assets;
using NArk.Core.Assets;
using NBitcoin;

namespace ArkadeHeroes.Tests.E2E;

/// <summary>
/// The structural TEETH of the breed covenant-v2 gate, live on regtest. Unlike
/// <see cref="CovenantBreedFlowE2ETests"/> (the honest happy path through the game
/// server) and <see cref="CovenantBreedProbeTests"/> (which exercises the OLD
/// <c>BreedAuthorized</c> gate), this builds the breed escrow directly (a funder-issued
/// species + a test oracle) so it can craft malicious packets and prove the emulator
/// REFUSES each cheat on the COMPOSED <c>BreedRetainAuthorized</c> gate: stealing a
/// parent instead of retaining it (AssetAtOutput), routing the child to a thief instead
/// of the player (ChildAtOutput 0xeb), and — on the introspection-bound refund — routing
/// a deposited parent to a thief (AssetAtOutput). The honest breed + honest refund
/// co-sign. This proves BreedRetainAuthorized is packet-trustless end to end.
/// </summary>
public class CovenantBreedStructuralProbeTests : IAsyncLifetime
{
    private static readonly Uri EmulatorUri = new("http://localhost:7073/");
    private const long Fee = 1_000;

    private SelfCustodyWallet _funder = null!;   // the player (parent-retention + child-mint + refund home)
    private SelfCustodyWallet _treasury = null!; // the fee destination (distinct address)
    private SelfCustodyWallet _thief = null!;    // the theft destination
    private readonly List<string> _dbPaths = [];

    public async Task InitializeAsync()
    {
        await RegtestHelper.WaitForArkdReadyAsync(TimeSpan.FromSeconds(30));
        _funder = await NewWalletAsync();
        _treasury = await NewWalletAsync();
        _thief = await NewWalletAsync();
        await RegtestHelper.ArkSend(_funder.Address, 300_000);
        await _funder.WaitForBalanceAsync(300_000, TimeSpan.FromSeconds(60));
    }

    private async Task<SelfCustodyWallet> NewWalletAsync()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"ah-breedstruct-{Guid.NewGuid():N}.db");
        _dbPaths.Add(dbPath);
        return await SelfCustodyWallet.CreateAsync(new SelfCustodyWalletOptions
        {
            ArkUri = "http://localhost:7070",
            DbPath = dbPath,
        });
    }

    public async Task DisposeAsync()
    {
        await _funder.DisposeAsync();
        await _treasury.DisposeAsync();
        await _thief.DisposeAsync();
        foreach (var p in _dbPaths)
            try { if (File.Exists(p)) File.Delete(p); } catch { /* windows lock */ }
    }

    private static (byte[] Priv, byte[] Pub) NewOracle()
    {
        var key = NBitcoin.Secp256k1.ECPrivKey.Create(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        Span<byte> pk = stackalloc byte[32];
        key.CreateXOnlyPubKey().WriteToSpan(pk);
        var priv = new byte[32];
        key.WriteToSpan(priv);
        return (priv, pk.ToArray());
    }

    private async Task<string> IssueAsync()
    {
        var mgr = _funder.GetService<IAssetManager>();
        var res = await mgr.IssueAsync(_funder.WalletId, new IssuanceParams(Amount: 1));
        await _funder.WaitForAssetAsync(res.AssetId, TimeSpan.FromSeconds(30));
        return res.AssetId;
    }

    [Fact]
    public async Task Breed_HonestCoSigned_ParentTheft_And_ChildTheft_Refused()
    {
        var transport = _funder.GetService<global::NArk.Core.Transport.IClientTransport>();
        var serverInfo = await transport.GetServerInfoAsync();
        var emulatorInfo = await EmulatorEndpoint.Client(EmulatorUri).GetInfoAsync();
        var isMain = serverInfo.Network == Network.Main;
        var dust = serverInfo.Dust.Satoshi;

        // A species control asset (the child is issued under it) + two parent heroes.
        var speciesId = await IssueAsync();
        var parentAId = await IssueAsync();
        var parentBId = await IssueAsync();
        var species = AssetId.FromString(speciesId);
        var parentA = AssetId.FromString(parentAId);
        var parentB = AssetId.FromString(parentBId);
        var playerScript = ArkAddress.Parse(_funder.Address).ScriptPubKey;
        var treasuryScript = ArkAddress.Parse(_treasury.Address).ScriptPubKey;
        var thiefScript = ArkAddress.Parse(_thief.Address).ScriptPubKey;

        var (oraclePriv, oraclePk) = NewOracle();
        const string breedingId = "e2e-breed-probe";
        var refundAfter = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(); // refund tested in the 2nd fact

        var parameters = new BreedEscrowParams(
            _funder.Address, parentAId, parentBId, speciesId, _treasury.Address,
            Fee, dust, Convert.ToHexString(oraclePk).ToLowerInvariant(), breedingId, refundAfter);
        var contract = BreedEscrowContracts.Build(parameters, serverInfo.SignerKey, emulatorInfo.SignerPubkey);
        var contractAddr = contract.GetArkAddress().ToString(isMain);

        // Deposit both parents + the fee (same shape as the game's breed escrow).
        await _funder.SendAssetAsync(contractAddr, parentAId, 1);
        await _funder.SendAssetAsync(contractAddr, parentBId, 1);
        await _funder.SendAsync(contractAddr, Fee);
        var vtxos = await CovenantSpender.WaitForVtxosAsync(_funder, contract, 3, TimeSpan.FromSeconds(60));
        var parentAVtxo = vtxos.First(v => v.Assets?.Any(a => a.AssetId == parentAId) == true);
        var parentBVtxo = vtxos.First(v => v.Assets?.Any(a => a.AssetId == parentBId) == true);
        var feeVtxo = vtxos.First(v => v.Assets is null or { Count: 0 });
        var ordered = new[] { parentAVtxo, parentBVtxo, feeVtxo }; // parentA vin0, parentB vin1, fee vin2
        var total = ordered.Sum(v => (long)v.Amount);

        // The oracle attests the child metadata root (as the game's receipt key does).
        var childMeta = BreedEscrowContracts.ChildMetadata(
            new string('a', 64), 1, parentAId, parentBId, new string('b', 64), "probe-nonce");
        var root = ArkadeCovenants.MetadataMerkleRoot(childMeta);
        var oracleSig = new byte[64];
        NBitcoin.Secp256k1.ECPrivKey.Create(oraclePriv).SignBIP340(root).WriteToSpan(oracleSig);

        // Every input spends the "breed" leaf with the breed-retain witness (childGroup 2, fee
        // out 1, parentA vin 0, parentB vin 1) — identical to ExecuteBreedCovenantAsync.
        CovenantSpender.CovenantInput[] BreedInputs() => ordered.Select(v =>
            new CovenantSpender.CovenantInput(contract, "breed",
                ArkadeCovenants.BreedRetainWitness(oracleSig, 2, feeOutputIndex: 1, 0, 1), v)).ToArray();

        AssetGroup ParentPt(AssetId id, ushort vin, ushort vout) => AssetGroup.Create(
            id, null, [AssetInput.Create(vin, 1)], [AssetOutput.Create(vout, 1)], []);
        AssetGroup Child(ushort vout) => AssetGroup.Create(
            null, AssetRef.FromId(species), [], [AssetOutput.Create(vout, 1)], childMeta);

        // ── Cheat: STEAL parentA — route it to a thief output (2) instead of the player at 0
        //    → AssetAtOutput(0, parentA, player) refuses.
        var parentTheft = await Assert.ThrowsAnyAsync<Exception>(() => CovenantSpender.SpendManyAsync(
            _funder, EmulatorUri, BreedInputs(),
            [
                new TxOut(Money.Satoshis(total - Fee - dust), playerScript),  // parentB + child (out 0)
                new TxOut(Money.Satoshis(Fee), treasuryScript),               // fee (out 1)
                new TxOut(Money.Satoshis(dust), thiefScript),                 // stolen parentA (out 2)
            ],
            extraPackets: [Packet.Create([ParentPt(parentA, 0, 2), ParentPt(parentB, 1, 0), Child(0)])]));
        Assert.Contains("Emulator tx failed", parentTheft.Message);

        // ── Cheat: STEAL the child — route it to a thief output (2) instead of the player at 0
        //    → ChildAtOutput(0) refuses (the child group's vout != 0).
        var childTheft = await Assert.ThrowsAnyAsync<Exception>(() => CovenantSpender.SpendManyAsync(
            _funder, EmulatorUri, BreedInputs(),
            [
                new TxOut(Money.Satoshis(total - Fee - dust), playerScript),  // parents (out 0)
                new TxOut(Money.Satoshis(Fee), treasuryScript),               // fee (out 1)
                new TxOut(Money.Satoshis(dust), thiefScript),                 // stolen child (out 2)
            ],
            extraPackets: [Packet.Create([ParentPt(parentA, 0, 0), ParentPt(parentB, 1, 0), Child(2)])]));
        Assert.Contains("Emulator tx failed", childTheft.Message);

        // ── Honest breed: both parents retained → player (output 0), child minted → player
        //    (output 0), fee → treasury (output 1) → co-signed.
        var response = await CovenantSpender.SpendManyAsync(
            _funder, EmulatorUri, BreedInputs(),
            [new TxOut(Money.Satoshis(total - Fee), playerScript), new TxOut(Money.Satoshis(Fee), treasuryScript)],
            extraPackets: [Packet.Create([ParentPt(parentA, 0, 0), ParentPt(parentB, 1, 0), Child(0)])]);
        Assert.False(string.IsNullOrEmpty(response.SignedArkTx), "the honest breed must be emulator-co-signed");
    }

    /// <summary>
    /// THE GATE for "a breed must consume both parents as inputs".
    ///
    /// <para>BreedRetainAuthorized checks only OUTPUTS — AssetAtOutput for each parent and ChildAtOutput
    /// for the child. Nothing in the leaf requires either parent vtxo to be spent. Read literally, a breed
    /// could satisfy every check while leaving the escrowed parents untouched, then reclaim them after
    /// expiry: the child gets minted for free and the parents survive.</para>
    ///
    /// <para>Whether that is reachable is the question this answers. The parents are UNIQUE single-unit
    /// assets sitting in the escrow, so an attacker has nowhere else to source them from — if the asset
    /// layer enforces conservation, an output claiming a parent with no corresponding input is already
    /// invalid and the leaf needs nothing. If instead the emulator co-signs this, the hole is real.</para>
    ///
    /// <para>Either answer is worth having in the suite permanently: it pins WHY the output-only leaf is
    /// safe, so a future change to asset conservation cannot silently reopen it.</para>
    /// </summary>
    [Fact]
    public async Task Breed_WithoutConsumingTheParents_IsRefused()
    {
        var transport = _funder.GetService<global::NArk.Core.Transport.IClientTransport>();
        var serverInfo = await transport.GetServerInfoAsync();
        var emulatorInfo = await EmulatorEndpoint.Client(EmulatorUri).GetInfoAsync();
        var isMain = serverInfo.Network == Network.Main;
        var dust = serverInfo.Dust.Satoshi;

        var speciesId = await IssueAsync();
        var parentAId = await IssueAsync();
        var parentBId = await IssueAsync();
        var species = AssetId.FromString(speciesId);
        var parentA = AssetId.FromString(parentAId);
        var parentB = AssetId.FromString(parentBId);
        var playerScript = ArkAddress.Parse(_funder.Address).ScriptPubKey;
        var treasuryScript = ArkAddress.Parse(_treasury.Address).ScriptPubKey;

        var (oraclePriv, oraclePk) = NewOracle();
        const string breedingId = "e2e-breed-noconsume";
        var refundAfter = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();

        var parameters = new BreedEscrowParams(
            _funder.Address, parentAId, parentBId, speciesId, _treasury.Address,
            Fee, dust, Convert.ToHexString(oraclePk).ToLowerInvariant(), breedingId, refundAfter);
        var contract = BreedEscrowContracts.Build(parameters, serverInfo.SignerKey, emulatorInfo.SignerPubkey);
        var contractAddr = contract.GetArkAddress().ToString(isMain);

        await _funder.SendAssetAsync(contractAddr, parentAId, 1);
        await _funder.SendAssetAsync(contractAddr, parentBId, 1);
        await _funder.SendAsync(contractAddr, Fee);
        var vtxos = await CovenantSpender.WaitForVtxosAsync(_funder, contract, 3, TimeSpan.FromSeconds(60));
        var feeVtxo = vtxos.First(v => v.Assets is null or { Count: 0 });

        var childMeta = BreedEscrowContracts.ChildMetadata(
            new string('a', 64), 1, parentAId, parentBId, new string('b', 64), "probe-nonce");
        var root = ArkadeCovenants.MetadataMerkleRoot(childMeta);
        var oracleSig = new byte[64];
        NBitcoin.Secp256k1.ECPrivKey.Create(oraclePriv).SignBIP340(root).WriteToSpan(oracleSig);

        // ONLY the fee vtxo is spent. Both parent vtxos stay in the escrow, unspent and reclaimable —
        // yet the packet still declares each parent as arriving at output 0, with NO input backing it.
        var feeOnly = new[]
        {
            new CovenantSpender.CovenantInput(contract, "breed",
                ArkadeCovenants.BreedRetainWitness(oracleSig, 2, feeOutputIndex: 1, 0, 1), feeVtxo),
        };
        AssetGroup Unbacked(AssetId id, ushort vout) => AssetGroup.Create(
            id, null, [], [AssetOutput.Create(vout, 1)], []);   // no inputs — that is the whole cheat
        AssetGroup Child(ushort vout) => AssetGroup.Create(
            null, AssetRef.FromId(species), [], [AssetOutput.Create(vout, 1)], childMeta);

        var refused = await Assert.ThrowsAnyAsync<Exception>(() => CovenantSpender.SpendManyAsync(
            _funder, EmulatorUri, feeOnly,
            [
                new TxOut(Money.Satoshis((long)feeVtxo.Amount - Fee), playerScript),
                new TxOut(Money.Satoshis(Fee), treasuryScript),
            ],
            extraPackets: [Packet.Create([Unbacked(parentA, 0), Unbacked(parentB, 0), Child(0)])]));

        // RESULT: refused. So the output-only leaf is not exploitable this way — a breed cannot mint a
        // child while leaving the escrowed parents intact, and BreedRetainAuthorized needs no input-side
        // check to make that true.
        //
        // CAVEAT on what this proves. The spend differs from the honest one in TWO ways: the parents are
        // unbacked AND only the fee vtxo is consumed. Either could be the reason it was rejected, so this
        // pins the BEHAVIOUR (the attack fails) without isolating the MECHANISM (asset conservation vs
        // something about the thin input set). Worth tightening with a control that consumes all three
        // inputs while still declaring a parent unbacked — until then, do not cite this as proof that
        // conservation is what closes the hole.
        //
        // The refusal must come from the chain/emulator, not from our own builder tripping over itself.
        Assert.Contains("Emulator tx failed", refused.Message);
    }

    [Fact]
    public async Task BreedRefund_BothParentsHomeAfterExpiry_TheftRefused_PreExpiryRefused()
    {
        var transport = _funder.GetService<global::NArk.Core.Transport.IClientTransport>();
        var serverInfo = await transport.GetServerInfoAsync();
        var emulatorInfo = await EmulatorEndpoint.Client(EmulatorUri).GetInfoAsync();
        var isMain = serverInfo.Network == Network.Main;
        var dust = serverInfo.Dust.Satoshi;

        var parentAId = await IssueAsync();
        var parentBId = await IssueAsync();
        var parentA = AssetId.FromString(parentAId);
        var parentB = AssetId.FromString(parentBId);
        var playerScript = ArkAddress.Parse(_funder.Address).ScriptPubKey;
        var thiefScript = ArkAddress.Parse(_thief.Address).ScriptPubKey;

        // The refund leaf ignores the oracle/species (it is AssetAtOutput x2), so dummy values suffice.
        var (_, oraclePk) = NewOracle();
        const string breedingId = "e2e-breed-refund-probe";
        var refundAfter = DateTimeOffset.UtcNow.AddSeconds(8).ToUnixTimeSeconds();

        var parameters = new BreedEscrowParams(
            _funder.Address, parentAId, parentBId, parentAId /* dummy species */, _treasury.Address,
            Fee, dust, Convert.ToHexString(oraclePk).ToLowerInvariant(), breedingId, refundAfter);
        var contract = BreedEscrowContracts.Build(parameters, serverInfo.SignerKey, emulatorInfo.SignerPubkey);
        var contractAddr = contract.GetArkAddress().ToString(isMain);

        await _funder.SendAssetAsync(contractAddr, parentAId, 1);
        await _funder.SendAssetAsync(contractAddr, parentBId, 1);
        await _funder.SendAsync(contractAddr, Fee);                 // the fee sats VTXO the real escrow also holds
        var vtxos = await CovenantSpender.WaitForVtxosAsync(_funder, contract, 3, TimeSpan.FromSeconds(60));
        var parentAVtxo = vtxos.First(v => v.Assets?.Any(a => a.AssetId == parentAId) == true);
        var parentBVtxo = vtxos.First(v => v.Assets?.Any(a => a.AssetId == parentBId) == true);
        var feeVtxo = vtxos.First(v => v.OutPoint != parentAVtxo.OutPoint && v.OutPoint != parentBVtxo.OutPoint);
        var total = (long)parentAVtxo.Amount + (long)parentBVtxo.Amount + (long)feeVtxo.Amount;

        // The refund routes BOTH parents to output 0 (the player) — one output, two assets. All
        // THREE escrow VTXOs spend the refund leaf; the fee sats ride output 0 as value.
        // parentA vin0, parentB vin1, fee vin2.
        CovenantSpender.CovenantInput[] RefundInputs(long lockTime) =>
        [
            new(contract, "refund", [], parentAVtxo, LockTime: new LockTime((uint)lockTime)),
            new(contract, "refund", [], parentBVtxo, LockTime: new LockTime((uint)lockTime)),
            new(contract, "refund", [], feeVtxo, LockTime: new LockTime((uint)lockTime)),
        ];
        Packet RefundPacket() => Packet.Create(
        [
            AssetGroup.Create(parentA, null, [AssetInput.Create(0, 1)], [AssetOutput.Create(0, 1)], []),
            AssetGroup.Create(parentB, null, [AssetInput.Create(1, 1)], [AssetOutput.Create(0, 1)], []),
        ]);
        TxOut[] HomeOutputs() => [new TxOut(Money.Satoshis(total), playerScript)];

        // Before expiry: refused by the leaf CLTV. Disposable non-canonical locktime (expiry+1).
        var early = await Assert.ThrowsAnyAsync<Exception>(() => CovenantSpender.SpendManyAsync(
            _funder, EmulatorUri, RefundInputs(refundAfter + 1), HomeOutputs(), extraPackets: [RefundPacket()]));
        Assert.False(early is TimeoutException, $"expected a refusal, got: {early.Message}");

        await RegtestHelper.WaitForChainTimeAsync(refundAfter, TimeSpan.FromSeconds(120));

        // THEFT: route output 0 (both parents) to the thief → AssetAtOutput(0, parentA, player)
        // refuses. Distinct output → distinct txid (disposable).
        var theft = await Assert.ThrowsAnyAsync<Exception>(() => CovenantSpender.SpendManyAsync(
            _funder, EmulatorUri, RefundInputs(refundAfter),
            [new TxOut(Money.Satoshis(total), thiefScript)],
            extraPackets: [RefundPacket()]));
        Assert.Contains("Emulator tx failed", theft.Message);

        // Canonical honest refund (submit exactly ONCE): both parents home, co-signed.
        var refund = await CovenantSpender.SpendManyAsync(
            _funder, EmulatorUri, RefundInputs(refundAfter), HomeOutputs(), extraPackets: [RefundPacket()]);
        Assert.False(string.IsNullOrEmpty(refund.SignedArkTx), "the post-expiry breed refund must be emulator-co-signed");
    }
}
