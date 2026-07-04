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
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(45);
        while (true)
        {
            var assets = await _funder.GetAssetsAsync();
            var minted = assets.FirstOrDefault(a => a.AssetId.StartsWith(arkTxId, StringComparison.OrdinalIgnoreCase));
            if (minted != default && minted.Amount == 1) break;
            Assert.True(DateTime.UtcNow < deadline,
                $"minted asset never appeared in the funder wallet (have: {string.Join(", ", assets.Select(a => a.AssetId))})");
            await Task.Delay(1500);
        }
    }
}
