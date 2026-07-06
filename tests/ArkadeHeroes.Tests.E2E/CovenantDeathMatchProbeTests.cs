using ArkadeHeroes.Chain.Covenants;
using ArkadeHeroes.Chain.NArk;
using ArkadeHeroes.Core.Fairness;
using NArk.Abstractions;
using NArk.Abstractions.Assets;
using NArk.Core.Assets;
using NBitcoin;

namespace ArkadeHeroes.Tests.E2E;

/// <summary>
/// The structural TEETH of the joint death-match covenant, live on regtest. Unlike
/// <see cref="CovenantDeathMatchE2ETests"/> (the honest happy path through the game
/// server), this drives the joint contract directly to prove the emulator REFUSES
/// every cheat — a settle keeping the loser alive, routing the loser to the winner,
/// paying the winner's hero to the wrong script, revealing the wrong seed, or forging
/// / cross-replaying the oracle signature — and that the introspection-bound refund
/// returns EACH hero only to ITS staker (a theft is refused; a pre-expiry claim is
/// refused). This is what makes the settle packet-trustless: the oracle attests only
/// the winning branch; the burn + return are covenant-enforced.
/// </summary>
public class CovenantDeathMatchProbeTests : IAsyncLifetime
{
    private static readonly Uri EmulatorUri = new("http://localhost:7073/");

    private SelfCustodyWallet _challenger = null!;
    private SelfCustodyWallet _defender = null!;
    private readonly List<string> _dbPaths = [];

    public async Task InitializeAsync()
    {
        await RegtestHelper.WaitForArkdReadyAsync(TimeSpan.FromSeconds(30));
        _challenger = await NewWalletAsync();
        _defender = await NewWalletAsync();
        // The challenger issues + stakes BOTH heroes (the covenant only cares about the
        // asset ids, not who deposits); the defender wallet supplies the second script.
        await RegtestHelper.ArkSend(_challenger.Address, 200_000);
        await _challenger.WaitForBalanceAsync(200_000, TimeSpan.FromSeconds(60));
    }

