using ArkadeHeroes.Chain.Covenants;
using ArkadeHeroes.Chain.NArk;
using NArk.Abstractions;
using NArk.Abstractions.Assets;
using NArk.Core.Assets;
using NBitcoin;

namespace ArkadeHeroes.Tests.E2E;

/// <summary>
/// GO/NO-GO probe for the merge covenant-v2 upgrade: proves — live, in isolation —
/// that <see cref="ArkadeCovenants.MintToPlayer"/> binds a FRESH mint (a tx-issued
/// asset whose id is unknowable in advance) to the baked player via 0xed output-asset-
/// count == 1 + 0xd1 output-script, once the input is burned so the mint is the lone
/// output asset. The honest spend co-signs; a mint routed to the WRONG script and a
/// smuggled SECOND asset at output 0 are refused. This validates the whole merge model
/// before touching the merge covenant.
/// </summary>
public class CovenantMintToPlayerProbeTests : IAsyncLifetime
{
    private static readonly Uri EmulatorUri = new("http://localhost:7073/");
    private const int Sweep = 4;

    private SelfCustodyWallet _funder = null!;
    private SelfCustodyWallet _stranger = null!;
    private readonly List<string> _dbPaths = [];

    public async Task InitializeAsync()
    {
        await RegtestHelper.WaitForArkdReadyAsync(TimeSpan.FromSeconds(30));
        _funder = await NewWalletAsync();
        _stranger = await NewWalletAsync(); // only its address is used (the wrong-script destination)
        await RegtestHelper.ArkSend(_funder.Address, 200_000);
        await _funder.WaitForBalanceAsync(200_000, TimeSpan.FromSeconds(60));
    }

    private async Task<SelfCustodyWallet> NewWalletAsync()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"ah-mintprobe-{Guid.NewGuid():N}.db");
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
        await _stranger.DisposeAsync();
        foreach (var p in _dbPaths)
            try { if (File.Exists(p)) File.Delete(p); } catch { /* windows lock */ }
    }

    [Fact]
    public async Task FreshMintToPlayer_HonestCoSigned_WrongScriptAndExtraAssetRefused()
    {
        var transport = _funder.GetService<global::NArk.Core.Transport.IClientTransport>();
        var serverInfo = await transport.GetServerInfoAsync();
        var emulatorInfo = await EmulatorEndpoint.Client(EmulatorUri).GetInfoAsync();
        var isMain = serverInfo.Network == Network.Main;
        var dust = serverInfo.Dust.Satoshi;

        // One input asset to burn (stands in for a merge input hero).
        var assetManager = _funder.GetService<IAssetManager>();
        var inRes = await assetManager.IssueAsync(_funder.WalletId, new IssuanceParams(Amount: 1));
        await _funder.WaitForAssetAsync(inRes.AssetId, TimeSpan.FromSeconds(30));
        var inputAsset = AssetId.FromString(inRes.AssetId);
        var playerScript = ArkAddress.Parse(_funder.Address).ScriptPubKey;
        var wrongScript = ArkAddress.Parse(_stranger.Address).ScriptPubKey;

        // Leaf: the mint lands at output 0 paying the player, AND the input is burned.
        var leaf = new List<byte>();
        leaf.AddRange(ArkadeCovenants.MintToPlayer(playerScript));
        leaf.AddRange(ArkadeCovenants.AssetBurned(inputAsset, Sweep));
        leaf.Add(0x51); // OP_1
        var contract = new ArkadeArtifactContract(
            "mint-to-player-probe", serverInfo.SignerKey, emulatorInfo.SignerPubkey,
            [new ArkadeContractFunction("mint", leaf.ToArray())]);
        var contractAddr = contract.GetArkAddress().ToString(isMain);

        await _funder.SendAssetAsync(contractAddr, inRes.AssetId, 1);
        var vtxo = await CovenantSpender.WaitForVtxoAsync(_funder, contract, TimeSpan.FromSeconds(45));

        CovenantSpender.CovenantInput[] Inputs() => [new(contract, "mint", [], vtxo)];

        // ── Cheat: mint to the WRONG script → 0xd1 refuses.
        var wrong = await Assert.ThrowsAnyAsync<Exception>(() => CovenantSpender.SpendManyAsync(
            _funder, EmulatorUri, Inputs(), [new TxOut(Money.Satoshis(dust), wrongScript)],
            extraPackets: [Packet.Create(
            [
                AssetGroup.Create(inputAsset, null, [AssetInput.Create(0, 1)], [], []),   // input burned
                AssetGroup.Create(null, null, [], [AssetOutput.Create(0, 1)], []),        // fresh mint → out 0
            ])]));
        Assert.Contains("Emulator tx failed", wrong.Message);

        // ── Cheat: smuggle a SECOND fresh asset at output 0 → 0xed count == 2 refuses.
        var extra = await Assert.ThrowsAnyAsync<Exception>(() => CovenantSpender.SpendManyAsync(
            _funder, EmulatorUri, Inputs(), [new TxOut(Money.Satoshis(dust), playerScript)],
            extraPackets: [Packet.Create(
            [
                AssetGroup.Create(inputAsset, null, [AssetInput.Create(0, 1)], [], []),
                AssetGroup.Create(null, null, [], [AssetOutput.Create(0, 1)], []),
                AssetGroup.Create(null, null, [], [AssetOutput.Create(0, 1)], []),        // 2nd asset at out 0
            ])]));
        Assert.Contains("Emulator tx failed", extra.Message);

        // ── Honest: fresh mint → output 0 paying the player, input burned → co-signed.
        var response = await CovenantSpender.SpendManyAsync(
            _funder, EmulatorUri, Inputs(), [new TxOut(Money.Satoshis(dust), playerScript)],
            extraPackets: [Packet.Create(
            [
                AssetGroup.Create(inputAsset, null, [AssetInput.Create(0, 1)], [], []),
                AssetGroup.Create(null, null, [], [AssetOutput.Create(0, 1)], []),
            ])]);
        Assert.False(string.IsNullOrEmpty(response.SignedArkTx),
            "the honest fresh-mint-to-player must be emulator-co-signed");
    }
}
