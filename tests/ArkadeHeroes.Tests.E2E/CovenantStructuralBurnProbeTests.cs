using ArkadeHeroes.Chain.Covenants;
using ArkadeHeroes.Chain.NArk;
using NArk.Abstractions;
using NArk.Abstractions.Assets;
using NArk.Core.Assets;
using NBitcoin;

namespace ArkadeHeroes.Tests.E2E;

/// <summary>
/// THE GO/NO-GO probe for covenant-architecture v2. Proves — live against the
/// emulator, in isolation from any death-match machinery — that the two new
/// structural introspection helpers can enforce a burn-one/return-one settle
/// WITHOUT trusting the server-built asset packet:
///   • <see cref="ArkadeCovenants.AssetAtOutput"/> — the winner's asset lands at
///     the winner's output (0xef amount==1 + 0xd1 output-script);
///   • <see cref="ArkadeCovenants.AssetBurned"/> — the loser's asset is absent
///     (0xef flag==0) from every output = burned.
/// The leaf carries ONLY these structural checks (no oracle gate) so a refusal
/// is unambiguously the structural teeth, not a signature/seed gate. If the
/// honest spend co-signs and all three cheats are refused, the whole v2 model is
/// proven and the death-match rebuild (Rung 2) can proceed on the same helpers.
/// </summary>
public class CovenantStructuralBurnProbeTests : IAsyncLifetime
{
    private static readonly Uri EmulatorUri = new("http://localhost:7073/");

