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
public class ContractDowngradeReachabilityTests
{
    private const string WalletId = "wallet-228";
    private const string Script = "5120aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static async Task<IContractStorage> StorageAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextFactory<GameArkDbContext>(b =>
            b.UseSqlite($"Data Source={Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())}"));
        services.AddArkEfCoreStorage<GameArkDbContext>();
        var provider = services.BuildServiceProvider();

        await using var db = await provider.GetRequiredService<IDbContextFactory<GameArkDbContext>>()
            .CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        await provider.GetRequiredService<IWalletStorage>().SaveWallet(
            new ArkWalletInfo(WalletId, null, null, WalletType.HD, null, 0));
        return provider.GetRequiredService<IContractStorage>();
    }

    private static ArkContractEntity Contract(ContractActivityState state) => new(
        Script, state, "ark-payment", new Dictionary<string, string>(), WalletId, DateTimeOffset.UtcNow);

    private static async Task<int> ScanSetSizeAsync(IContractStorage storage) =>
        (await storage.GetContracts(walletIds: [WalletId], isActive: true)).Count;

    [Fact]
    public async Task ReSavingALiveContractAsInactive_DropsItFromTheScanSet()
    {
        var storage = await StorageAsync();
        await storage.SaveContract(Contract(ContractActivityState.Active));
        Assert.Equal(1, await ScanSetSizeAsync(storage));

        await storage.SaveContract(Contract(ContractActivityState.Inactive));

        Assert.Equal(0, await ScanSetSizeAsync(storage));
    }
}
