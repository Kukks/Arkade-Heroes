using ArkadeHeroes.Chain.Covenants;
using ArkadeHeroes.Chain.NArk;
using NArk.Core.Assets;
using NBitcoin;

namespace ArkadeHeroes.Tests.E2E;

/// <summary>
/// Probe ladder for covenant breeding (docs/plans/19-backlog.md §19). Rung 1:
/// prove arkd + emulator accept a FRESH ISSUANCE asset group in a covenant-leaf
/// spend submitted via the co-sign path, with the asset packet and the
/// EmulatorPacket composed into ONE extension output — and that the
/// EmulatorPacket survives transaction construction byte-for-byte (the
/// builder's asset-vin remap path rebuilds extension outputs from the asset
/// packet alone; see the packet-composition hazard note).
/// </summary>
public class CovenantBreedProbeTests : IAsyncLifetime
{
    private static readonly Uri EmulatorUri = new("http://localhost:7073/");

    private SelfCustodyWallet _funder = null!;
    private string _dbPath = null!;

    public async Task InitializeAsync()
    {
        await RegtestHelper.WaitForArkdReadyAsync(TimeSpan.FromSeconds(30));
        _dbPath = Path.Combine(Path.GetTempPath(), $"ah-breedprobe-{Guid.NewGuid():N}.db");
        _funder = await SelfCustodyWallet.CreateAsync(new SelfCustodyWalletOptions
        {
            ArkUri = "http://localhost:7070",
            DbPath = _dbPath,
        });
        await RegtestHelper.ArkSend(_funder.Address, 50_000);
        await _funder.WaitForBalanceAsync(50_000, TimeSpan.FromSeconds(60));
    }

