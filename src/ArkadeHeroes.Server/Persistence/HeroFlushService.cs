using Microsoft.Extensions.Options;

namespace ArkadeHeroes.Server.Persistence;

/// <summary>
/// The PROGRESSION half of hero durability: every <see cref="GameOptions.HeroFlushInterval"/> it persists
/// the heroes whose level/XP, equipment, cooldowns or breed count changed since the last pass, and drains
/// one final time on graceful shutdown (an operator bounce loses nothing; only a real crash pays the
/// bounded window). Identity events — mint, burn, transfer, rename — persist inline in GameService and
/// never wait for this loop. Registered ONLY when <c>Game:StateDbPath</c> is configured; the default
/// in-memory server runs no background churn at all.
/// </summary>
public sealed class HeroFlushService(
    GameStore store, IGameStatePersistence persistence, IOptions<GameOptions> options,
    ILogger<HeroFlushService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.HeroFlushInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await FlushDirtyHeroesAsync(stoppingToken);
        }
        catch (OperationCanceledException) { /* graceful stop — fall through to the last drain */ }
        await FlushDirtyHeroesAsync(CancellationToken.None);
    }

    /// <summary>One flush pass over the dirty set. Public so a test can force a deterministic flush
    /// instead of racing the timer.</summary>
    public async Task FlushDirtyHeroesAsync(CancellationToken ct = default)
    {
        foreach (var heroId in store.DrainDirtyHeroes())
        {
            // Marked, then burned before this pass ran: the burn already erased the durable row — skip,
            // or the save below would resurrect a hero whose on-chain asset is retired.
            if (!store.Heroes.TryGetValue(heroId, out var hero)) continue;
            try
            {
                await persistence.SaveHeroAsync(hero, ct);
                // Present at the read above but burned DURING the save: the burn's delete may have landed
                // between our read and our write, leaving the row re-inserted. Re-check and compensate so
                // the last durable word on a burned hero is always the delete.
                if (!store.Heroes.ContainsKey(heroId))
                    await persistence.DeleteHeroAsync(heroId, ct);
            }
            catch (OperationCanceledException)
            {
                store.MarkHeroDirty(heroId);   // shutdown mid-save — the final drain picks it up
                throw;
            }
            catch (Exception ex)
            {
                // A transient store fault must neither kill the host (a faulted BackgroundService stops
                // it) nor silently drop the change — re-mark and let the next tick retry. Identity saves
                // are unaffected: they run inline in the request, not here.
                store.MarkHeroDirty(heroId);
                logger.LogWarning(ex, "Hero progression flush failed for {HeroId}; re-marked for the next pass.", heroId);
            }
        }
    }
}
