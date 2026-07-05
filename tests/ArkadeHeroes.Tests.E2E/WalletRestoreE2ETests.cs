using ArkadeHeroes.Chain.NArk;

namespace ArkadeHeroes.Tests.E2E;

/// <summary>
/// The non-custodial recovery guarantee, live: a wallet backed up as its mnemonic
/// is fully recoverable from those words ALONE. Restoring into a FRESH data dir
/// re-derives the SAME address and recovers the on-chain funds sitting there —
/// no server, no backup service, no original wallet file. This is what "you own
/// your heroes" means when the client machine is lost.
/// </summary>
public class WalletRestoreE2ETests : IAsyncLifetime
{
    private readonly List<string> _dbPaths = [];

    public async Task InitializeAsync() => await RegtestHelper.WaitForArkdReadyAsync(TimeSpan.FromSeconds(30));

    public Task DisposeAsync()
    {
        foreach (var p in _dbPaths)
            try { if (File.Exists(p)) File.Delete(p); } catch { /* windows lock */ }
        return Task.CompletedTask;
    }

    private async Task<SelfCustodyWallet> OpenAsync(string? mnemonic)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"ah-restore-{Guid.NewGuid():N}.db");
        _dbPaths.Add(dbPath);
        return await SelfCustodyWallet.CreateAsync(new SelfCustodyWalletOptions
        {
            ArkUri = "http://localhost:7070",
            DbPath = dbPath,
            Mnemonic = mnemonic,
        });
    }

    [Fact]
    public async Task Wallet_RestoresFromMnemonic_SameAddress_RecoversFunds()
    {
        // A player creates a wallet and writes down the 12 words.
        string mnemonic, address;
        await using (var original = await OpenAsync(mnemonic: null))
        {
            mnemonic = original.Mnemonic;
            address = original.Address;
        }

        // Funds land at the address (on-chain — the wallet needn't be open).
        await RegtestHelper.ArkSend(address, 40_000);

        // ...then the machine is lost. Restore from the words into a FRESH data dir.
        await using var restored = await OpenAsync(mnemonic);

        // The same words re-derive the same identity, and the funds are recovered.
        Assert.Equal(mnemonic, restored.Mnemonic);
        Assert.Equal(address, restored.Address);
        await restored.WaitForBalanceAsync(40_000, TimeSpan.FromSeconds(60));
    }
}
