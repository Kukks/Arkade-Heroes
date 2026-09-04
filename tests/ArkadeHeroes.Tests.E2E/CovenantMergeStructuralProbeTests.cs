using ArkadeHeroes.Chain.Covenants;
using ArkadeHeroes.Chain.NArk;
using NArk.Abstractions;
using NArk.Abstractions.Assets;
using NArk.Core.Assets;
using NBitcoin;

namespace ArkadeHeroes.Tests.E2E;

/// <summary>
/// The structural TEETH of the merge covenant-v2 gate, live on regtest. Unlike
/// <see cref="CovenantMergeFlowE2ETests"/> (the honest happy path through the game
/// server), this builds the merge escrow directly (a funder-issued species + a test
/// oracle) so it can craft malicious packets and prove the emulator REFUSES each cheat
/// on the COMPOSED gate: paying the fused hero to the wrong script (MintToPlayer 0xd1),
/// keeping a "burned" input alive (AssetBurned), and — on the introspection-bound refund
/// — routing a deposited hero to a thief (AssetAtOutput). The honest merge + honest
/// refund co-sign. This proves MergeAuthorized is packet-trustless end to end.
/// </summary>
public class CovenantMergeStructuralProbeTests : IAsyncLifetime
{
    private static readonly Uri EmulatorUri = new("http://localhost:7073/");
    private const long Fee = 1_000;

    private SelfCustodyWallet _funder = null!;   // the player (mint destination + refund home)
    private SelfCustodyWallet _treasury = null!; // the fee destination (distinct address)
    private SelfCustodyWallet _thief = null!;    // the wrong-script / theft destination
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
        var dbPath = Path.Combine(Path.GetTempPath(), $"ah-mergeprobe-{Guid.NewGuid():N}.db");
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

    private async Task<(SelfCustodyWallet W, string AssetId)> IssueAsync(bool underControl = false, string? control = null)
    {
        var mgr = _funder.GetService<IAssetManager>();
        var res = await mgr.IssueAsync(_funder.WalletId,
            new IssuanceParams(Amount: 1, ControlAssetId: underControl ? control : null));
        await _funder.WaitForAssetAsync(res.AssetId, TimeSpan.FromSeconds(30));
        return (_funder, res.AssetId);
    }

