using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace ArkadeHeroes.Chain;

/// <summary>
/// Chain simulation for unit tests and offline development: same contract as
/// the NArk-backed service, no infrastructure. Every player starts with a
/// faucet balance; asset ids are fake but unique.
/// </summary>
public class InMemoryChainService : IChainService
{
    public const long FaucetSats = 100_000;

    private readonly ConcurrentDictionary<string, PlayerWallet> _wallets = new();
    private readonly ConcurrentDictionary<string, long> _balances = new();
    private readonly ConcurrentDictionary<string, string> _assetHolders = new(); // assetId → playerId
    private readonly ConcurrentDictionary<string, string> _itemAssets = new(); // itemId → assetId
    private readonly ConcurrentDictionary<(string PlayerId, string ItemId), ulong> _itemHoldings = new();
    private long _treasuryBalance;

    private static string NewId(string prefix)
        => $"{prefix}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant()}";

    public Task<ChainInfo> GetInfoAsync(CancellationToken ct = default)
        => Task.FromResult(new ChainInfo("InMemory", "simnet", "sim-treasury", "sim-species-asset"));

    public Task<PlayerWallet> GetOrCreatePlayerWalletAsync(string playerId, CancellationToken ct = default)
    {
        var wallet = _wallets.GetOrAdd(playerId, id =>
        {
            _balances[id] = FaucetSats;
            return new PlayerWallet(id, NewId("sim-ark1"));
        });
        return Task.FromResult(wallet);
    }

    public Task<long> GetBalanceSatsAsync(string playerId, CancellationToken ct = default)
        => Task.FromResult(_balances.GetValueOrDefault(playerId));

    public Task<HeroMintResult> MintHeroAssetAsync(string playerId, HeroMintData data, CancellationToken ct = default)
    {
        if (!_wallets.ContainsKey(playerId))
            throw new InvalidOperationException($"Player {playerId} has no wallet.");
        var assetId = NewId("sim-asset");
        _assetHolders[assetId] = playerId;
        return Task.FromResult(new HeroMintResult(assetId, NewId("sim-arktx")));
    }

    public Task<string> PayFeeAsync(string playerId, long amountSats, string memo, CancellationToken ct = default)
    {
        if (amountSats < 0) throw new ArgumentOutOfRangeException(nameof(amountSats));
        var paid = false;
        _balances.AddOrUpdate(playerId,
            _ => throw new InvalidOperationException($"Player {playerId} has no wallet."),
            (_, balance) =>
            {
                if (balance < amountSats) return balance;
                paid = true;
                return balance - amountSats;
            });
        if (!paid)
            throw new InvalidOperationException(
                $"Insufficient balance for fee of {amountSats} sats ({memo}).");
        Interlocked.Add(ref _treasuryBalance, amountSats);
        return Task.FromResult(NewId("sim-payment"));
    }

    public Task<string> PayoutAsync(string playerId, long amountSats, string memo, CancellationToken ct = default)
    {
        if (amountSats < 0) throw new ArgumentOutOfRangeException(nameof(amountSats));
        if (!_balances.ContainsKey(playerId))
            throw new InvalidOperationException($"Player {playerId} has no wallet.");
        if (Interlocked.Add(ref _treasuryBalance, -amountSats) < 0)
        {
            Interlocked.Add(ref _treasuryBalance, amountSats);
            throw new InvalidOperationException($"Treasury cannot cover payout of {amountSats} sats ({memo}).");
        }
        _balances.AddOrUpdate(playerId, amountSats, (_, balance) => balance + amountSats);
        return Task.FromResult(NewId("sim-payout"));
    }

    public Task<string> TransferHeroAssetAsync(string fromPlayerId, string toPlayerId, string assetId, CancellationToken ct = default)
    {
        if (!_wallets.ContainsKey(toPlayerId))
            throw new InvalidOperationException($"Player {toPlayerId} has no wallet.");
        var moved = _assetHolders.TryUpdate(assetId, toPlayerId, fromPlayerId);
        if (!moved)
            throw new InvalidOperationException($"Asset {assetId} is not held by {fromPlayerId}.");
        return Task.FromResult(NewId("sim-arktx"));
    }

    public Task<bool> VerifyHeroOwnershipAsync(string playerId, string assetId, CancellationToken ct = default)
        => Task.FromResult(_assetHolders.TryGetValue(assetId, out var holder) && holder == playerId);

    public Task<ItemDeliveryResult> DeliverItemAssetAsync(string playerId, string itemId, string itemName, CancellationToken ct = default)
    {
        if (!_wallets.ContainsKey(playerId))
            throw new InvalidOperationException($"Player {playerId} has no wallet.");
        var assetId = _itemAssets.GetOrAdd(itemId, _ => NewId("sim-item"));
        _itemHoldings.AddOrUpdate((playerId, itemId), 1UL, (_, count) => count + 1);
        return Task.FromResult(new ItemDeliveryResult(assetId, NewId("sim-arktx")));
    }

    public Task<ulong> GetItemAssetBalanceAsync(string playerId, string itemId, CancellationToken ct = default)
        => Task.FromResult(_itemHoldings.GetValueOrDefault((playerId, itemId)));
}
