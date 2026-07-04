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
