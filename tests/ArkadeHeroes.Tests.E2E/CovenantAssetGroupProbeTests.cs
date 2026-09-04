using ArkadeHeroes.Chain.Covenants;
using ArkadeHeroes.Chain.NArk;
using NArk.Abstractions;
using NArk.Abstractions.Assets;
using NArk.Core.Assets;
using NBitcoin;

namespace ArkadeHeroes.Tests.E2E;

/// <summary>
/// GO/NO-GO probe for the trustless-reclaim opcodes, live and in isolation — pins
/// 0xe5 INSPECTNUMASSETGROUPS, 0xe8 FINDASSETGROUPBYASSETID and 0xec INSPECTASSETGROUPSUM
/// (and the BigNum-vs-scriptnum sum comparison) against the running emulator BEFORE the
/// reclaim leaf composes them. The backlog table is source-read, not yet emulator-executed
/// for these rows.
/// </summary>
public class CovenantAssetGroupProbeTests : IAsyncLifetime
{
    private static readonly Uri EmulatorUri = new("http://localhost:7073/");

    private SelfCustodyWallet _funder = null!;
    private readonly List<string> _dbPaths = [];

    public async Task InitializeAsync()
    {
        await RegtestHelper.WaitForArkdReadyAsync(TimeSpan.FromSeconds(30));
        _funder = await NewWalletAsync();
        await RegtestHelper.ArkSend(_funder.Address, 200_000);
        await _funder.WaitForBalanceAsync(200_000, TimeSpan.FromSeconds(60));
    }

    private async Task<SelfCustodyWallet> NewWalletAsync()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"ah-agprobe-{Guid.NewGuid():N}.db");
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
        foreach (var p in _dbPaths)
            try { if (File.Exists(p)) File.Delete(p); } catch { /* windows lock */ }
    }

    [Fact]
    public async Task AssetGroupOpcodes_NumGroups_Find_InputSum_BehaveAsSpecified()
    {
        var transport = _funder.GetService<global::NArk.Core.Transport.IClientTransport>();
        var serverInfo = await transport.GetServerInfoAsync();
        var emulatorInfo = await EmulatorEndpoint.Client(EmulatorUri).GetInfoAsync();
        var isMain = serverInfo.Network == Network.Main;
        var funderScript = ArkAddress.Parse(_funder.Address).ScriptPubKey;

        // One asset, amount 4 = 2 for the wrong-count cheat + 2 for the honest check (each
        // sub-probe funds its own contract; the input-sum check reads a non-trivial value 2).
        var assetManager = _funder.GetService<IAssetManager>();
        var a = await assetManager.IssueAsync(_funder.WalletId, new IssuanceParams(Amount: 4));
        await _funder.WaitForAssetAsync(a.AssetId, TimeSpan.FromSeconds(30));
        var asset = AssetId.FromString(a.AssetId);

        // ── Cheat: assert numGroups==2 when the packet has 1 → refused.
        var wrongCount = new List<byte>();
        wrongCount.AddRange(ArkadeCovenants.NumAssetGroupsIs(2));
        wrongCount.Add(0x51);
        var wrongContract = new ArkadeArtifactContract(
            "asset-group-probe-wrong", serverInfo.SignerKey, emulatorInfo.SignerPubkey,
            [new ArkadeContractFunction("check", wrongCount.ToArray())]);
        var wrongAddr = wrongContract.GetArkAddress().ToString(isMain);
        await _funder.SendAssetAsync(wrongAddr, a.AssetId, 2);
        var wrongVtxo = await CovenantSpender.WaitForVtxoAsync(_funder, wrongContract, TimeSpan.FromSeconds(45));
        var refused = await Assert.ThrowsAnyAsync<Exception>(() => CovenantSpender.SpendManyAsync(
            _funder, EmulatorUri, [new(wrongContract, "check", [], wrongVtxo)],
            [new TxOut(Money.Satoshis((long)wrongVtxo.Amount), funderScript)],
            extraPackets: [Packet.Create([AssetGroup.Create(asset, null, [AssetInput.Create(0, 2)], [AssetOutput.Create(0, 2)], [])])]));
        Assert.Contains("Emulator tx failed", refused.Message);

        // ── Honest: exactly ONE asset group AND that asset's input sum == 2 → co-signed.
        var leaf = new List<byte>();
        leaf.AddRange(ArkadeCovenants.NumAssetGroupsIs(1));
        leaf.AddRange(ArkadeCovenants.AssetInputSumIs(asset, 2));
        leaf.Add(0x51); // OP_1
        var contract = new ArkadeArtifactContract(
            "asset-group-probe", serverInfo.SignerKey, emulatorInfo.SignerPubkey,
            [new ArkadeContractFunction("check", leaf.ToArray())]);
        var addr = contract.GetArkAddress().ToString(isMain);
        await _funder.SendAssetAsync(addr, a.AssetId, 2);
        var vtxo = await CovenantSpender.WaitForVtxoAsync(_funder, contract, TimeSpan.FromSeconds(45));

        var ok = await CovenantSpender.SpendManyAsync(
            _funder, EmulatorUri, [new(contract, "check", [], vtxo)],
            [new TxOut(Money.Satoshis((long)vtxo.Amount), funderScript)],
            extraPackets: [Packet.Create([AssetGroup.Create(asset, null, [AssetInput.Create(0, 2)], [AssetOutput.Create(0, 2)], [])])]);
        Assert.False(string.IsNullOrEmpty(ok.SignedArkTx), "numGroups==1 + inSum==2 must co-sign");
    }
}
