using System.Text;
using ArkadeHeroes.Chain.NArk;

namespace ArkadeHeroes.Tests.E2E;

/// <summary>
/// Opt-in wallet encryption end-to-end against a real operator: a wallet created
/// with a passphrase stores NO cleartext mnemonic on disk, reopens only with the
/// same passphrase (wrong/absent passphrase is refused), and a passphrase-less
/// wallet stays plaintext (today's non-interactive default). Uses a real
/// operator only because SelfCustodyWallet.CreateAsync needs server info; no
/// funding is required.
/// </summary>
public class WalletEncryptionE2ETests : IAsyncLifetime
{
    // A valid BIP-39 test-vector mnemonic (from the spec) — deterministic so the
    // cleartext scan looks for an exact string.
    private const string Mnemonic = "legal winner thank year wave sausage worth useful legal winner thank yellow";
    private const string Passphrase = "a-strong-passphrase-123";

    private readonly List<string> _dbPaths = [];

    public async Task InitializeAsync() => await RegtestHelper.WaitForArkdReadyAsync(TimeSpan.FromSeconds(30));

    public Task DisposeAsync()
    {
        foreach (var p in _dbPaths)
            foreach (var f in RelatedFiles(p))
                try { if (File.Exists(f)) File.Delete(f); } catch { /* windows lock */ }
        return Task.CompletedTask;
    }

    private string NewDbPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ah-enc-{Guid.NewGuid():N}.db");
        _dbPaths.Add(path);
        return path;
    }

    private static IEnumerable<string> RelatedFiles(string dbPath) =>
        [dbPath, dbPath + "-wal", dbPath + "-shm", dbPath + "-journal"];

    private static Task<SelfCustodyWallet> OpenAsync(string dbPath, string? passphrase, string? mnemonic = null) =>
        SelfCustodyWallet.CreateAsync(new SelfCustodyWalletOptions
        {
            ArkUri = "http://localhost:7070",
            DbPath = dbPath,
            Mnemonic = mnemonic,
            Passphrase = passphrase,
        });

    private static bool AnyFileContains(string dbPath, string needle)
    {
        // Release the SQLite connection pool so the file handle is free to read.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in RelatedFiles(dbPath))
            if (File.Exists(f) && Encoding.Latin1.GetString(File.ReadAllBytes(f)).Contains(needle, StringComparison.Ordinal))
                return true;
        return false;
    }

    [Fact]
    public async Task EncryptedWallet_NoCleartextOnDisk_ReopensOnlyWithPassphrase()
    {
        var dbPath = NewDbPath();

        // Create with a passphrase and a known mnemonic.
        string address;
        await using (var wallet = await OpenAsync(dbPath, Passphrase, Mnemonic))
        {
            Assert.Equal(Mnemonic, wallet.Mnemonic);
            address = wallet.Address;
        }

        // The mnemonic is NOWHERE in the wallet DB (or its WAL/journal sidecars);
        // the ciphertext marker IS present.
        Assert.False(AnyFileContains(dbPath, Mnemonic), "the mnemonic must not be stored in cleartext");
        Assert.False(AnyFileContains(dbPath, "sausage"), "no mnemonic word may leak");
        Assert.True(AnyFileContains(dbPath, "enc:v1:"), "the encrypted-secret marker should be on disk");

        // Reopens with the SAME passphrase — same identity, same mnemonic.
        await using (var reopened = await OpenAsync(dbPath, Passphrase))
        {
            Assert.Equal(address, reopened.Address);
            Assert.Equal(Mnemonic, reopened.Mnemonic);
        }

        // Wrong passphrase is refused (authenticated decryption fails).
        await Assert.ThrowsAnyAsync<Exception>(() => OpenAsync(dbPath, "wrong-passphrase"));

        // No passphrase is refused — the encrypted secret can't be misused.
        await Assert.ThrowsAsync<InvalidOperationException>(() => OpenAsync(dbPath, passphrase: null));
    }

    [Fact]
    public async Task PasswordlessWallet_StaysPlaintext_TodaysDefault()
    {
        var dbPath = NewDbPath();
        string address;
        await using (var wallet = await OpenAsync(dbPath, passphrase: null, mnemonic: Mnemonic))
            address = wallet.Address;

        // Without a passphrase the mnemonic is stored in cleartext (unchanged
        // behaviour the E2E suite relies on) and reopens with no passphrase.
        Assert.True(AnyFileContains(dbPath, Mnemonic), "passwordless wallets remain plaintext today");
        await using var reopened = await OpenAsync(dbPath, passphrase: null);
        Assert.Equal(address, reopened.Address);
        Assert.Equal(Mnemonic, reopened.Mnemonic);
    }
}
