using ArkadeHeroes.Chain.Covenants;
using ArkadeHeroes.Chain.NArk;
using NArk.Abstractions;
using NArk.Abstractions.Assets;
using NArk.Core.Assets;
using NBitcoin;

namespace ArkadeHeroes.Tests.E2E;

/// <summary>
/// GO/NO-GO probe for the offer covenant-v2 upgrade: proves — live, in isolation — that
/// <see cref="ArkadeCovenants.AssetAtWitnessOutput"/> (witness-indexed 0xef presence, the
/// `.ark` tx.outputs[i].assets.lookup == 1 intent) enforces conservation. The honest spend
/// (asset passes through to output 0, witness points at 0) co-signs; BURNING the asset is
/// refused; a witness pointing at an output the asset is NOT at is refused.
/// </summary>
public class CovenantAssetAtWitnessOutputProbeTests : IAsyncLifetime
{
    private static readonly Uri EmulatorUri = new("http://localhost:7073/");

    private SelfCustodyWallet _funder = null!;
    private SelfCustodyWallet _other = null!;
    private readonly List<string> _dbPaths = [];

    public async Task InitializeAsync()
    {
        await RegtestHelper.WaitForArkdReadyAsync(TimeSpan.FromSeconds(30));
        _funder = await NewWalletAsync();
        _other = await NewWalletAsync(); // a distinct second-output script (no coalescing)
        await RegtestHelper.ArkSend(_funder.Address, 100_000);
        await _funder.WaitForBalanceAsync(100_000, TimeSpan.FromSeconds(60));
    }

    private async Task<SelfCustodyWallet> NewWalletAsync()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"ah-witnessout-{Guid.NewGuid():N}.db");
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
        await _other.DisposeAsync();
        foreach (var p in _dbPaths)
            try { if (File.Exists(p)) File.Delete(p); } catch { /* windows lock */ }
    }

    [Fact]
    public async Task WitnessIndexedPresence_HonestCoSigned_BurnAndWrongIndexRefused()
    {
        var transport = _funder.GetService<global::NArk.Core.Transport.IClientTransport>();
        var serverInfo = await transport.GetServerInfoAsync();
        var emulatorInfo = await new EmulatorClient(EmulatorUri).GetInfoAsync();
        var isMain = serverInfo.Network == Network.Main;
        var dust = serverInfo.Dust.Satoshi;
        var funderScript = ArkAddress.Parse(_funder.Address).ScriptPubKey;
        var otherScript = ArkAddress.Parse(_other.Address).ScriptPubKey;

        var mgr = _funder.GetService<IAssetManager>();
        var res = await mgr.IssueAsync(_funder.WalletId, new IssuanceParams(Amount: 1));
        await _funder.WaitForAssetAsync(res.AssetId, TimeSpan.FromSeconds(30));
        var asset = AssetId.FromString(res.AssetId);

        var leaf = new List<byte>();
        leaf.AddRange(ArkadeCovenants.AssetAtWitnessOutput(asset));
        leaf.Add(0x51); // OP_1
        var contract = new ArkadeArtifactContract(
            "witness-out-probe", serverInfo.SignerKey, emulatorInfo.SignerPubkey,
            [new ArkadeContractFunction("spend", leaf.ToArray())]);
        var addr = contract.GetArkAddress().ToString(isMain);

        await _funder.SendAssetAsync(addr, res.AssetId, 1);
        var vtxo = await CovenantSpender.WaitForVtxoAsync(_funder, contract, TimeSpan.FromSeconds(45));
        var fund = (long)vtxo.Amount;

        CovenantSpender.CovenantInput[] Inputs(int outIdx) =>
            [new(contract, "spend", [ArkadeCovenants.EncodeIndex(outIdx)], vtxo)];
        Packet Passthrough(ushort vout) => Packet.Create(
            [AssetGroup.Create(asset, null, [AssetInput.Create(0, 1)], [AssetOutput.Create(vout, 1)], [])]);

        // ── Cheat: BURN the asset (input, no output) — the lookup finds nothing anywhere.
        var burn = await Assert.ThrowsAnyAsync<Exception>(() => CovenantSpender.SpendManyAsync(
            _funder, EmulatorUri, Inputs(0), [new TxOut(Money.Satoshis(fund), funderScript)],
            extraPackets: [Packet.Create(
                [AssetGroup.Create(asset, null, [AssetInput.Create(0, 1)], [], [])])]));
        Assert.Contains("Emulator rejected", burn.Message);

        // ── Cheat: the witness points at output 0 but the asset is routed to output 1.
        var wrongIndex = await Assert.ThrowsAnyAsync<Exception>(() => CovenantSpender.SpendManyAsync(
            _funder, EmulatorUri, Inputs(0),
            [new TxOut(Money.Satoshis(dust), funderScript), new TxOut(Money.Satoshis(fund - dust), otherScript)],
            extraPackets: [Passthrough(1)]));
        Assert.Contains("Emulator rejected", wrongIndex.Message);

        // ── Honest: asset → output 0, witness points at 0 → co-signed.
        var response = await CovenantSpender.SpendManyAsync(
            _funder, EmulatorUri, Inputs(0), [new TxOut(Money.Satoshis(fund), funderScript)],
            extraPackets: [Passthrough(0)]);
        Assert.False(string.IsNullOrEmpty(response.SignedArkTx),
            "the honest witness-indexed presence spend must be emulator-co-signed");
    }
}
