using System.Net;
using ArkadeHeroes.Chain.Covenants;
using ArkadeHeroes.Chain.NArk;
using NArk.Arkade.Emulator;
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
        var emulatorInfo = await EmulatorEndpoint.Client(EmulatorUri).GetInfoAsync();

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
            .FirstOrDefault(p => p.PacketType == EmulatorPacket.PacketTypeId)?.SerializePacketData();
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
        var emulatorInfo = await EmulatorEndpoint.Client(EmulatorUri).GetInfoAsync();
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
        var emulatorInfo = await EmulatorEndpoint.Client(EmulatorUri).GetInfoAsync();
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
        var emulatorInfo = await EmulatorEndpoint.Client(EmulatorUri).GetInfoAsync();
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

    /// <summary>
    /// Rung 4: the FULL breeding covenant — <see cref="ArkadeCovenants.BreedAuthorized"/> —
    /// live: parents present + retained, child controlled by the species,
    /// exact fee, oracle-signed metadata root. One honest breed passes; four
    /// adversarial variants are refused (each on its own VTXO at the SAME
    /// contract — refusals here are emulator-side and poison nothing).
    /// </summary>
    [Fact]
    public async Task BreedAuthorized_HonestPasses_CheatsRefused()
    {
        var transport = _funder.GetService<global::NArk.Core.Transport.IClientTransport>();
        var serverInfo = await transport.GetServerInfoAsync();
        var emulatorInfo = await EmulatorEndpoint.Client(EmulatorUri).GetInfoAsync();
        var funderScript = global::NArk.Abstractions.ArkAddress.Parse(_funder.Address).ScriptPubKey;
        var isMain = serverInfo.Network == Network.Main;
        byte[] opTrue = [0x51];
        const long fund = 12_000, carrierIn = 6_000, feeSats = 2_000;

        // Mint the species, then BOTH parents (one tx, two controlled groups).
        var mintContract = new ArkadeArtifactContract(
            "rung4-mint", serverInfo.SignerKey, emulatorInfo.SignerPubkey,
            [new ArkadeContractFunction("mint", opTrue)]);
        var mintAddress = mintContract.GetArkAddress().ToString(isMain);
        await _funder.SendAsync(mintAddress, fund);
        await _funder.SendAsync(mintAddress, fund);
        var mintVtxos = await CovenantSpender.WaitForVtxosAsync(_funder, mintContract, 2, TimeSpan.FromSeconds(45));

        var speciesResponse = await CovenantSpender.SpendManyAsync(
            _funder, EmulatorUri,
            [new CovenantSpender.CovenantInput(mintContract, "mint", [], mintVtxos[0])],
            [new TxOut(Money.Satoshis(fund), funderScript)],
            extraPackets: [Packet.Create([AssetGroup.Create(
                assetId: null, controlAsset: null, inputs: [],
                outputs: [AssetOutput.Create(0, 1)],
                metadata: new List<AssetMetadata> { AssetMetadata.Create("species", "rung4") })])]);
        var speciesId = AssetId.Create(
            PSBT.Parse(speciesResponse.SignedArkTx, serverInfo.Network).GetGlobalTransaction().GetHash().ToString(), 0);
        await WaitForMintedAssetAsync(speciesId.ToString()[..64]);

        AssetGroup ControlledMint(string genome) => AssetGroup.Create(
            assetId: null, controlAsset: AssetRef.FromId(speciesId), inputs: [],
            outputs: [AssetOutput.Create(0, 1)],
            metadata: new List<AssetMetadata> { AssetMetadata.Create("genome", genome) });
        var parentsResponse = await CovenantSpender.SpendManyAsync(
            _funder, EmulatorUri,
            [new CovenantSpender.CovenantInput(mintContract, "mint", [], mintVtxos[1])],
            [new TxOut(Money.Satoshis(fund), funderScript)],
            extraPackets: [Packet.Create([ControlledMint("parent-a"), ControlledMint("parent-b")])]);
        var parentsTxId = PSBT.Parse(parentsResponse.SignedArkTx, serverInfo.Network)
            .GetGlobalTransaction().GetHash().ToString();
        var parentAId = AssetId.Create(parentsTxId, 0);
        var parentBId = AssetId.Create(parentsTxId, 1);
        await _funder.WaitForAssetAsync(parentAId.ToString(), TimeSpan.FromSeconds(45));
        await _funder.WaitForAssetAsync(parentBId.ToString(), TimeSpan.FromSeconds(45));

        // The breed contract (ids now known).
        var oracleKey = NBitcoin.Secp256k1.ECPrivKey.Create(
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        Span<byte> oraclePkSpan = stackalloc byte[32];
        oracleKey.CreateXOnlyPubKey().WriteToSpan(oraclePkSpan);
        var oraclePk = oraclePkSpan.ToArray();

        // The fee pays a treasury address DISTINCT from the player (funder), or
        // the builder coalesces the two same-script outputs and the fee vanishes.
        var treasuryScript = new Key().PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, serverInfo.Network).ScriptPubKey;
        var breedContract = new ArkadeArtifactContract(
            "rung4-breed", serverInfo.SignerKey, emulatorInfo.SignerPubkey,
            [new ArkadeContractFunction("breed",
                ArkadeCovenants.BreedAuthorized(speciesId, parentAId, parentBId, oraclePk, treasuryScript, feeSats))]);
        var breedAddress = breedContract.GetArkAddress();

        // Deliver BOTH parents onto carriers at the breed address. NArk's
        // selector cannot ride two assets on one coin, so deliver per-asset;
        // its passthrough may still hitchhike B onto A's carrier (rung 2), so
        // the honest tx spends WHATEVER carriers hold the parents.
        var spending = _funder.GetService<global::NArk.Core.Services.SpendingService>();
        async Task DeliverAsync(AssetId id) => await spending.Spend(_funder.WalletId,
        [
            new global::NArk.Abstractions.ArkTxOut(
                global::NArk.Abstractions.ArkTxOutType.Vtxo, Money.Satoshis(carrierIn), breedAddress)
            { Assets = [new global::NArk.Abstractions.ArkTxOutAsset(id.ToString(), 1)] },
        ]);
        await DeliverAsync(parentAId);
        var afterA = await CovenantSpender.WaitForVtxosAsync(_funder, breedContract, 1, TimeSpan.FromSeconds(45));
        if (!afterA.Any(v => v.Assets is { Count: 2 })) await DeliverAsync(parentBId);

        var vtxos = await CovenantSpender.WaitForVtxosAsync(_funder, breedContract, 1, TimeSpan.FromSeconds(45));
        var carriers = vtxos.Where(v => v.Assets is { Count: > 0 }).OrderBy(v => v.OutPoint.Hash.ToString()).ToList();
        int VinOf(AssetId id) => carriers.FindIndex(v => v.Assets!.Any(a => a.AssetId == id.ToString()));
        var iA = VinOf(parentAId);
        var iB = VinOf(parentBId);
        Assert.True(iA >= 0 && iB >= 0, "parents not on breed carriers");

        // Packet: passthrough parents (retained) + controlled child at group 2.
        var childMetadata = new List<AssetMetadata>
        {
            AssetMetadata.Create("breed", "arkade-heroes-breed-v1|rung4|parents"),
            AssetMetadata.Create("genome", "rung4-child-genome"),
        };
        AssetGroup Pt(AssetId id, int vin) => AssetGroup.Create(
            assetId: id, controlAsset: null,
            inputs: [AssetInput.Create((ushort)vin, 1)], outputs: [AssetOutput.Create(0, 1)], metadata: []);
        AssetGroup Child(AssetId control) => AssetGroup.Create(
            assetId: null, controlAsset: AssetRef.FromId(control), inputs: [],
            outputs: [AssetOutput.Create(0, 1)], metadata: childMetadata);
        byte[] SignRoot(byte[] root32)
        {
            var sig = oracleKey.SignBIP340(root32);
            var bytes = new byte[64];
            sig.WriteToSpan(bytes);
            return bytes;
        }
        var honestRoot = ArkadeCovenants.MetadataMerkleRoot(childMetadata);

        // All attempts run on the SAME carriers — emulator refusals never reach
        // arkd, so a rejected cheat leaves the VTXOs unspent for the next try.
        // Every packet conserves assets (parents in+out, child mint), so arkd
        // is satisfied and ONLY the covenant is the gatekeeper.
        Task<EmulatorSubmitTxResult> Attempt(List<AssetGroup> groups, byte[] sig, long feePaid)
        {
            var total = carriers.Sum(v => (long)v.Amount);
            return CovenantSpender.SpendManyAsync(
                _funder, EmulatorUri,
                [.. carriers.Select(v => new CovenantSpender.CovenantInput(breedContract, "breed",
                    ArkadeCovenants.BreedWitness(sig, 2, feeOutputIndex: 1, iA, iB), v))],
                [
                    new TxOut(Money.Satoshis(total - feePaid), funderScript),  // change + assets (vout 0)
                    new TxOut(Money.Satoshis(feePaid), treasuryScript),         // fee (vout 1)
                ],
                extraPackets: [Packet.Create(groups)]);
        }
        List<AssetGroup> HonestGroups() => [Pt(parentAId, iA), Pt(parentBId, iB), Child(speciesId)];

        // 1. WRONG-ROOT signature (oracle signed other bytes) — CSFS refuses.
        var badSig = await Assert.ThrowsAnyAsync<Exception>(() =>
            Attempt(HonestGroups(), SignRoot(System.Security.Cryptography.SHA256.HashData([0xba, 0xad])), feeSats));
        Assert.Contains("Emulator tx failed", badSig.Message);

        // 2. WRONG SPECIES (child controlled by parentA, not the species) —
        //    arkd accepts (parentA exists), the 0xe7 species pin refuses.
        var wrongSpecies = await Assert.ThrowsAnyAsync<Exception>(() =>
            Attempt([Pt(parentAId, iA), Pt(parentBId, iB), Child(parentAId)], SignRoot(honestRoot), feeSats));
        Assert.Contains("Emulator tx failed", wrongSpecies.Message);

        // 3. FEE THEFT (pays 1000, not 2000) — the payTo pin refuses.
        var feeTheft = await Assert.ThrowsAnyAsync<Exception>(() =>
            Attempt(HonestGroups(), SignRoot(honestRoot), 1_000));
        Assert.Contains("Emulator tx failed", feeTheft.Message);

        // 4. HONEST breed — parents retained, child minted, fee paid.
        var honest = await Attempt(HonestGroups(), SignRoot(honestRoot), feeSats);
        var breedTxId = PSBT.Parse(honest.SignedArkTx, serverInfo.Network)
            .GetGlobalTransaction().GetHash().ToString();
        var childId = await WaitForMintedAssetAsync(breedTxId);
        Assert.EndsWith("0200", childId); // child = group 2
        var assets = await _funder.GetAssetsAsync();
        Assert.Contains(assets, a => a.AssetId == parentAId.ToString());
        Assert.Contains(assets, a => a.AssetId == parentBId.ToString());
    }

    /// <summary>
    /// Minimal isolated probe for the ctrl_txid wire format of
    /// OP_INSPECTASSETGROUPCTRL(0xe7): mint a species, then spend a plain
    /// covenant VTXO in a tx whose packet is a single controlled-child
    /// issuance, running 0xe7 on group 0 and comparing the pushed ctrl_txid
    /// against each candidate. Exactly one leaf co-signs — it names the format.
    /// </summary>
    [Fact]
    public async Task CtrlTxidFormat_Resolves()
    {
        var transport = _funder.GetService<global::NArk.Core.Transport.IClientTransport>();
        var serverInfo = await transport.GetServerInfoAsync();
        var emulatorInfo = await EmulatorEndpoint.Client(EmulatorUri).GetInfoAsync();
        var funderScript = global::NArk.Abstractions.ArkAddress.Parse(_funder.Address).ScriptPubKey;
        var isMain = serverInfo.Network == Network.Main;
        const long fund = 12_000;
        byte[] opTrue = [0x51];

        var speciesContract = new ArkadeArtifactContract(
            "ctrlfmt-species", serverInfo.SignerKey, emulatorInfo.SignerPubkey,
            [new ArkadeContractFunction("mint", opTrue)]);
        await _funder.SendAsync(speciesContract.GetArkAddress().ToString(isMain), fund);
        var speciesVtxo = await CovenantSpender.WaitForVtxoAsync(_funder, speciesContract, TimeSpan.FromSeconds(45));
        var speciesResponse = await CovenantSpender.SpendManyAsync(
            _funder, EmulatorUri,
            [new CovenantSpender.CovenantInput(speciesContract, "mint", [], speciesVtxo)],
            [new TxOut(Money.Satoshis(fund), funderScript)],
            extraPackets: [Packet.Create([AssetGroup.Create(
                assetId: null, controlAsset: null, inputs: [],
                outputs: [AssetOutput.Create(0, 1)],
                metadata: new List<AssetMetadata> { AssetMetadata.Create("species", "ctrlfmt") })])]);
        var speciesId = AssetId.Create(
            PSBT.Parse(speciesResponse.SignedArkTx, serverInfo.Network).GetGlobalTransaction().GetHash().ToString(), 0);
        await WaitForMintedAssetAsync(speciesId.ToString()[..64]);

        // Move the species asset OUT of the funder so it cannot hitchhike onto
        // the probe VTXOs (which would make arkd reject on conservation).
        // Control-by-ID resolves against arkd's issuance history, so the asset
        // need not be held (rung 3: a non-holder mints under it fine).
        var sinkDb = Path.Combine(Path.GetTempPath(), $"ah-sink-{Guid.NewGuid():N}.db");
        await using (var sink = await SelfCustodyWallet.CreateAsync(new SelfCustodyWalletOptions
        {
            ArkUri = "http://localhost:7070",
            DbPath = sinkDb,
        }))
        {
            await _funder.SendAssetAsync(sink.Address, speciesId.ToString(), 1);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(45);
            while ((await _funder.GetAssetsAsync()).Any(a => a.AssetId == speciesId.ToString()))
            {
                Assert.True(DateTime.UtcNow < deadline, "species asset never left the funder");
                await Task.Delay(1000);
            }

            // 0xe7 (group 0) → VERIFY found → DROP gidx → compare ctrl_txid.
        byte[] Probe(byte[] candidate) => [0x00, 0xe7, 0x69, 0x75, (byte)candidate.Length, .. candidate, 0x87];
        var probe = new ArkadeArtifactContract(
            "ctrlfmt-probe", serverInfo.SignerKey, emulatorInfo.SignerPubkey,
            [
                new ArkadeContractFunction("raw", Probe(speciesId.Txid)),
                new ArkadeContractFunction("rev", Probe(speciesId.Txid.Reverse().ToArray())),
                new ArkadeContractFunction("ser", Probe(speciesId.Serialize())),
            ]);

        AssetGroup Child() => AssetGroup.Create(
            assetId: null, controlAsset: AssetRef.FromId(speciesId), inputs: [],
            outputs: [AssetOutput.Create(0, 1)],
            metadata: new List<AssetMetadata> { AssetMetadata.Create("genome", "ctrlfmt-child") });

        var winners = new List<string>();
        foreach (var fn in new[] { "raw", "rev", "ser" })
        {
            await _funder.SendAsync(probe.GetArkAddress().ToString(isMain), fund);
            var vtxo = await CovenantSpender.WaitForVtxoAsync(_funder, probe, TimeSpan.FromSeconds(45));
            try
            {
                await CovenantSpender.SpendManyAsync(
                    _funder, EmulatorUri,
                    [new CovenantSpender.CovenantInput(probe, fn, [], vtxo)],
                    [new TxOut(Money.Satoshis(fund), funderScript)],
                    extraPackets: [Packet.Create([Child()])]);
                winners.Add(fn);
            }
            // A mismatched candidate is refused by the emulator as an HTTP 500, and nothing else in
            // this spend produces one. Narrow deliberately: a transport failure carries a null status
            // and a construction failure is not an HttpRequestException at all, so both propagate
            // rather than silently dropping a candidate and making winners.Single() lie.
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.InternalServerError) { }
        }
            // ctrl_txid is REVERSED (internal) order — same as the 0xf1/0xf2 family.
            Assert.Equal("rev", winners.Single());
        }
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