    private async Task<SelfCustodyWallet> NewWalletAsync()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"ah-dmprobe-{Guid.NewGuid():N}.db");
        _dbPaths.Add(dbPath);
        return await SelfCustodyWallet.CreateAsync(new SelfCustodyWalletOptions
        {
            ArkUri = "http://localhost:7070",
            DbPath = dbPath,
        });
    }

    public async Task DisposeAsync()
    {
        await _challenger.DisposeAsync();
        await _defender.DisposeAsync();
        foreach (var path in _dbPaths)
            try { if (File.Exists(path)) File.Delete(path); } catch { /* windows lock */ }
    }

    private async Task<(string CAsset, string DAsset)> IssueTwoHeroesAsync()
    {
        var assetManager = _challenger.GetService<IAssetManager>();
        var c = await assetManager.IssueAsync(_challenger.WalletId, new IssuanceParams(Amount: 1));
        var d = await assetManager.IssueAsync(_challenger.WalletId, new IssuanceParams(Amount: 1));
        await _challenger.WaitForAssetAsync(c.AssetId, TimeSpan.FromSeconds(30));
        await _challenger.WaitForAssetAsync(d.AssetId, TimeSpan.FromSeconds(30));
        return (c.AssetId, d.AssetId);
    }

    private static (byte[] Priv, byte[] Pub) NewOracle()
    {
        var key = NBitcoin.Secp256k1.ECPrivKey.Create(
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        Span<byte> pk = stackalloc byte[32];
        key.CreateXOnlyPubKey().WriteToSpan(pk);
        byte[] priv = new byte[32];
        key.WriteToSpan(priv);
        return (priv, pk.ToArray());
    }

    private static byte[] SignSettle(byte[] oraclePriv, string matchId, bool challengerWon)
    {
        var key = NBitcoin.Secp256k1.ECPrivKey.Create(oraclePriv);
        var sig = key.SignBIP340(ArkadeCovenants.DeathMatchSettleMessage(matchId, challengerWon));
        var bytes = new byte[64];
        sig.WriteToSpan(bytes);
        return bytes;
    }

    [Fact]
    public async Task JointSettle_HonestCoSigned_AllCheatsRefused()
    {
        var transport = _challenger.GetService<global::NArk.Core.Transport.IClientTransport>();
        var serverInfo = await transport.GetServerInfoAsync();
        var emulatorInfo = await new EmulatorClient(EmulatorUri).GetInfoAsync();
        var isMain = serverInfo.Network == Network.Main;
        var dust = serverInfo.Dust.Satoshi;

        var (cAssetId, dAssetId) = await IssueTwoHeroesAsync();
        var cAsset = AssetId.FromString(cAssetId);
        var dAsset = AssetId.FromString(dAssetId);
        var challengerScript = ArkAddress.Parse(_challenger.Address).ScriptPubKey;
        var wrongScript = ArkAddress.Parse(_defender.Address).ScriptPubKey;

        var serverSeed = CommitReveal.NewSeed();
        var commitment = Convert.FromHexString(CommitReveal.Commit(serverSeed));
        var (oraclePriv, oraclePk) = NewOracle();
        const string matchId = "e2e-dm-probe-settle";
        var refundAfter = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(); // refunds not in play here

        // The joint contract: challenger is the winner in the honest run (settleToChallenger).
        var parameters = new DeathMatchJointEscrowParams(
            _challenger.Address, cAssetId, _defender.Address, dAssetId,
            Convert.ToHexString(commitment).ToLowerInvariant(), Convert.ToHexString(oraclePk).ToLowerInvariant(),
            matchId, dust, refundAfter);
        var contract = DeathMatchEscrowContracts.BuildJoint(parameters, serverInfo.SignerKey, emulatorInfo.SignerPubkey);
        var contractAddr = contract.GetArkAddress().ToString(isMain);

        // Both heroes staked into the ONE joint address.
        await _challenger.SendAssetAsync(contractAddr, cAssetId, 1);
        await _challenger.SendAssetAsync(contractAddr, dAssetId, 1);
        var vtxos = await CovenantSpender.WaitForVtxosAsync(_challenger, contract, 2, TimeSpan.FromSeconds(45));
        var cVtxo = vtxos.First(v => v.Assets!.Any(a => a.AssetId == cAssetId));
        var dVtxo = vtxos.First(v => v.Assets!.Any(a => a.AssetId == dAssetId));
        var total = (long)cVtxo.Amount + (long)dVtxo.Amount;

        // settleToChallenger: winner = challenger's hero (C) vin 0, loser = defender's hero (D) vin 1.
        CovenantSpender.CovenantInput[] Inputs(byte[] seed, byte[] sig) =>
        [
            new(contract, "settleToChallenger", [seed, sig], cVtxo),
            new(contract, "settleToChallenger", [seed, sig], dVtxo),
        ];
        Packet HonestPacket() => Packet.Create(
        [
            AssetGroup.Create(cAsset, null, [AssetInput.Create(0, 1)], [AssetOutput.Create(0, 1)], []),
            AssetGroup.Create(dAsset, null, [AssetInput.Create(1, 1)], [], []),   // loser burned
        ]);
        var honestSig = SignSettle(oraclePriv, matchId, challengerWon: true);

        // ── Cheat: FORGED oracle signature (an attacker's key) → CSFS refuses.
        var (forgerPriv, _) = NewOracle();
        var forged = await Assert.ThrowsAnyAsync<Exception>(() => CovenantSpender.SpendManyAsync(
            _challenger, EmulatorUri, Inputs(serverSeed, SignSettle(forgerPriv, matchId, true)),
            [new TxOut(Money.Satoshis(total), challengerScript)], extraPackets: [HonestPacket()]));
        Assert.Contains("Emulator rejected", forged.Message);

        // ── Cheat: CROSS-BRANCH replay — the oracle's DEFENDER-branch sig can't
        //    authorize settleToChallenger (its baked message differs).
        var crossBranch = await Assert.ThrowsAnyAsync<Exception>(() => CovenantSpender.SpendManyAsync(
            _challenger, EmulatorUri, Inputs(serverSeed, SignSettle(oraclePriv, matchId, challengerWon: false)),
            [new TxOut(Money.Satoshis(total), challengerScript)], extraPackets: [HonestPacket()]));
        Assert.Contains("Emulator rejected", crossBranch.Message);

        // ── Cheat: WRONG seed — the commit gate fails even with a valid oracle sig.
        var wrongSeed = await Assert.ThrowsAnyAsync<Exception>(() => CovenantSpender.SpendManyAsync(
            _challenger, EmulatorUri, Inputs(CommitReveal.NewSeed(), honestSig),
            [new TxOut(Money.Satoshis(total), challengerScript)], extraPackets: [HonestPacket()]));
        Assert.Contains("Emulator rejected", wrongSeed.Message);

        // ── Cheat: KEEP THE LOSER ALIVE — route D to its own second output.
        var keepAlive = Packet.Create(
        [
            AssetGroup.Create(cAsset, null, [AssetInput.Create(0, 1)], [AssetOutput.Create(0, 1)], []),
            AssetGroup.Create(dAsset, null, [AssetInput.Create(1, 1)], [AssetOutput.Create(1, 1)], []),
        ]);
        var alive = await Assert.ThrowsAnyAsync<Exception>(() => CovenantSpender.SpendManyAsync(
            _challenger, EmulatorUri, Inputs(serverSeed, honestSig),
            [new TxOut(Money.Satoshis(dust), challengerScript), new TxOut(Money.Satoshis(dust), challengerScript)],
            extraPackets: [keepAlive]));
        Assert.Contains("Emulator rejected", alive.Message);

        // ── Cheat: route the LOSER to the WINNER's output (0).
        var toWinner = Packet.Create(
        [
            AssetGroup.Create(cAsset, null, [AssetInput.Create(0, 1)], [AssetOutput.Create(0, 1)], []),
            AssetGroup.Create(dAsset, null, [AssetInput.Create(1, 1)], [AssetOutput.Create(0, 1)], []),
        ]);
        var loserToWinner = await Assert.ThrowsAnyAsync<Exception>(() => CovenantSpender.SpendManyAsync(
            _challenger, EmulatorUri, Inputs(serverSeed, honestSig),
            [new TxOut(Money.Satoshis(total), challengerScript)], extraPackets: [toWinner]));
        Assert.Contains("Emulator rejected", loserToWinner.Message);

        // ── Cheat: pay the WINNER's hero to the WRONG script.
        var wrongDest = await Assert.ThrowsAnyAsync<Exception>(() => CovenantSpender.SpendManyAsync(
            _challenger, EmulatorUri, Inputs(serverSeed, honestSig),
            [new TxOut(Money.Satoshis(total), wrongScript)], extraPackets: [HonestPacket()]));
        Assert.Contains("Emulator rejected", wrongDest.Message);

        // ── Honest settle: valid oracle sig on THIS branch, revealed seed, winner's
        //    hero at output 0 paying the winner, loser's hero burned → co-signed.
        var response = await CovenantSpender.SpendManyAsync(
            _challenger, EmulatorUri, Inputs(serverSeed, honestSig),
            [new TxOut(Money.Satoshis(total), challengerScript)], extraPackets: [HonestPacket()]);
        Assert.False(string.IsNullOrEmpty(response.SignedArkTx), "the honest joint settle must be emulator-co-signed");
        Assert.Equal(2, response.SignedCheckpointTxs.Length);
    }

    [Fact]
    public async Task JointRefund_EachHeroHomeAfterExpiry_TheftRefused_PreExpiryRefused()
    {
        var transport = _challenger.GetService<global::NArk.Core.Transport.IClientTransport>();
        var serverInfo = await transport.GetServerInfoAsync();
        var emulatorInfo = await new EmulatorClient(EmulatorUri).GetInfoAsync();
        var isMain = serverInfo.Network == Network.Main;
        var dust = serverInfo.Dust.Satoshi;

        var (cAssetId, dAssetId) = await IssueTwoHeroesAsync();
        var cAsset = AssetId.FromString(cAssetId);
        var dAsset = AssetId.FromString(dAssetId);
        var challengerScript = ArkAddress.Parse(_challenger.Address).ScriptPubKey;
        var defenderScript = ArkAddress.Parse(_defender.Address).ScriptPubKey;

        var serverSeed = CommitReveal.NewSeed();
        var commitment = Convert.FromHexString(CommitReveal.Commit(serverSeed));
        var (_, oraclePk) = NewOracle();
        const string matchId = "e2e-dm-probe-refund";
        // A match neither party ever settled: the refund leaf returns EACH hero home
        // after expiry — no oracle, no server, pure covenant.
        var refundAfter = DateTimeOffset.UtcNow.AddSeconds(8).ToUnixTimeSeconds();

        var parameters = new DeathMatchJointEscrowParams(
            _challenger.Address, cAssetId, _defender.Address, dAssetId,
            Convert.ToHexString(commitment).ToLowerInvariant(), Convert.ToHexString(oraclePk).ToLowerInvariant(),
            matchId, dust, refundAfter);
        var contract = DeathMatchEscrowContracts.BuildJoint(parameters, serverInfo.SignerKey, emulatorInfo.SignerPubkey);
        var contractAddr = contract.GetArkAddress().ToString(isMain);

        await _challenger.SendAssetAsync(contractAddr, cAssetId, 1);
        await _challenger.SendAssetAsync(contractAddr, dAssetId, 1);
        var vtxos = await CovenantSpender.WaitForVtxosAsync(_challenger, contract, 2, TimeSpan.FromSeconds(45));
        var cVtxo = vtxos.First(v => v.Assets!.Any(a => a.AssetId == cAssetId));
        var dVtxo = vtxos.First(v => v.Assets!.Any(a => a.AssetId == dAssetId));

        // The refund routes C → output 0 (challenger), D → output 1 (defender), both
        // passthrough. Challenger's carrier vin 0, defender's vin 1.
        CovenantSpender.CovenantInput[] RefundInputs(long txLockTime) =>
        [
            new(contract, "refund", [], cVtxo, LockTime: new LockTime((uint)txLockTime)),
            new(contract, "refund", [], dVtxo, LockTime: new LockTime((uint)txLockTime)),
        ];
        Packet RefundPacket() => Packet.Create(
        [
            AssetGroup.Create(cAsset, null, [AssetInput.Create(0, 1)], [AssetOutput.Create(0, 1)], []),
            AssetGroup.Create(dAsset, null, [AssetInput.Create(1, 1)], [AssetOutput.Create(1, 1)], []),
        ]);
        TxOut[] HomeOutputs() =>
            [new TxOut(Money.Satoshis(dust), challengerScript), new TxOut(Money.Satoshis(dust), defenderScript)];

        // Before expiry: refused by arkd's forfeit-closure gate (leaf CLTV vs blocktime).
        // Use a disposable non-canonical locktime (expiry+1) so a refused submit never
        // poisons the canonical refund txid below (arkd v0.9.9-rc.1 sticky-failure bug).
        var early = await Assert.ThrowsAnyAsync<Exception>(() => CovenantSpender.SpendManyAsync(
            _challenger, EmulatorUri, RefundInputs(refundAfter + 1), HomeOutputs(), extraPackets: [RefundPacket()]));
        Assert.False(early is TimeoutException, $"expected a refusal, got: {early.Message}");

        // Wait for the CHAIN's clock to pass expiry.
        await RegtestHelper.WaitForChainTimeAsync(refundAfter, TimeSpan.FromSeconds(120));

        // THEFT: route the challenger's hero to the DEFENDER's script (output 0 pays the
        // wrong party). The refund leaf's AssetAtOutput(0, C, challengerScript) refuses it.
        // Distinct outputs → distinct txid (disposable), so this never touches the canonical
        // refund below.
        var theft = await Assert.ThrowsAnyAsync<Exception>(() => CovenantSpender.SpendManyAsync(
            _challenger, EmulatorUri, RefundInputs(refundAfter),
            [new TxOut(Money.Satoshis(dust), defenderScript), new TxOut(Money.Satoshis(dust), defenderScript)],
            extraPackets: [RefundPacket()]));
        Assert.Contains("Emulator rejected", theft.Message);

        // Canonical honest refund (submit exactly ONCE): each hero home, co-signed.
        var refund = await CovenantSpender.SpendManyAsync(
            _challenger, EmulatorUri, RefundInputs(refundAfter), HomeOutputs(), extraPackets: [RefundPacket()]);
        Assert.False(string.IsNullOrEmpty(refund.SignedArkTx), "the post-expiry refund must be emulator-co-signed");
        Assert.Equal(2, refund.SignedCheckpointTxs.Length);
    }
}
