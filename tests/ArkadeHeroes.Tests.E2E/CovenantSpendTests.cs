using ArkadeHeroes.Chain.Covenants;
using ArkadeHeroes.Chain.NArk;

namespace ArkadeHeroes.Tests.E2E;

/// <summary>
/// The first REAL covenant enforcement on regtest: a VTXO spendable only via
/// an emulator-co-signed leaf. A passing Arkade Script gets the emulator's
/// signature (the spend completes); a failing script is REFUSED — proof the
/// covenant is enforced by script execution, not by anyone's goodwill.
/// Requires the regtest stack with the emulator profile.
/// </summary>
public class CovenantSpendTests : IAsyncLifetime
{
    private static readonly Uri EmulatorUri = new("http://localhost:7073/");

    private SelfCustodyWallet _funder = null!;
    private string _walletDbPath = null!;

    public async Task InitializeAsync()
    {
        await RegtestHelper.WaitForArkdReadyAsync(TimeSpan.FromSeconds(30));

        _walletDbPath = Path.Combine(Path.GetTempPath(), $"ah-covenant-{Guid.NewGuid():N}.db");
        _funder = await SelfCustodyWallet.CreateAsync(new SelfCustodyWalletOptions
        {
            ArkUri = "http://localhost:7070",
            DbPath = _walletDbPath,
        });

        await RegtestHelper.ArkSend(_funder.Address, 100_000);
        await _funder.WaitForBalanceAsync(100_000, TimeSpan.FromSeconds(60));
    }

    public async Task DisposeAsync()
    {
        await _funder.DisposeAsync();
        try { if (File.Exists(_walletDbPath)) File.Delete(_walletDbPath); } catch { /* windows lock */ }
    }

    [Fact]
    public async Task EmulatorCoSignsAPassingCovenantSpend()
    {
        // OP_TRUE — the minimal passing Arkade Script.
        var result = await CovenantProbe.RunAsync(
            _funder, EmulatorUri, arkadeScript: [0x51], scriptWitness: []);

        Assert.False(string.IsNullOrEmpty(result.SignedArkTx),
            "emulator should return the co-signed Arkade transaction");
        Assert.True(result.SignedCheckpointCount >= 1,
            "emulator should return co-signed checkpoint transactions");

        // The covenant VTXO must actually be consumed: the wallet sees the
        // round-tripped funds again as a fresh VTXO at its own address.
        await _funder.WaitForBalanceAsync(100_000 - 1, TimeSpan.FromSeconds(45));
    }

    [Fact]
    public async Task EmulatorRefusesAFailingCovenantSpend()
    {
        // OP_FALSE (empty push) — the script evaluates false; the emulator
        // must refuse to sign. THIS is covenant enforcement.
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => CovenantProbe.RunAsync(
            _funder, EmulatorUri, arkadeScript: [0x00], scriptWitness: []));

        Assert.Contains("Emulator rejected", ex.Message);
    }
}
