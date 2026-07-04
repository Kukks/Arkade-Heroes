using ArkadeHeroes.Chain.Covenants;
using ArkadeHeroes.Chain.NArk;
using ArkadeHeroes.Core.Fairness;
using NArk.Abstractions;
using NBitcoin;

namespace ArkadeHeroes.Tests.E2E;

/// <summary>
/// The wager-escrow covenant live on regtest (coinflip's trustless-match
/// shape): both players fund stake VTXOs under one escrow contract whose two
/// settle branches each require (a) the pre-committed server seed revealed and
/// (b) BOTH stakes spent atomically with the full pot paid to that branch's
/// winner. Wrong seed → refused. Short pot → refused. Honest settle → both
/// stakes sweep to the winner in one emulator-co-signed transaction.
/// </summary>
public class WagerEscrowCovenantTests : IAsyncLifetime
{
    private static readonly Uri EmulatorUri = new("http://localhost:7073/");
    private const long Stake = 5_000;
    private const long Pot = 2 * Stake;

    private SelfCustodyWallet _challenger = null!;
    private SelfCustodyWallet _defender = null!;
    private readonly List<string> _dbPaths = [];

    public async Task InitializeAsync()
    {
        await RegtestHelper.WaitForArkdReadyAsync(TimeSpan.FromSeconds(30));
        _challenger = await NewWalletAsync();
        _defender = await NewWalletAsync();
        await RegtestHelper.ArkSend(_challenger.Address, 50_000);
        await RegtestHelper.ArkSend(_defender.Address, 50_000);
        await _challenger.WaitForBalanceAsync(50_000, TimeSpan.FromSeconds(60));
        await _defender.WaitForBalanceAsync(50_000, TimeSpan.FromSeconds(60));
    }