    [Fact]
    public async Task Merge_HonestCoSigned_MintTheft_BurnedInputResale_And_RefundTheft_Refused()
    {
        var transport = _funder.GetService<global::NArk.Core.Transport.IClientTransport>();
        var serverInfo = await transport.GetServerInfoAsync();
        var emulatorInfo = await EmulatorEndpoint.Client(EmulatorUri).GetInfoAsync();
        var isMain = serverInfo.Network == Network.Main;
        var dust = serverInfo.Dust.Satoshi;

        // A species control asset (the mint is issued under it) + two input heroes to fuse.
        var (_, speciesId) = await IssueAsync();
        var (_, baseId) = await IssueAsync();
        var (_, sacrificeId) = await IssueAsync();
        var species = AssetId.FromString(speciesId);
        var baseAsset = AssetId.FromString(baseId);
        var sacrificeAsset = AssetId.FromString(sacrificeId);
        var playerScript = ArkAddress.Parse(_funder.Address).ScriptPubKey;
        var treasuryScript = ArkAddress.Parse(_treasury.Address).ScriptPubKey;
        var thiefScript = ArkAddress.Parse(_thief.Address).ScriptPubKey;

        var (oraclePriv, oraclePk) = NewOracle();
        const string mergeId = "e2e-merge-probe";
        var refundAfter = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(); // refund tested in the 2nd fact

        var parameters = new MergeEscrowParams(
            _funder.Address, baseId, sacrificeId, speciesId, _treasury.Address,
            Fee, dust, Convert.ToHexString(oraclePk).ToLowerInvariant(), mergeId, refundAfter);
        var contract = MergeEscrowContracts.Build(parameters, serverInfo.SignerKey, emulatorInfo.SignerPubkey);
        var contractAddr = contract.GetArkAddress().ToString(isMain);

        // Deposit base + sacrifice + the fee (same shape as the game's merge escrow).
        await _funder.SendAssetAsync(contractAddr, baseId, 1);
        await _funder.SendAssetAsync(contractAddr, sacrificeId, 1);
        await _funder.SendAsync(contractAddr, Fee);
        var vtxos = await CovenantSpender.WaitForVtxosAsync(_funder, contract, 3, TimeSpan.FromSeconds(60));
        var baseVtxo = vtxos.First(v => v.Assets?.Any(a => a.AssetId == baseId) == true);
        var sacVtxo = vtxos.First(v => v.Assets?.Any(a => a.AssetId == sacrificeId) == true);
        var feeVtxo = vtxos.First(v => v.Assets is null or { Count: 0 });
        var ordered = new[] { baseVtxo, sacVtxo, feeVtxo };
        var total = ordered.Sum(v => (long)v.Amount);

        // The oracle attests the fused metadata root (as the game's receipt key does).
        var fusedMeta = BreedEscrowContracts.ChildMetadata(
            new string('a', 64), 1, baseId, sacrificeId, new string('b', 64), "probe-nonce");
        var root = ArkadeCovenants.MetadataMerkleRoot(fusedMeta);
        var oracleSig = new byte[64];
        NBitcoin.Secp256k1.ECPrivKey.Create(oraclePriv).SignBIP340(root).WriteToSpan(oracleSig);

        // Every input spends the "merge" leaf with the breed witness (childGroup 2, fee out 1,
        // base vin 0, sacrifice vin 1) — identical to ExecuteMergeAsync.
        CovenantSpender.CovenantInput[] MergeInputs() => ordered.Select(v =>
            new CovenantSpender.CovenantInput(contract, "merge",
                ArkadeCovenants.BreedWitness(oracleSig, 2, feeOutputIndex: 1, 0, 1), v)).ToArray();

        AssetGroup MintGroup(ushort vout) => AssetGroup.Create(
            null, AssetRef.FromId(species), [], [AssetOutput.Create(vout, 1)], fusedMeta);

        // ── Cheat: pay the FUSED hero to the thief (output 0) → MintToPlayer 0xd1 refuses.
        var mintTheft = await Assert.ThrowsAnyAsync<Exception>(() => CovenantSpender.SpendManyAsync(
            _funder, EmulatorUri, MergeInputs(),
            [new TxOut(Money.Satoshis(total - Fee), thiefScript), new TxOut(Money.Satoshis(Fee), treasuryScript)],
            extraPackets: [Packet.Create(
            [
                AssetGroup.Create(baseAsset, null, [AssetInput.Create(0, 1)], [], []),
                AssetGroup.Create(sacrificeAsset, null, [AssetInput.Create(1, 1)], [], []),
                MintGroup(0),
            ])]));
        Assert.Contains("Emulator tx failed", mintTheft.Message);

        // ── Cheat: keep the "burned" base ALIVE — route it to output 2 → AssetBurned refuses.
        var resale = await Assert.ThrowsAnyAsync<Exception>(() => CovenantSpender.SpendManyAsync(
            _funder, EmulatorUri, MergeInputs(),
            [
                new TxOut(Money.Satoshis(total - Fee - dust), playerScript),
                new TxOut(Money.Satoshis(Fee), treasuryScript),
                new TxOut(Money.Satoshis(dust), thiefScript),   // base smuggled out, kept alive
            ],
            extraPackets: [Packet.Create(
            [
                AssetGroup.Create(baseAsset, null, [AssetInput.Create(0, 1)], [AssetOutput.Create(2, 1)], []), // NOT burned
                AssetGroup.Create(sacrificeAsset, null, [AssetInput.Create(1, 1)], [], []),
                MintGroup(0),
            ])]));
        Assert.Contains("Emulator tx failed", resale.Message);

        // ── Honest merge: base + sacrifice burned, fused → player (output 0), fee → treasury.
        var response = await CovenantSpender.SpendManyAsync(
            _funder, EmulatorUri, MergeInputs(),
            [new TxOut(Money.Satoshis(total - Fee), playerScript), new TxOut(Money.Satoshis(Fee), treasuryScript)],
            extraPackets: [Packet.Create(
            [
                AssetGroup.Create(baseAsset, null, [AssetInput.Create(0, 1)], [], []),
                AssetGroup.Create(sacrificeAsset, null, [AssetInput.Create(1, 1)], [], []),
                MintGroup(0),
            ])]);
        Assert.False(string.IsNullOrEmpty(response.SignedArkTx), "the honest merge must be emulator-co-signed");
    }

