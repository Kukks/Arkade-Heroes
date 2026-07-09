using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NArk.Core.Services;

namespace ArkadeHeroes.Web.Wallet;

/// <summary>
/// Manually starts the NArk SDK background services in Blazor WASM (which has no IHostedService).
/// Mirrors the NArk sample wallet's ArkServiceStartup, minus swaps (the game wallet doesn't use Boltz).
/// </summary>
public static class ArkServiceStartup
{
    public static async Task StartArkServicesAsync(this IServiceProvider services)
    {
        var cts = new CancellationTokenSource();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("ArkServiceStartup");

        await services.GetRequiredService<SweeperService>().StartAsync(cts.Token);
        await services.GetRequiredService<BatchManagementService>().StartAsync(cts.Token);
        await services.GetRequiredService<IntentSynchronizationService>().StartAsync(cts.Token);
        await services.GetRequiredService<IntentGenerationService>().StartAsync(cts.Token);

        try
        {
            await services.GetRequiredService<VtxoSynchronizationService>().StartAsync(cts.Token);
        }
        catch (Exception ex) { logger.LogWarning(ex, "VtxoSynchronizationService failed to start — falling back to polling"); }
    }
}