    private async Task<SelfCustodyWallet> NewWalletAsync()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"ah-wager-{Guid.NewGuid():N}.db");
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

    [Fact]
    public async Task BothStakesSweepAtomicallyToTheWinner_CheatsRefused()
    {
        var transport = _challenger.GetService<global::NArk.Core.Transport.IClientTransport>();
        var serverInfo = await transport.GetServerInfoAsync();
        var emulatorInfo = await new EmulatorClient(EmulatorUri).GetInfoAsync();

        // Match open: the server commits to a seed BEFORE stakes are funded —
        // the commitment, the players, AND the oracle key are baked into the
        // escrow contract itself.
        var serverSeed = CommitReveal.NewSeed();
        var commitment = Convert.FromHexString(CommitReveal.Commit(serverSeed));

        var oracleKey = NBitcoin.Secp256k1.ECPrivKey.Create(
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        Span<byte> oraclePkSpan = stackalloc byte[32];
        oracleKey.CreateXOnlyPubKey().WriteToSpan(oraclePkSpan);
        var oraclePk = oraclePkSpan.ToArray();

        const string matchId = "e2e-covenant-match";
        var challengerPkScript = ArkAddress.Parse(_challenger.Address).ScriptPubKey;
        var defenderPkScript = ArkAddress.Parse(_defender.Address).ScriptPubKey;
        var refundAfter = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(); // refunds not in play here

        // Per-party escrows (coinflip shape): both carry both settle branches;
        // each carries a refund leaf paying only its own party.
        ArkadeContractFunction[] SettleBranches() =>
        [
            new("settleToChallenger",
                ArkadeCovenants.SettleAuthorized(
                    ArkadeCovenants.SettleMessage(matchId, challengerWon: true), oraclePk,
                    commitment, challengerPkScript, Pot, Stake)),
            new("settleToDefender",
                ArkadeCovenants.SettleAuthorized(
                    ArkadeCovenants.SettleMessage(matchId, challengerWon: false), oraclePk,
                    commitment, defenderPkScript, Pot, Stake)),
        ];
        var challengerEscrow = new ArkadeArtifactContract(
            "escrow-challenger", serverInfo.SignerKey, emulatorInfo.SignerPubkey,
            [.. SettleBranches(), new("refund", ArkadeCovenants.RefundTo(challengerPkScript, Stake), new LockTime((uint)refundAfter))]);
        var defenderEscrow = new ArkadeArtifactContract(
            "escrow-defender", serverInfo.SignerKey, emulatorInfo.SignerPubkey,
            [.. SettleBranches(), new("refund", ArkadeCovenants.RefundTo(defenderPkScript, Stake), new LockTime((uint)refundAfter))]);
        var isMain = serverInfo.Network == Network.Main;

        // Each player stakes from their OWN wallet into THEIR OWN escrow.
        await _challenger.SendAsync(challengerEscrow.GetArkAddress().ToString(isMain), Stake);
        await _defender.SendAsync(defenderEscrow.GetArkAddress().ToString(isMain), Stake);
        var stakes = new[]
        {
            await CovenantSpender.WaitForVtxoAsync(_challenger, challengerEscrow, TimeSpan.FromSeconds(45)),
            await CovenantSpender.WaitForVtxoAsync(_challenger, defenderEscrow, TimeSpan.FromSeconds(45)),
        };
        var escrows = new[] { challengerEscrow, defenderEscrow };

        // The oracle (game key) signs ONLY the true outcome's branch message.
        byte[] SignSettle(NBitcoin.Secp256k1.ECPrivKey key, bool challengerWon)
        {
            var signature = key.SignBIP340(ArkadeCovenants.SettleMessage(matchId, challengerWon));
            var bytes = new byte[64];
            signature.WriteToSpan(bytes);
            return bytes;
        }
        var honestSig = SignSettle(oracleKey, challengerWon: true);

        // Witness: [outputIndex, otherInputIndex, serverSeed, oracleSig] — sig on top.
        CovenantSpender.CovenantInput[] SettleInputs(byte[] seed, byte[] oracleSig) =>
        [
            new(escrows[0], "settleToChallenger",
                [ArkadeCovenants.EncodeIndex(0), ArkadeCovenants.EncodeIndex(1), seed, oracleSig], stakes[0]),
            new(escrows[1], "settleToChallenger",
                [ArkadeCovenants.EncodeIndex(0), ArkadeCovenants.EncodeIndex(0), seed, oracleSig], stakes[1]),
        ];

        // 1. FORGED oracle signature (an attacker's key) — refused.
        var forgerKey = NBitcoin.Secp256k1.ECPrivKey.Create(
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var forged = await Assert.ThrowsAnyAsync<Exception>(() => CovenantSpender.SpendManyAsync(
            _challenger, EmulatorUri, SettleInputs(serverSeed, SignSettle(forgerKey, true)),
            [new TxOut(Money.Satoshis(Pot), challengerPkScript)]));
        Assert.Contains("Emulator rejected", forged.Message);

        // 2. CROSS-BRANCH replay: the oracle's signature for the DEFENDER
        //    branch cannot authorize the challenger branch.
        var crossBranch = await Assert.ThrowsAnyAsync<Exception>(() => CovenantSpender.SpendManyAsync(
            _challenger, EmulatorUri, SettleInputs(serverSeed, SignSettle(oracleKey, challengerWon: false)),
            [new TxOut(Money.Satoshis(Pot), challengerPkScript)]));
        Assert.Contains("Emulator rejected", crossBranch.Message);

        // 3. Wrong seed — the commit gate fails even with a valid oracle sig.
        var wrongSeed = await Assert.ThrowsAnyAsync<Exception>(() => CovenantSpender.SpendManyAsync(
            _challenger, EmulatorUri, SettleInputs(CommitReveal.NewSeed(), honestSig),
            [new TxOut(Money.Satoshis(Pot), challengerPkScript)]));
        Assert.Contains("Emulator rejected", wrongSeed.Message);

        // 4. Short pot — pays the winner less than the pot, siphoning the rest.
        var shortPot = await Assert.ThrowsAnyAsync<Exception>(() => CovenantSpender.SpendManyAsync(
            _challenger, EmulatorUri, SettleInputs(serverSeed, honestSig),
            [
                new TxOut(Money.Satoshis(Pot - 2_000), challengerPkScript),
                new TxOut(Money.Satoshis(2_000), defenderPkScript),
            ]));
        Assert.Contains("Emulator rejected", shortPot.Message);

        // 5. Honest settle: oracle-authorized branch, revealed seed, full pot.
        var balanceBefore = await _challenger.GetBalanceSatsAsync();
        var response = await CovenantSpender.SpendManyAsync(
            _challenger, EmulatorUri, SettleInputs(serverSeed, honestSig),
            [new TxOut(Money.Satoshis(Pot), challengerPkScript)]);
        Assert.False(string.IsNullOrEmpty(response.SignedArkTx));
        Assert.Equal(2, response.SignedCheckpointTxs.Length);

        await _challenger.WaitForBalanceAsync(balanceBefore + Pot, TimeSpan.FromSeconds(45));
    }

    [Fact]
    public async Task AbandonedStakeIsRefundableAfterExpiry_WithoutTheServer()
    {
        var transport = _challenger.GetService<global::NArk.Core.Transport.IClientTransport>();
        var serverInfo = await transport.GetServerInfoAsync();
        var emulatorInfo = await new EmulatorClient(EmulatorUri).GetInfoAsync();

        // A match nobody ever accepted: the challenger staked, the defender
        // vanished. The refund leaf lets the challenger reclaim after expiry
        // with NO oracle, NO defender, NO game server — pure covenant.
        var challengerPkScript = ArkAddress.Parse(_challenger.Address).ScriptPubKey;
        var refundAfter = DateTimeOffset.UtcNow.AddSeconds(8).ToUnixTimeSeconds();

        var escrow = new ArkadeArtifactContract(
            "escrow-refund-probe", serverInfo.SignerKey, emulatorInfo.SignerPubkey,
            [new ArkadeContractFunction("refund",
                ArkadeCovenants.RefundTo(challengerPkScript, Stake), new LockTime((uint)refundAfter))]);
        var isMain = serverInfo.Network == Network.Main;

        await _challenger.SendAsync(escrow.GetArkAddress().ToString(isMain), Stake);
        var stake = await CovenantSpender.WaitForVtxoAsync(_challenger, escrow, TimeSpan.FromSeconds(45));

        CovenantSpender.CovenantInput[] RefundInputs(long txLockTime) =>
        [
            new(escrow, "refund", [ArkadeCovenants.EncodeIndex(0)], stake,
                LockTime: new LockTime((uint)txLockTime)),
        ];
        TxOut[] refundOutputs = [new TxOut(Money.Satoshis(Stake), challengerPkScript)];

        // Before expiry: refused by arkd's forfeit-closure gate (the leaf's
        // CLTV is judged against chain blocktime). The probe deliberately uses
        // a NON-canonical tx locktime (expiry+1): arkd records a failure event
        // under the submitted txid, permanently poisoning that txid's event
        // stream on this arkd version — a disposable probe txid keeps the
        // canonical refund txid clean for the real claim below.
        var early = await Assert.ThrowsAnyAsync<Exception>(() => CovenantSpender.SpendManyAsync(
            _challenger, EmulatorUri, RefundInputs(refundAfter + 1), refundOutputs));
        Assert.False(early is TimeoutException, $"expected a refusal, got: {early.Message}");

        // Wait until the CHAIN's clock (not the wall clock) passes expiry,
        // then submit the canonical refund exactly ONCE. Never submit early
        // and retry: each refusal would poison the canonical txid forever.
        await RegtestHelper.WaitForChainTimeAsync(refundAfter, TimeSpan.FromSeconds(120));
        var balanceBefore = await _challenger.GetBalanceSatsAsync();
        var refund = await CovenantSpender.SpendManyAsync(
            _challenger, EmulatorUri, RefundInputs(refundAfter), refundOutputs);
        Assert.False(string.IsNullOrEmpty(refund.SignedArkTx));

        await _challenger.WaitForBalanceAsync(balanceBefore + Stake, TimeSpan.FromSeconds(90));
    }
}
