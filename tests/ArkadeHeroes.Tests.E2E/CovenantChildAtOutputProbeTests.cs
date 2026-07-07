using ArkadeHeroes.Chain.Covenants;
using ArkadeHeroes.Chain.NArk;
using NArk.Abstractions;
using NArk.Core.Assets;
using NBitcoin;

namespace ArkadeHeroes.Tests.E2E;

/// <summary>
/// GO/NO-GO probe for the breed covenant-v2 upgrade: proves — live, in isolation —
/// that <see cref="ArkadeCovenants.ChildAtOutput"/> (0xeb INSPECTASSETGROUP) binds a
/// fresh issuance group's output to the baked tx-output. The honest spend (child →
/// output 0) co-signs; a spend routing the child to output 1 (0xeb vout != 0) is
/// refused. This validates the one new opcode before touching the breed covenant.
/// </summary>
public class CovenantChildAtOutputProbeTests : IAsyncLifetime
{
    private static readonly Uri EmulatorUri = new("http://localhost:7073/");

    private SelfCustodyWallet _funder = null!;
    private SelfCustodyWallet _stranger = null!;
    private readonly List<string> _dbPaths = [];

    public async Task InitializeAsync()
    {
        await RegtestHelper.WaitForArkdReadyAsync(TimeSpan.FromSeconds(30));
        _funder = await NewWalletAsync();
        _stranger = await NewWalletAsync(); // only its address (the cheat's distinct output-1 script)
        await RegtestHelper.ArkSend(_funder.Address, 100_000);
        await _funder.WaitForBalanceAsync(100_000, TimeSpan.FromSeconds(60));
    }

    private async Task<SelfCustodyWallet> NewWalletAsync()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"ah-childprobe-{Guid.NewGuid():N}.db");
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
    public async Task ChildAtOutput_HonestCoSigned_ChildRoutedElsewhereRefused()
    {
        var transport = _funder.GetService<global::NArk.Core.Transport.IClientTransport>();
        var serverInfo = await transport.GetServerInfoAsync();
        var emulatorInfo = await new EmulatorClient(EmulatorUri).GetInfoAsync();
        var isMain = serverInfo.Network == Network.Main;
        var dust = serverInfo.Dust.Satoshi;
        var funderScript = ArkAddress.Parse(_funder.Address).ScriptPubKey;
        var strangerScript = ArkAddress.Parse(_stranger.Address).ScriptPubKey;

        // Leaf: the child group (index 0 in the witness) must have its output at tx-output 0.
        var leaf = new List<byte>();
        leaf.AddRange(ArkadeCovenants.ChildAtOutput(0));
        leaf.Add(0x51); // OP_1
        var contract = new ArkadeArtifactContract(
            "child-at-output-probe", serverInfo.SignerKey, emulatorInfo.SignerPubkey,
            [new ArkadeContractFunction("mint", leaf.ToArray())]);
        var addr = contract.GetArkAddress().ToString(isMain);

        const long fund = 20_000;
        await _funder.SendAsync(addr, fund);
        var vtxo = await CovenantSpender.WaitForVtxoAsync(_funder, contract, TimeSpan.FromSeconds(45));

        // Witness: [childK=0]. The child is a fresh uncontrolled issuance = packet group 0.
        CovenantSpender.CovenantInput[] Inputs() =>
            [new(contract, "mint", [ArkadeCovenants.EncodeIndex(0)], vtxo)];
        AssetGroup Child(ushort vout) => AssetGroup.Create(null, null, [], [AssetOutput.Create(vout, 1)], []);

        // ── Cheat: route the child to output 1 (vout != 0) → 0xeb EQUALVERIFY refuses.
        //    output 0 = funder, output 1 = stranger (distinct scripts, no coalescing).
        var elsewhere = await Assert.ThrowsAnyAsync<Exception>(() => CovenantSpender.SpendManyAsync(
            _funder, EmulatorUri, Inputs(),
            [new TxOut(Money.Satoshis(dust), funderScript), new TxOut(Money.Satoshis(fund - dust), strangerScript)],
            extraPackets: [Packet.Create([Child(1)])]));
        Assert.Contains("Emulator rejected", elsewhere.Message);

        // ── Honest: the child at output 0 → co-signed.
        var response = await CovenantSpender.SpendManyAsync(
            _funder, EmulatorUri, Inputs(),
            [new TxOut(Money.Satoshis(fund), funderScript)],
            extraPackets: [Packet.Create([Child(0)])]);
        Assert.False(string.IsNullOrEmpty(response.SignedArkTx),
            "the honest child-at-output-0 must be emulator-co-signed");
    }
}
