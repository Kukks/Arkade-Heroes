using ArkadeHeroes.Chain.NArk;
using Microsoft.Extensions.DependencyInjection;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Intents;
using NBitcoin;

namespace ArkadeHeroes.Tests;

public class RenewalSchedulingCompositionTests
{
    [Fact]
    public async Task TheTreasuryWallet_GeneratesIntents_RatherThanThrowingOutOfItsFirstCycle()
    {
        Assert.Empty(await Scheduler(ServerServices()).GetIntentsToSubmit([]));
    }

    [Fact]
    public void TheTreasuryWallet_RenewsBehindTheFeeGuard()
    {
        Assert.IsType<BatchRenewalScheduler>(Scheduler(ServerServices()));
    }

    [Fact]
    public async Task TheConsoleWallet_GeneratesIntents_OnceItHasAChainSource()
    {
        var scheduler = Scheduler(ConsoleServices(Esplora));

        Assert.IsType<BatchRenewalScheduler>(scheduler);
        Assert.Empty(await scheduler.GetIntentsToSubmit([]));
    }

    [Fact]
    public void TheConsoleWallet_WithoutAChainSource_GainsNothingItDidNotHave()
    {
        // A chain source would also switch on the SDK's boarding-UTXO discovery, unasked for.
        var services = ConsoleServices(esploraUri: null).BuildServiceProvider();

        Assert.Null(services.GetService<IBitcoinBlockchain>());
    }

    private const string Esplora = "http://localhost:3000/api";

    // Chain time is answered locally and every other chain call throws, so an empty coin set that
    // reached the network would fail here rather than hang against a real node.
    private static IIntentScheduler Scheduler(IServiceCollection services)
    {
        services.AddSingleton<IBitcoinBlockchain>(new UnreachableChain());
        return services.BuildServiceProvider().GetRequiredService<IIntentScheduler>();
    }

    private static IServiceCollection ServerServices() =>
        new ServiceCollection().AddLogging().AddNArkChain(new NArkChainOptions { DbPath = TempDb() });

    private static IServiceCollection ConsoleServices(string? esploraUri) =>
        SelfCustodyWallet.ConfigureServices(
            new ServiceCollection(),
            new SelfCustodyWalletOptions { DbPath = TempDb(), EsploraUri = esploraUri });

    private static string TempDb() => Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    private sealed class UnreachableChain : IBitcoinBlockchain
    {
        public Task<TimeHeight> GetChainTime(CancellationToken cancellationToken = default) =>
            Task.FromResult(new TimeHeight(DateTimeOffset.UtcNow, 100));

        public Task<IReadOnlyList<BoardingUtxo>> GetUtxosAsync(string address, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> BroadcastAsync(Transaction tx, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> BroadcastPackageAsync(Transaction parent, Transaction child, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TxStatus> GetTxStatusAsync(uint256 txid, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<FeeRate> EstimateFeeRateAsync(int confirmTarget = 6, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