    // Generous absence sweep: the honest settle tx has 2 real outputs (winner +
    // the appended extension); a cheat adds at most one (the smuggled loser
    // output). 0xef has no range check on the output index — it just returns
    // "absent" for a non-existent output — so over-sweeping is harmless (absent =
    // pass) while under-sweeping would let a cheat route the loser past the check.
    private const int OutputCount = 4;

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
        var dbPath = Path.Combine(Path.GetTempPath(), $"ah-burnprobe-{Guid.NewGuid():N}.db");
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
        foreach (var path in _dbPaths)
            try { if (File.Exists(path)) File.Delete(path); } catch { /* windows lock */ }
    }

    [Fact]
    public async Task StructuralSettle_WinnerReturnedLoserBurned_CheatsRefused()
    {
        var transport = _funder.GetService<global::NArk.Core.Transport.IClientTransport>();
        var serverInfo = await transport.GetServerInfoAsync();
        var emulatorInfo = await new EmulatorClient(EmulatorUri).GetInfoAsync();
        var isMain = serverInfo.Network == Network.Main;
        var dust = serverInfo.Dust.Satoshi;

        // Two distinct assets standing in for the two staked heroes: winner (W)
        // and loser (L). Uncontrolled issuance (Amount 1) — no species needed for
        // the probe; the structural checks only see the canonical asset id.
        var assetManager = _funder.GetService<IAssetManager>();
        var wRes = await assetManager.IssueAsync(_funder.WalletId, new IssuanceParams(Amount: 1));
        var lRes = await assetManager.IssueAsync(_funder.WalletId, new IssuanceParams(Amount: 1));
        await _funder.WaitForAssetAsync(wRes.AssetId, TimeSpan.FromSeconds(30));
        await _funder.WaitForAssetAsync(lRes.AssetId, TimeSpan.FromSeconds(30));

        var winnerAsset = AssetId.FromString(wRes.AssetId);
        var loserAsset = AssetId.FromString(lRes.AssetId);
        var winnerScript = ArkAddress.Parse(_funder.Address).ScriptPubKey;
        var wrongScript = ArkAddress.Parse(_stranger.Address).ScriptPubKey;

        // The JOINT escrow: ONE contract, one "settle" leaf structurally binding
        // BOTH consequences. The leaf takes NO witness — every index/asset/script
        // is baked, so there is no witness freedom to exploit.
        var settleLeaf = new List<byte>();
        settleLeaf.AddRange(ArkadeCovenants.AssetAtOutput(0, winnerAsset, winnerScript));
        settleLeaf.AddRange(ArkadeCovenants.AssetBurned(loserAsset, OutputCount));
        settleLeaf.Add(0x51); // OP_1 — leave EXACTLY one truthy stack item (the death-match lesson)
        var contract = new ArkadeArtifactContract(
            "structural-burn-probe", serverInfo.SignerKey, emulatorInfo.SignerPubkey,
            [new ArkadeContractFunction("settle", settleLeaf.ToArray())]);
        var contractAddr = contract.GetArkAddress().ToString(isMain);

        // Stake both assets into the ONE joint address (the joint-escrow shape).
        await _funder.SendAssetAsync(contractAddr, wRes.AssetId, 1);
        await _funder.SendAssetAsync(contractAddr, lRes.AssetId, 1);
        var vtxos = await CovenantSpender.WaitForVtxosAsync(_funder, contract, 2, TimeSpan.FromSeconds(45));
        var winnerVtxo = vtxos.First(v => v.Assets!.Any(a => a.AssetId == wRes.AssetId));
        var loserVtxo = vtxos.First(v => v.Assets!.Any(a => a.AssetId == lRes.AssetId));
        var total = (long)winnerVtxo.Amount + (long)loserVtxo.Amount;

        // Winner carrier vin 0, loser carrier vin 1 — the packet vins the leaf's
        // baked checks assume. Witness EMPTY (fully baked leaf).
        CovenantSpender.CovenantInput[] Inputs() =>
        [
            new(contract, "settle", [], winnerVtxo),
            new(contract, "settle", [], loserVtxo),
        ];

        // The honest packet: winner passes through (vin0 → out0), loser BURNED
        // (declared as vin1 with NO output — arkd destroys it).
        Packet HonestPacket() => Packet.Create(
        [
            AssetGroup.Create(winnerAsset, null, [AssetInput.Create(0, 1)], [AssetOutput.Create(0, 1)], []),
            AssetGroup.Create(loserAsset, null, [AssetInput.Create(1, 1)], [], []),
        ]);

        // ── Cheat A: keep the loser ALIVE — route L to its own second output.
        //    AssetBurned finds L at output 1 (flag 1) → refused.
        var keepAlive = Packet.Create(
        [
            AssetGroup.Create(winnerAsset, null, [AssetInput.Create(0, 1)], [AssetOutput.Create(0, 1)], []),
            AssetGroup.Create(loserAsset, null, [AssetInput.Create(1, 1)], [AssetOutput.Create(1, 1)], []),
        ]);
        var cheatA = await Assert.ThrowsAnyAsync<Exception>(() => CovenantSpender.SpendManyAsync(
            _funder, EmulatorUri, Inputs(),
            [new TxOut(Money.Satoshis(dust), winnerScript), new TxOut(Money.Satoshis(dust), winnerScript)],
            extraPackets: [keepAlive]));
        Assert.Contains("Emulator rejected", cheatA.Message);

        // ── Cheat B: route the loser to the WINNER's output (0) — a sneaky
        //    "return both to the winner". AssetBurned finds L at output 0 → refused.
        var toWinner = Packet.Create(
        [
            AssetGroup.Create(winnerAsset, null, [AssetInput.Create(0, 1)], [AssetOutput.Create(0, 1)], []),
            AssetGroup.Create(loserAsset, null, [AssetInput.Create(1, 1)], [AssetOutput.Create(0, 1)], []),
        ]);
        var cheatB = await Assert.ThrowsAnyAsync<Exception>(() => CovenantSpender.SpendManyAsync(
            _funder, EmulatorUri, Inputs(),
            [new TxOut(Money.Satoshis(total), winnerScript)],
            extraPackets: [toWinner]));
        Assert.Contains("Emulator rejected", cheatB.Message);

        // ── Cheat C: burn the loser correctly but pay the WINNER's asset to the
        //    WRONG script. AssetAtOutput's 0xd1 output-script check → refused.
        var cheatC = await Assert.ThrowsAnyAsync<Exception>(() => CovenantSpender.SpendManyAsync(
            _funder, EmulatorUri, Inputs(),
            [new TxOut(Money.Satoshis(total), wrongScript)],
            extraPackets: [HonestPacket()]));
        Assert.Contains("Emulator rejected", cheatC.Message);

        // ── Honest settle: winner asset → winner (output 0), loser asset BURNED.
        //    The emulator co-signs — the structural checks are satisfied with NO
        //    oracle, NO packet trust.
        var response = await CovenantSpender.SpendManyAsync(
            _funder, EmulatorUri, Inputs(),
            [new TxOut(Money.Satoshis(total), winnerScript)],
            extraPackets: [HonestPacket()]);
        Assert.False(string.IsNullOrEmpty(response.SignedArkTx),
            "the honest burn-one/return-one settle must be emulator-co-signed");
        Assert.Equal(2, response.SignedCheckpointTxs.Length);
    }
}
