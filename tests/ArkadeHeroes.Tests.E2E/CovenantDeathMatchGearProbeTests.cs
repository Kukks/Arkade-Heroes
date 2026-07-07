using ArkadeHeroes.Chain.Covenants;
using ArkadeHeroes.Chain.NArk;
using ArkadeHeroes.Core.Fairness;
using NArk.Abstractions;
using NArk.Abstractions.Assets;
using NArk.Core.Assets;
using NBitcoin;

namespace ArkadeHeroes.Tests.E2E;

/// <summary>
/// The structural TEETH of the GEARED death-match covenant, live on regtest. Extends the
/// proven joint escrow with covenant-staked gear: both sides stake their hero PLUS gear
/// units; the settle routes ALL gear to the winner (including the SAME fungible item staked
/// by BOTH sides — the amount-2 aggregation at output 0); the refund routes each side's hero
/// + own gear home. Cheats refused: gear kept by the loser (gear-theft), only 1 of 2 units
/// delivered (gear-shortchange), a refund stealing the other side's gear. This is also the
/// largest covenant spend yet (5 inputs) — the input-count scaling probe.
/// </summary>
public class CovenantDeathMatchGearProbeTests : IAsyncLifetime
{
    private static readonly Uri EmulatorUri = new("http://localhost:7073/");

    private SelfCustodyWallet _challenger = null!;
    private SelfCustodyWallet _defender = null!;
    private readonly List<string> _dbPaths = [];

    public async Task InitializeAsync()
    {
        await RegtestHelper.WaitForArkdReadyAsync(TimeSpan.FromSeconds(30));
        _challenger = await NewWalletAsync();
        _defender = await NewWalletAsync(); // supplies the second party's script
        await RegtestHelper.ArkSend(_challenger.Address, 300_000);
        await _challenger.WaitForBalanceAsync(300_000, TimeSpan.FromSeconds(60));
    }

