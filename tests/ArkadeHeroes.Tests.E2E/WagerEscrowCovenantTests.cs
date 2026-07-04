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
        // the commitment is baked into the escrow contract itself.
        var serverSeed = CommitReveal.NewSeed();
        var commitment = Convert.FromHexString(CommitReveal.Commit(serverSeed));

        var challengerPkScript = ArkAddress.Parse(_challenger.Address).ScriptPubKey;
        var defenderPkScript = ArkAddress.Parse(_defender.Address).ScriptPubKey;

        var escrow = new ArkadeArtifactContract(
            "wager-escrow", serverInfo.SignerKey, emulatorInfo.SignerPubkey,
            [
                new ArkadeContractFunction("settleToChallenger",
                    ArkadeCovenants.SettleWithSeed(commitment, challengerPkScript, Pot, Stake)),
                new ArkadeContractFunction("settleToDefender",
                    ArkadeCovenants.SettleWithSeed(commitment, defenderPkScript, Pot, Stake)),
            ]);
        var escrowAddress = escrow.GetArkAddress().ToString(serverInfo.Network == Network.Main);

        // Each player stakes from their OWN wallet into the shared escrow.
        await _challenger.SendAsync(escrowAddress, Stake);
        await _defender.SendAsync(escrowAddress, Stake);
        var stakes = await CovenantSpender.WaitForVtxosAsync(_challenger, escrow, 2, TimeSpan.FromSeconds(45));

        // Say the challenger won the (replayable) battle. Per-entry witness:
        // [outputIndex, otherInputIndex, serverSeed] — seed on top.
        CovenantSpender.CovenantInput[] SettleInputs(byte[] seed) =>
        [
            new(escrow, "settleToChallenger",
                [ArkadeCovenants.EncodeIndex(0), ArkadeCovenants.EncodeIndex(1), seed], stakes[0]),
            new(escrow, "settleToChallenger",
                [ArkadeCovenants.EncodeIndex(0), ArkadeCovenants.EncodeIndex(0), seed], stakes[1]),
        ];

        // 1. Wrong seed — the commit gate fails, the emulator refuses.
        var wrongSeed = await Assert.ThrowsAnyAsync<Exception>(() => CovenantSpender.SpendManyAsync(
            _challenger, EmulatorUri, SettleInputs(CommitReveal.NewSeed()),
            [new TxOut(Money.Satoshis(Pot), challengerPkScript)]));
        Assert.Contains("Emulator rejected", wrongSeed.Message);

        // 2. Short pot — pays the winner less than the pot, siphoning the rest.
        var shortPot = await Assert.ThrowsAnyAsync<Exception>(() => CovenantSpender.SpendManyAsync(
            _challenger, EmulatorUri, SettleInputs(serverSeed),
            [
                new TxOut(Money.Satoshis(Pot - 2_000), challengerPkScript),
                new TxOut(Money.Satoshis(2_000), defenderPkScript),
            ]));
        Assert.Contains("Emulator rejected", shortPot.Message);

        // 3. Honest settle: reveal the committed seed, sweep both stakes to the winner.
        var balanceBefore = await _challenger.GetBalanceSatsAsync();
        var response = await CovenantSpender.SpendManyAsync(
            _challenger, EmulatorUri, SettleInputs(serverSeed),
            [new TxOut(Money.Satoshis(Pot), challengerPkScript)]);
        Assert.False(string.IsNullOrEmpty(response.SignedArkTx));
        Assert.Equal(2, response.SignedCheckpointTxs.Length);

        await _challenger.WaitForBalanceAsync(balanceBefore + Pot, TimeSpan.FromSeconds(45));
    }
}
