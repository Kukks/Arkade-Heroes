using ArkadeHeroes.Chain.NArk;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Wallets;
using NArk.Storage.EfCore.Hosting;

namespace ArkadeHeroes.Tests;

/// <summary>Issue #228 reproduced: re-saving a LIVE contract as Inactive — what a recycled-descriptor
/// renewal does — drops it from the scan set, so the wallet stops watching the player's REGISTERED payout
/// address. ⚠ ASSERTS THE DEFECT: when the SDK stops downgrading active contracts this goes RED, which
/// is the good news — delete it then, do not "repair" it green.</summary>
public class ContractDowngradeReachabilityTests : IAsyncLifetime
{
    private const string WalletId = "wallet-228";
    private const string Script = "5120aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ah-228-{Guid.NewGuid():N}.db");
    private ServiceProvider _provider = null!;
    private IContractStorage _storage = null!;

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextFactory<GameArkDbContext>(b => b.UseSqlite($"Data Source={_dbPath}"));
        services.AddArkEfCoreStorage<GameArkDbContext>();
        _provider = services.BuildServiceProvider();

        await using var db = await _provider.GetRequiredService<IDbContextFactory<GameArkDbContext>>()
            .CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        await _provider.GetRequiredService<IWalletStorage>().SaveWallet(
            new ArkWalletInfo(WalletId, null, null, WalletType.HD, null, 0));
        _storage = _provider.GetRequiredService<IContractStorage>();
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        // Same housekeeping contract as AuditLogTests.Cleanup: release this file's pool, then never let
        // a leftover temp file fail a run whose assertions already passed.
        SqliteTestDb.ReleasePool(_dbPath);
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static ArkContractEntity Contract(ContractActivityState state) => new(
        Script, state, "ark-payment", new Dictionary<string, string>(), WalletId, DateTimeOffset.UtcNow);

    private async Task<int> ScanSetSizeAsync() =>
        (await _storage.GetContracts(walletIds: [WalletId], isActive: true)).Count;

    [Fact]
    public async Task ReSavingALiveContractAsInactive_DropsItFromTheScanSet()
    {
        await _storage.SaveContract(Contract(ContractActivityState.Active));
        Assert.Equal(1, await ScanSetSizeAsync());

        await _storage.SaveContract(Contract(ContractActivityState.Inactive));

        Assert.Equal(0, await ScanSetSizeAsync());
    }
}