    private async Task<SelfCustodyWallet> NewWalletAsync()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"ah-gearprobe-{Guid.NewGuid():N}.db");
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
        foreach (var p in _dbPaths)
            try { if (File.Exists(p)) File.Delete(p); } catch { /* windows lock */ }
    }

    private static (byte[] Priv, byte[] Pub) NewOracle()
    {
        var key = NBitcoin.Secp256k1.ECPrivKey.Create(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        Span<byte> pk = stackalloc byte[32];
        key.CreateXOnlyPubKey().WriteToSpan(pk);
        var priv = new byte[32];
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

    private async Task<string> IssueAsync(ulong amount)
    {
        var mgr = _challenger.GetService<IAssetManager>();
        var res = await mgr.IssueAsync(_challenger.WalletId, new IssuanceParams(Amount: amount));
        await _challenger.WaitForAssetAsync(res.AssetId, TimeSpan.FromSeconds(30));
        return res.AssetId;
    }

    /// <summary>Issues heroes + gear, builds the geared joint escrow, stakes everything (5 carriers), and returns the spend fixtures.</summary>
    private async Task<(ArkadeArtifactContract Contract, List<global::NArk.Abstractions.VTXOs.ArkVtxo> Ordered,
        AssetId CHero, AssetId DHero, AssetId GearX, AssetId GearY, long Total, Script ChalScript, Script DefScript)>
        StakeGearedEscrowAsync(string matchId, byte[] commitment, byte[] oraclePk, long refundAfter)
    {
        var transport = _challenger.GetService<global::NArk.Core.Transport.IClientTransport>();
        var serverInfo = await transport.GetServerInfoAsync();
        var emulatorInfo = await new EmulatorClient(EmulatorUri).GetInfoAsync();
        var isMain = serverInfo.Network == Network.Main;

        var cHeroId = await IssueAsync(1);
        var dHeroId = await IssueAsync(1);
        var gearXId = await IssueAsync(2); // staked by BOTH sides → the amount-2 aggregation
        var gearYId = await IssueAsync(1); // staked by the defender only

        var parameters = new DeathMatchJointEscrowParams(
            _challenger.Address, cHeroId, _defender.Address, dHeroId,
            Convert.ToHexString(commitment).ToLowerInvariant(), Convert.ToHexString(oraclePk).ToLowerInvariant(),
            matchId, serverInfo.Dust.Satoshi, refundAfter,
            ChallengerGear: [new GearStake(gearXId, 1)],
            DefenderGear: [new GearStake(gearXId, 1), new GearStake(gearYId, 1)]);
        var contract = DeathMatchEscrowContracts.BuildJoint(parameters, serverInfo.SignerKey, emulatorInfo.SignerPubkey);
        var addr = contract.GetArkAddress().ToString(isMain);

        // 5 carriers: both heroes + gearX twice (separate deposits → separate vins,
        // exercising cross-carrier aggregation) + gearY.
        await _challenger.SendAssetAsync(addr, cHeroId, 1);
        await _challenger.SendAssetAsync(addr, dHeroId, 1);
        await _challenger.SendAssetAsync(addr, gearXId, 1);
        await _challenger.SendAssetAsync(addr, gearXId, 1);
        await _challenger.SendAssetAsync(addr, gearYId, 1);
        var vtxos = await CovenantSpender.WaitForVtxosAsync(_challenger, contract, 5, TimeSpan.FromSeconds(60));

        var cHeroVtxo = vtxos.First(v => v.Assets?.Any(a => a.AssetId == cHeroId) == true);
        var dHeroVtxo = vtxos.First(v => v.Assets?.Any(a => a.AssetId == dHeroId) == true);
        var gearCarriers = vtxos
            .Where(v => v.OutPoint != cHeroVtxo.OutPoint && v.OutPoint != dHeroVtxo.OutPoint)
            .OrderBy(v => v.OutPoint.ToString(), StringComparer.Ordinal)
            .ToList();
        var ordered = new List<global::NArk.Abstractions.VTXOs.ArkVtxo> { cHeroVtxo, dHeroVtxo };
        ordered.AddRange(gearCarriers);

        return (contract, ordered,
            AssetId.FromString(cHeroId), AssetId.FromString(dHeroId),
            AssetId.FromString(gearXId), AssetId.FromString(gearYId),
            ordered.Sum(v => (long)v.Amount),
            ArkAddress.Parse(_challenger.Address).ScriptPubKey,
            ArkAddress.Parse(_defender.Address).ScriptPubKey);
    }

    /// <summary>Per-vin inputs for an asset across the ordered carriers.</summary>
    private static List<AssetInput> InputsOf(List<global::NArk.Abstractions.VTXOs.ArkVtxo> ordered, AssetId asset)
    {
        var ins = new List<AssetInput>();
        for (var vin = 0; vin < ordered.Count; vin++)
        {
            var amt = ordered[vin].Assets?.Where(a => a.AssetId == asset.ToString()).Aggregate(0UL, (s, a) => s + a.Amount) ?? 0;
            if (amt > 0) ins.Add(AssetInput.Create((ushort)vin, amt));
        }
        return ins;
    }

    [Fact]
    public async Task GearedSettle_AllGearToWinner_TheftAndShortchangeRefused()
    {
        var serverSeed = CommitReveal.NewSeed();
        var commitment = Convert.FromHexString(CommitReveal.Commit(serverSeed));
        var (oraclePriv, oraclePk) = NewOracle();
        const string matchId = "e2e-gear-settle";
        var refundAfter = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();

        var (contract, ordered, cHero, dHero, gearX, gearY, total, chalScript, defScript) =
            await StakeGearedEscrowAsync(matchId, commitment, oraclePk, refundAfter);
        var honestSig = SignSettle(oraclePriv, matchId, challengerWon: true);
        var dust = 330L;

        CovenantSpender.CovenantInput[] Inputs() => ordered
            .Select(v => new CovenantSpender.CovenantInput(contract, "settleToChallenger", [serverSeed, honestSig], v))
            .ToArray();
        AssetGroup WinnerHero() => AssetGroup.Create(cHero, null, [AssetInput.Create(0, 1)], [AssetOutput.Create(0, 1)], []);
        AssetGroup LoserBurn() => AssetGroup.Create(dHero, null, [AssetInput.Create(1, 1)], [], []);

        // ── Cheat: GEAR-THEFT — gearY routed to output 1 (the loser keeps it)
        //    instead of the winner's output 0 → AssetAtOutput(0, gearY, winner, 1) refused.
        var theft = await Assert.ThrowsAnyAsync<Exception>(() => CovenantSpender.SpendManyAsync(
            _challenger, EmulatorUri, Inputs(),
            [new TxOut(Money.Satoshis(total - dust), chalScript), new TxOut(Money.Satoshis(dust), defScript)],
            extraPackets: [Packet.Create(
            [
                WinnerHero(), LoserBurn(),
                AssetGroup.Create(gearX, null, InputsOf(ordered, gearX), [AssetOutput.Create(0, 2)], []),
                AssetGroup.Create(gearY, null, InputsOf(ordered, gearY), [AssetOutput.Create(1, 1)], []),
            ])]));
        Assert.Contains("Emulator rejected", theft.Message);

        // ── Cheat: GEAR-SHORTCHANGE — only 1 of gearX's 2 units delivered to output 0
        //    (the other burned) → the amount==2 check refused.
        var shortchange = await Assert.ThrowsAnyAsync<Exception>(() => CovenantSpender.SpendManyAsync(
            _challenger, EmulatorUri, Inputs(),
            [new TxOut(Money.Satoshis(total), chalScript)],
            extraPackets: [Packet.Create(
            [
                WinnerHero(), LoserBurn(),
                AssetGroup.Create(gearX, null, InputsOf(ordered, gearX), [AssetOutput.Create(0, 1)], []), // 1 of 2
                AssetGroup.Create(gearY, null, InputsOf(ordered, gearY), [AssetOutput.Create(0, 1)], []),
            ])]));
        Assert.Contains("Emulator rejected", shortchange.Message);

        // ── Honest geared settle: winner hero + gearX(2) + gearY(1) → output 0,
        //    loser hero burned → co-signed.
        var response = await CovenantSpender.SpendManyAsync(
            _challenger, EmulatorUri, Inputs(),
            [new TxOut(Money.Satoshis(total), chalScript)],
            extraPackets: [Packet.Create(
            [
                WinnerHero(), LoserBurn(),
                AssetGroup.Create(gearX, null, InputsOf(ordered, gearX), [AssetOutput.Create(0, 2)], []),
                AssetGroup.Create(gearY, null, InputsOf(ordered, gearY), [AssetOutput.Create(0, 1)], []),
            ])]);
        Assert.False(string.IsNullOrEmpty(response.SignedArkTx), "the honest geared settle must be emulator-co-signed");
    }

    [Fact]
    public async Task GearedRefund_EachSideHomeAfterExpiry_GearTheftAndPreExpiryRefused()
    {
        var serverSeed = CommitReveal.NewSeed();
        var commitment = Convert.FromHexString(CommitReveal.Commit(serverSeed));
        var (_, oraclePk) = NewOracle();
        const string matchId = "e2e-gear-refund";
        var refundAfter = DateTimeOffset.UtcNow.AddSeconds(8).ToUnixTimeSeconds();

        var (contract, ordered, cHero, dHero, gearX, gearY, total, chalScript, defScript) =
            await StakeGearedEscrowAsync(matchId, commitment, oraclePk, refundAfter);
        var dust = 330L;

        CovenantSpender.CovenantInput[] RefundInputs(long lockTime) => ordered
            .Select(v => new CovenantSpender.CovenantInput(contract, "refund", [], v, LockTime: new LockTime((uint)lockTime)))
            .ToArray();
        // Honest routing: challenger hero + 1 gearX → out 0; defender hero + 1 gearX + gearY → out 1.
        Packet HonestPacket() => Packet.Create(
        [
            AssetGroup.Create(cHero, null, [AssetInput.Create(0, 1)], [AssetOutput.Create(0, 1)], []),
            AssetGroup.Create(dHero, null, [AssetInput.Create(1, 1)], [AssetOutput.Create(1, 1)], []),
            AssetGroup.Create(gearX, null, InputsOf(ordered, gearX), [AssetOutput.Create(0, 1), AssetOutput.Create(1, 1)], []),
            AssetGroup.Create(gearY, null, InputsOf(ordered, gearY), [AssetOutput.Create(1, 1)], []),
        ]);
        TxOut[] HomeOutputs() =>
            [new TxOut(Money.Satoshis(2 * dust), chalScript), new TxOut(Money.Satoshis(total - 2 * dust), defScript)];

        // ── Pre-expiry: refused (disposable non-canonical locktime = expiry+1).
        var early = await Assert.ThrowsAnyAsync<Exception>(() => CovenantSpender.SpendManyAsync(
            _challenger, EmulatorUri, RefundInputs(refundAfter + 1), HomeOutputs(), extraPackets: [HonestPacket()]));
        Assert.False(early is TimeoutException, $"expected a refusal, got: {early.Message}");

        await RegtestHelper.WaitForChainTimeAsync(refundAfter, TimeSpan.FromSeconds(120));

        // ── REFUND GEAR-THEFT: the challenger routes the defender's gearY to output 0
        //    (their own side) → AssetAtOutput(1, gearY, defScript, 1) refused.
        var theft = await Assert.ThrowsAnyAsync<Exception>(() => CovenantSpender.SpendManyAsync(
            _challenger, EmulatorUri, RefundInputs(refundAfter),
            [new TxOut(Money.Satoshis(3 * dust), chalScript), new TxOut(Money.Satoshis(total - 3 * dust), defScript)],
            extraPackets: [Packet.Create(
            [
                AssetGroup.Create(cHero, null, [AssetInput.Create(0, 1)], [AssetOutput.Create(0, 1)], []),
                AssetGroup.Create(dHero, null, [AssetInput.Create(1, 1)], [AssetOutput.Create(1, 1)], []),
                AssetGroup.Create(gearX, null, InputsOf(ordered, gearX), [AssetOutput.Create(0, 1), AssetOutput.Create(1, 1)], []),
                AssetGroup.Create(gearY, null, InputsOf(ordered, gearY), [AssetOutput.Create(0, 1)], []), // stolen
            ])]));
        Assert.Contains("Emulator rejected", theft.Message);

        // ── Honest post-expiry refund (submit ONCE): each side's hero + own gear home.
        var refund = await CovenantSpender.SpendManyAsync(
            _challenger, EmulatorUri, RefundInputs(refundAfter), HomeOutputs(), extraPackets: [HonestPacket()]);
        Assert.False(string.IsNullOrEmpty(refund.SignedArkTx), "the post-expiry geared refund must be emulator-co-signed");
    }
}