    [Fact]
    public async Task MergeRefund_EachHeroHomeAfterExpiry_TheftRefused_PreExpiryRefused()
    {
        var transport = _funder.GetService<global::NArk.Core.Transport.IClientTransport>();
        var serverInfo = await transport.GetServerInfoAsync();
        var emulatorInfo = await EmulatorEndpoint.Client(EmulatorUri).GetInfoAsync();
        var isMain = serverInfo.Network == Network.Main;
        var dust = serverInfo.Dust.Satoshi;

        var (_, baseId) = await IssueAsync();
        var (_, sacrificeId) = await IssueAsync();
        var baseAsset = AssetId.FromString(baseId);
        var sacrificeAsset = AssetId.FromString(sacrificeId);
        var playerScript = ArkAddress.Parse(_funder.Address).ScriptPubKey;
        var thiefScript = ArkAddress.Parse(_thief.Address).ScriptPubKey;

        // The refund leaf ignores the oracle/species (it is AssetAtOutput x2), so dummy values suffice.
        var (_, oraclePk) = NewOracle();
        const string mergeId = "e2e-merge-refund-probe";
        var refundAfter = DateTimeOffset.UtcNow.AddSeconds(8).ToUnixTimeSeconds();

        var parameters = new MergeEscrowParams(
            _funder.Address, baseId, sacrificeId, baseId /* dummy species */, _treasury.Address,
            Fee, dust, Convert.ToHexString(oraclePk).ToLowerInvariant(), mergeId, refundAfter);
        var contract = MergeEscrowContracts.Build(parameters, serverInfo.SignerKey, emulatorInfo.SignerPubkey);
        var contractAddr = contract.GetArkAddress().ToString(isMain);

        await _funder.SendAssetAsync(contractAddr, baseId, 1);
        await _funder.SendAssetAsync(contractAddr, sacrificeId, 1);
        await _funder.SendAsync(contractAddr, Fee);                 // the fee sats VTXO the real escrow also holds
        var vtxos = await CovenantSpender.WaitForVtxosAsync(_funder, contract, 3, TimeSpan.FromSeconds(60));
        var baseVtxo = vtxos.First(v => v.Assets?.Any(a => a.AssetId == baseId) == true);
        var sacVtxo = vtxos.First(v => v.Assets?.Any(a => a.AssetId == sacrificeId) == true);
        var feeVtxo = vtxos.First(v => v.OutPoint != baseVtxo.OutPoint && v.OutPoint != sacVtxo.OutPoint);
        var total = (long)baseVtxo.Amount + (long)sacVtxo.Amount + (long)feeVtxo.Amount;

        // Refund routes BOTH heroes to output 0 (the player) — one output, two assets, one
        // owner (two player-paying outputs would be coalesced by the builder). All THREE
        // escrow VTXOs spend the refund leaf; the fee sats ride output 0 as value.
        // base vin0, sacrifice vin1, fee vin2.
        CovenantSpender.CovenantInput[] RefundInputs(long lockTime) =>
        [
            new(contract, "refund", [], baseVtxo, LockTime: new LockTime((uint)lockTime)),
            new(contract, "refund", [], sacVtxo, LockTime: new LockTime((uint)lockTime)),
            new(contract, "refund", [], feeVtxo, LockTime: new LockTime((uint)lockTime)),
        ];
        Packet RefundPacket() => Packet.Create(
        [
            AssetGroup.Create(baseAsset, null, [AssetInput.Create(0, 1)], [AssetOutput.Create(0, 1)], []),
            AssetGroup.Create(sacrificeAsset, null, [AssetInput.Create(1, 1)], [AssetOutput.Create(0, 1)], []),
        ]);
        TxOut[] HomeOutputs() => [new TxOut(Money.Satoshis(total), playerScript)];

        // Before expiry: refused by the leaf CLTV. Disposable non-canonical locktime (expiry+1)
        // so a refused submit never poisons the canonical refund txid (arkd sticky-failure bug).
        var early = await Assert.ThrowsAnyAsync<Exception>(() => CovenantSpender.SpendManyAsync(
            _funder, EmulatorUri, RefundInputs(refundAfter + 1), HomeOutputs(), extraPackets: [RefundPacket()]));
        Assert.False(early is TimeoutException, $"expected a refusal, got: {early.Message}");

        await RegtestHelper.WaitForChainTimeAsync(refundAfter, TimeSpan.FromSeconds(120));

        // THEFT: route output 0 (both heroes) to the thief → AssetAtOutput(0, base, player)
        // refuses (0xd1 output-script). Distinct output → distinct txid (disposable).
        var theft = await Assert.ThrowsAnyAsync<Exception>(() => CovenantSpender.SpendManyAsync(
            _funder, EmulatorUri, RefundInputs(refundAfter),
            [new TxOut(Money.Satoshis(total), thiefScript)],
            extraPackets: [RefundPacket()]));
        Assert.Contains("Emulator tx failed", theft.Message);

        // Canonical honest refund (submit exactly ONCE): each hero home, co-signed.
        var refund = await CovenantSpender.SpendManyAsync(
            _funder, EmulatorUri, RefundInputs(refundAfter), HomeOutputs(), extraPackets: [RefundPacket()]);
        Assert.False(string.IsNullOrEmpty(refund.SignedArkTx), "the post-expiry merge refund must be emulator-co-signed");
    }
}