    public async Task DisposeAsync()
    {
        await _funder.DisposeAsync();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* windows lock */ }
    }

    [Fact]
    public async Task IssuanceUnderCovenant_CoSigned_AssetMaterializes_PacketSurvives()
    {
        var transport = _funder.GetService<global::NArk.Core.Transport.IClientTransport>();
        var serverInfo = await transport.GetServerInfoAsync();
        var emulatorInfo = await new EmulatorClient(EmulatorUri).GetInfoAsync();

        // Covenant VTXO whose single leaf runs a trivially-true arkade script
        // (OP_TRUE) — the probe isolates the ISSUANCE question from covenant
        // logic. Witness: empty.
        byte[] opTrue = [0x51];
        var contract = new ArkadeArtifactContract(
            "breed-probe", serverInfo.SignerKey, emulatorInfo.SignerPubkey,
            [new ArkadeContractFunction("probe", opTrue)]);
        var address = contract.GetArkAddress().ToString(serverInfo.Network == Network.Main);

        const long fund = 20_000;
        await _funder.SendAsync(address, fund);
        var vtxo = await CovenantSpender.WaitForVtxoAsync(_funder, contract, TimeSpan.FromSeconds(45));

        // Fresh issuance group, exactly the AssetManager.IssueAsync shape:
        // no AssetId (identity = (this txid, group 0)), no control asset,
        // amount 1 to vout 0, genome-style metadata.
        var metadata = new List<AssetMetadata>
        {
            AssetMetadata.Create("breed", "arkade-heroes-breed-v1|probe"),
            AssetMetadata.Create("genome", "cafebabe00000001"),
        };
        var issuance = AssetGroup.Create(
            assetId: null,
            controlAsset: null,
            inputs: [],
            outputs: [AssetOutput.Create(0, 1)],
            metadata: metadata);
        var assetPacket = Packet.Create([issuance]);

        var funderScript = global::NArk.Abstractions.ArkAddress.Parse(_funder.Address).ScriptPubKey;
        var response = await CovenantSpender.SpendManyAsync(
            _funder, EmulatorUri,
            [new CovenantSpender.CovenantInput(contract, "probe", [], vtxo)],
            [new TxOut(Money.Satoshis(fund), funderScript)],
            extraPackets: [assetPacket]);
        Assert.False(string.IsNullOrEmpty(response.SignedArkTx));

        // Byte-survival: the signed ark tx's extension output must still carry
        // BOTH packets — the asset group AND our EmulatorPacket, byte-for-byte.
        var arkTx = PSBT.Parse(response.SignedArkTx, serverInfo.Network).GetGlobalTransaction();
        var extension = Extension.FromTransaction(arkTx);
        Assert.NotNull(extension);
        var survivedAsset = extension!.GetAssetPacket();
        Assert.NotNull(survivedAsset);
        Assert.Single(survivedAsset!.Groups);
        Assert.Equal(2, survivedAsset.Groups[0].Metadata.Count);
        var expectedEmulator = new EmulatorPacket(
            [new EmulatorEntry(0, opTrue, [])]).SerializePacketData();
        var survivedEmulator = extension.Packets
            .FirstOrDefault(p => p.PacketType == EmulatorPacket.TypeByte)?.SerializePacketData();
        Assert.NotNull(survivedEmulator);
        Assert.Equal(Convert.ToHexString(expectedEmulator), Convert.ToHexString(survivedEmulator!));

        // The minted asset materializes: the refunded VTXO at out0 carries
        // amount 1 of the fresh asset (id = (arkTxid, 0)).
        var arkTxId = arkTx.GetHash().ToString();
        await _funder.WaitForBalanceAsync(fund, TimeSpan.FromSeconds(60));
        await WaitForMintedAssetAsync(arkTxId);
    }

    /// <summary>
    /// Rung 2: (a) a CONTROLLED issuance (control asset referenced by ID, no
    /// control input — arkd looks the prior issuance up in its database) is
    /// accepted under a covenant leaf; (b) the first asset-introspection
    /// opcode rows hold LIVE exactly as pinned from source:
    /// INSPECTNUMASSETGROUPS on the species mint, and
    /// INSPECTASSETGROUPMETADATAHASH on the child mint — the latter also
    /// proves ArkadeCovenants.MetadataMerkleRoot is byte-identical to the
    /// emulator's computeMetadataMerkleRoot (the C# root-parity requirement).
    /// </summary>
    [Fact]
    public async Task ControlledIssuance_AndIntrospectionRows_LiveSemantics()
    {
        var transport = _funder.GetService<global::NArk.Core.Transport.IClientTransport>();
        var serverInfo = await transport.GetServerInfoAsync();
        var emulatorInfo = await new EmulatorClient(EmulatorUri).GetInfoAsync();
        var funderScript = global::NArk.Abstractions.ArkAddress.Parse(_funder.Address).ScriptPubKey;
        const long fund = 12_000;

        // ── Species mint under INSPECTNUMASSETGROUPS OP_1 OP_EQUAL ─────────
        byte[] numGroupsIsOne = [0xe5, 0x51, 0x87];
        var speciesContract = new ArkadeArtifactContract(
            "breed-probe-species", serverInfo.SignerKey, emulatorInfo.SignerPubkey,
            [new ArkadeContractFunction("mint", numGroupsIsOne)]);

        // Fund BOTH covenant VTXOs before any asset exists: once the species
        // unit sits in the funder wallet, a later send can carry it onto the
        // destination VTXO — and arkd enforces asset conservation (an
        // asset-carrying input not accounted for in the packet is refused
        // with ASSET_NOT_FOUND). Observed live; see the plan notes.
        var childMetadata = new List<AssetMetadata>
        {
            AssetMetadata.Create("breed", "arkade-heroes-breed-v1|rung2"),
            AssetMetadata.Create("genome", new string('c', 130)), // >127B forces multi-byte LEB128 in the leaf
            AssetMetadata.Create("parents", "a|b"),               // odd count exercises the promoted-node path
        };
        var expectedRoot = ArkadeCovenants.MetadataMerkleRoot(childMetadata);
        byte[] rootMatches = [0xe9, 32, .. expectedRoot, 0x87]; // INSPECTASSETGROUPMETADATAHASH <root> EQUAL
        var childContract = new ArkadeArtifactContract(
            "breed-probe-child", serverInfo.SignerKey, emulatorInfo.SignerPubkey,
            [new ArkadeContractFunction("mint", rootMatches)]);
        await _funder.SendAsync(speciesContract.GetArkAddress().ToString(serverInfo.Network == Network.Main), fund);
        await _funder.SendAsync(childContract.GetArkAddress().ToString(serverInfo.Network == Network.Main), fund);
        var speciesVtxo = await CovenantSpender.WaitForVtxoAsync(_funder, speciesContract, TimeSpan.FromSeconds(45));
        var childVtxo = await CovenantSpender.WaitForVtxoAsync(_funder, childContract, TimeSpan.FromSeconds(45));

        var speciesGroup = AssetGroup.Create(
            assetId: null, controlAsset: null, inputs: [],
            outputs: [AssetOutput.Create(0, 1)],
            metadata: new List<AssetMetadata> { AssetMetadata.Create("species", "arkade-heroes-probe") });
        var speciesResponse = await CovenantSpender.SpendManyAsync(
            _funder, EmulatorUri,
            [new CovenantSpender.CovenantInput(speciesContract, "mint", [], speciesVtxo)],
            [new TxOut(Money.Satoshis(fund), funderScript)],
            extraPackets: [Packet.Create([speciesGroup])]);
        var speciesTxId = PSBT.Parse(speciesResponse.SignedArkTx, serverInfo.Network)
            .GetGlobalTransaction().GetHash().ToString();
        var speciesAssetId = await WaitForMintedAssetAsync(speciesTxId);

        // ── Controlled child mint under the metadata-root covenant ─────────
        // The covenant pins the EXACT metadata Merkle root — the shape the
        // breeding oracle will sign. Witness: [groupIndex] (0 → empty elem).
        var childGroup = AssetGroup.Create(
            assetId: null,
            controlAsset: AssetRef.FromId(AssetId.FromString(speciesAssetId)),
            inputs: [],
            outputs: [AssetOutput.Create(0, 1)],
            metadata: childMetadata);
        var childResponse = await CovenantSpender.SpendManyAsync(
            _funder, EmulatorUri,
            [new CovenantSpender.CovenantInput(childContract, "mint", [ArkadeCovenants.EncodeIndex(0)], childVtxo)],
            [new TxOut(Money.Satoshis(fund), funderScript)],
            extraPackets: [Packet.Create([childGroup])]);
        var childTxId = PSBT.Parse(childResponse.SignedArkTx, serverInfo.Network)
            .GetGlobalTransaction().GetHash().ToString();
        await WaitForMintedAssetAsync(childTxId);
    }

    /// <summary>
    /// Rung 3a: can a wallet that neither issued nor holds a control asset
    /// mint under it? arkd's issuance check is documented as an existence
    /// lookup ("verify it exists as a prior issuance"), which would mean the
    /// SPECIES GATE MUST LIVE IN THE COVENANT (INSPECTASSETGROUPCTRL pin) —
    /// this probe pins the live behavior either way.
    /// </summary>
    [Fact]
    public async Task NonHolderControlledMint_IsAccepted_SoTheCovenantMustPinTheSpecies()
    {
        var transport = _funder.GetService<global::NArk.Core.Transport.IClientTransport>();
        var serverInfo = await transport.GetServerInfoAsync();
        var emulatorInfo = await new EmulatorClient(EmulatorUri).GetInfoAsync();
        var funderScript = global::NArk.Abstractions.ArkAddress.Parse(_funder.Address).ScriptPubKey;
        const long fund = 12_000;
        byte[] opTrue = [0x51];

        // The funder mints a species (their own covenant VTXO, funded upfront).
        var speciesContract = new ArkadeArtifactContract(
            "rung3a-species", serverInfo.SignerKey, emulatorInfo.SignerPubkey,
            [new ArkadeContractFunction("mint", opTrue)]);
        await _funder.SendAsync(speciesContract.GetArkAddress().ToString(serverInfo.Network == Network.Main), fund);
        var speciesVtxo = await CovenantSpender.WaitForVtxoAsync(_funder, speciesContract, TimeSpan.FromSeconds(45));
        var speciesResponse = await CovenantSpender.SpendManyAsync(
            _funder, EmulatorUri,
            [new CovenantSpender.CovenantInput(speciesContract, "mint", [], speciesVtxo)],
            [new TxOut(Money.Satoshis(fund), funderScript)],
            extraPackets: [Packet.Create([AssetGroup.Create(
                assetId: null, controlAsset: null, inputs: [],
                outputs: [AssetOutput.Create(0, 1)],
                metadata: new List<AssetMetadata> { AssetMetadata.Create("species", "rung3a") })])]);
        var speciesAssetId = await WaitForMintedAssetAsync(
            PSBT.Parse(speciesResponse.SignedArkTx, serverInfo.Network).GetGlobalTransaction().GetHash().ToString());

        // A STRANGER (fresh wallet, never touched the species) mints under it.
        var strangerDb = Path.Combine(Path.GetTempPath(), $"ah-stranger-{Guid.NewGuid():N}.db");
        await using var stranger = await SelfCustodyWallet.CreateAsync(new SelfCustodyWalletOptions
        {
            ArkUri = "http://localhost:7070",
            DbPath = strangerDb,
        });
        await RegtestHelper.ArkSend(stranger.Address, 30_000);
        await stranger.WaitForBalanceAsync(30_000, TimeSpan.FromSeconds(60));
        var strangerScript = global::NArk.Abstractions.ArkAddress.Parse(stranger.Address).ScriptPubKey;

        var strangerContract = new ArkadeArtifactContract(
            "rung3a-stranger", serverInfo.SignerKey, emulatorInfo.SignerPubkey,
            [new ArkadeContractFunction("mint", opTrue)]);
        await stranger.SendAsync(strangerContract.GetArkAddress().ToString(serverInfo.Network == Network.Main), fund);
        var strangerVtxo = await CovenantSpender.WaitForVtxoAsync(stranger, strangerContract, TimeSpan.FromSeconds(45));

        var response = await CovenantSpender.SpendManyAsync(
            stranger, EmulatorUri,
            [new CovenantSpender.CovenantInput(strangerContract, "mint", [], strangerVtxo)],
            [new TxOut(Money.Satoshis(fund), strangerScript)],
            extraPackets: [Packet.Create([AssetGroup.Create(
                assetId: null,
                controlAsset: AssetRef.FromId(AssetId.FromString(speciesAssetId)),
                inputs: [],
                outputs: [AssetOutput.Create(0, 1)],
                metadata: new List<AssetMetadata> { AssetMetadata.Create("intruder", "not-the-holder") })])]);
        Assert.False(string.IsNullOrEmpty(response.SignedArkTx));
        // Accepted ⇒ arkd does NOT gate controlled issuance on holding the
        // control asset. The species gate is the COVENANT'S job.
    }

    /// <summary>
    /// Rung 3b: parent-shaped structure live in ONE tx — an input that
    /// genuinely carries an asset (the species mint's carrier output IS this
    /// probe's covenant VTXO), a passthrough group retaining it (arkd's
    /// conservation rule), a controlled child issuance, and a covenant leaf
    /// exercising INSPECTINASSETLOOKUP(0xf2: parent present at vin 0, amount 1)
    /// + INSPECTASSETGROUPCTRL(0xe7: child group's control == species id).
    /// </summary>
    [Fact]
    public async Task ParentRetention_InLookupAndCtrlRows_LiveSemantics()
    {
        var transport = _funder.GetService<global::NArk.Core.Transport.IClientTransport>();
        var serverInfo = await transport.GetServerInfoAsync();
        var emulatorInfo = await new EmulatorClient(EmulatorUri).GetInfoAsync();
        var funderScript = global::NArk.Abstractions.ArkAddress.Parse(_funder.Address).ScriptPubKey;
        var isMain = serverInfo.Network == Network.Main;
        const long fund = 12_000;
        byte[] opTrue = [0x51];

        var speciesContract = new ArkadeArtifactContract(
            "rung3b-species", serverInfo.SignerKey, emulatorInfo.SignerPubkey,
            [new ArkadeContractFunction("mint", opTrue)]);
        await _funder.SendAsync(speciesContract.GetArkAddress().ToString(isMain), fund);
        var speciesVtxo = await CovenantSpender.WaitForVtxoAsync(_funder, speciesContract, TimeSpan.FromSeconds(45));

        // Species mint first (carrier back to the funder) — the probe scripts
        // pin the species id, so the contracts are built AFTER the txid is
        // known; the asset is then delivered to each probe DETERMINISTICALLY
        // via SendAssetAsync (no reliance on coin-selection hitchhiking).
        var speciesResponse = await CovenantSpender.SpendManyAsync(
            _funder, EmulatorUri,
            [new CovenantSpender.CovenantInput(speciesContract, "mint", [], speciesVtxo)],
            [new TxOut(Money.Satoshis(fund), funderScript)],
            extraPackets: [Packet.Create([AssetGroup.Create(
                assetId: null, controlAsset: null, inputs: [],
                outputs: [AssetOutput.Create(0, 1)],
                metadata: new List<AssetMetadata> { AssetMetadata.Create("species", "rung3b") })])]);
        var speciesTxId = PSBT.Parse(speciesResponse.SignedArkTx, serverInfo.Network)
            .GetGlobalTransaction().GetHash().ToString();
        var speciesId = AssetId.Create(speciesTxId, 0);
        await WaitForMintedAssetAsync(speciesTxId);

        // P1 (initial stack bottom→top: i, t — asset-entry index t on top):
        //   0xf1 INSPECTINASSETAT pops t, i → pushes txid, gidx, amount (top).
        //   Staged checks so the failing stage names any divergence:
        //   amount==1 EQUALVERIFY; gidx==0 EQUALVERIFY; txid==species EQUAL.
        // Byte-order probe: the first run with speciesId.Txid verbatim failed
        // ONLY at the final txid EQUAL — testing the reversed (internal) order.
        byte[] p1Script = [0xf1, 0x51, 0x88, 0x00, 0x88, 0x20, .. speciesId.Txid.Reverse(), 0x87];
        var p1Contract = new ArkadeArtifactContract(
            "rung3b-p1", serverInfo.SignerKey, emulatorInfo.SignerPubkey,
            [new ArkadeContractFunction("probe", p1Script)]);
        // P2 (initial stack: k): 0xe7 pops k → pushes ctrl_txid, ctrl_gidx,
        // found; VERIFY found; ctrl_gidx == 0 EQUALVERIFY; DROP txid; TRUE.
        byte[] p2Script = [0xe7, 0x69, 0x00, 0x88, 0x75, 0x51];
        var p2Contract = new ArkadeArtifactContract(
            "rung3b-p2", serverInfo.SignerKey, emulatorInfo.SignerPubkey,
            [new ArkadeContractFunction("probe", p2Script)]);

        AssetGroup Passthrough() => AssetGroup.Create(
            assetId: speciesId, controlAsset: null,
            inputs: [AssetInput.Create(0, 1)],
            outputs: [AssetOutput.Create(0, 1)],
            metadata: []);

        // ── P1: input asset entry row (0xf1) ────────────────────────────────
        await _funder.SendAssetAsync(
            p1Contract.GetArkAddress().ToString(isMain), speciesId.ToString(), 1);
        var p1Vtxo = await CovenantSpender.WaitForVtxoAsync(_funder, p1Contract, TimeSpan.FromSeconds(45));
        var p1Response = await CovenantSpender.SpendManyAsync(
            _funder, EmulatorUri,
            [new CovenantSpender.CovenantInput(p1Contract, "probe",
                [ArkadeCovenants.EncodeIndex(0), ArkadeCovenants.EncodeIndex(0)],
                p1Vtxo)],
            [new TxOut(p1Vtxo.TxOut.Value, p2Contract.GetArkAddress().ScriptPubKey)],
            extraPackets: [Packet.Create([Passthrough()])]);
        Assert.False(string.IsNullOrEmpty(p1Response.SignedArkTx));

        // ── P2: control-asset row (0xe7) on a breed-shaped packet ──────────
        var p2Vtxo = await CovenantSpender.WaitForVtxoAsync(_funder, p2Contract, TimeSpan.FromSeconds(45));
        var child = AssetGroup.Create(
            assetId: null,
            controlAsset: AssetRef.FromId(speciesId),
            inputs: [],
            outputs: [AssetOutput.Create(0, 1)],
            metadata: new List<AssetMetadata> { AssetMetadata.Create("genome", "rung3b-child") });
        var p2Response = await CovenantSpender.SpendManyAsync(
            _funder, EmulatorUri,
            [new CovenantSpender.CovenantInput(p2Contract, "probe",
                [ArkadeCovenants.EncodeIndex(1)],
                p2Vtxo)],
            [new TxOut(p2Vtxo.TxOut.Value, funderScript)],
            extraPackets: [Packet.Create([Passthrough(), child])]);
        var breedTxId = PSBT.Parse(p2Response.SignedArkTx, serverInfo.Network)
            .GetGlobalTransaction().GetHash().ToString();

        // Both the retained parent AND the child (id = (breedTxid, 1)) land
        // back in the funder's wallet on the same carrier output.
        await WaitForMintedAssetAsync(speciesTxId);
        var childId = await WaitForMintedAssetAsync(breedTxId);
        Assert.EndsWith("0100", childId); // group index 1, uint16 LE
    }

    /// <summary>Waits until an asset whose id starts with the given txid holds amount 1 in the funder wallet, returning the full asset id.</summary>
    private async Task<string> WaitForMintedAssetAsync(string arkTxId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(45);
        while (true)
        {
            var assets = await _funder.GetAssetsAsync();
            var minted = assets.FirstOrDefault(a => a.AssetId.StartsWith(arkTxId, StringComparison.OrdinalIgnoreCase));
            if (minted != default && minted.Amount == 1) return minted.AssetId;
            Assert.True(DateTime.UtcNow < deadline,
                $"minted asset for {arkTxId} never appeared (have: {string.Join(", ", assets.Select(a => a.AssetId))})");
            await Task.Delay(1500);
        }
    }
}
