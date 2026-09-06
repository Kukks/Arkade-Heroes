using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NArk.Abstractions.Assets;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Safety;
using NArk.Abstractions.Wallets;
using NArk.Blockchain;
using NArk.Safety.AsyncKeyedLock;
using NArk.Core.Services;
using NArk.Core.Wallet;
using NArk.Hosting;
using NArk.Storage.EfCore.Hosting;

namespace ArkadeHeroes.Chain.NArk;

public static class NArkChainRegistration
{
    /// <summary>
    /// Registers the full NArk stack (transport, storage, core services) plus
    /// the game's <see cref="NArkChainService"/>. Mirrors the SDK sample
    /// wallet's composition, with SQLite persistence.
    /// </summary>
    public static IServiceCollection AddNArkChain(this IServiceCollection services, NArkChainOptions options)
    {
        services.AddSingleton(options);

        services.AddDbContextFactory<GameArkDbContext>(builder =>
            builder.UseSqlite($"Data Source={options.DbPath}"));
        services.AddArkEfCoreStorage<GameArkDbContext>();

        services.AddArkCoreServices();
        services.AddArkNetwork(new ArkNetworkConfig(options.ArkUri, EsploraUri: options.EsploraUri));

        services.AddArkadeRenewalScheduling();
        services.AddSingleton<ISafetyService, AsyncSafetyService>();
        services.AddSingleton<IWalletProvider, DefaultWalletProvider>();
        services.AddSingleton<IAssetManager, AssetManager>();
        services.AddSingleton<IBitcoinBlockchain>(_ =>
            new EsploraBlockchain(new Uri(options.EsploraUri.TrimEnd('/') + "/")));

        services.AddSingleton<NArkChainService>();
        services.AddSingleton<IChainService>(sp => sp.GetRequiredService<NArkChainService>());

        // Schema must exist before ArkHostedLifecycle starts the sync services:
        // StartingAsync runs before every hosted service's StartAsync.
        services.AddHostedService<GameChainDbInitializer>();

        return services;
    }

    private sealed class GameChainDbInitializer(IDbContextFactory<GameArkDbContext> dbFactory)
        : IHostedLifecycleService
    {
        public async Task StartingAsync(CancellationToken cancellationToken)
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            await db.Database.EnsureCreatedAsync(cancellationToken);
        }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
