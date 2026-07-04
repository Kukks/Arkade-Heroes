using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace ArkadeHeroes.Chain;

/// <summary>
/// Chain simulation for unit tests and offline development, honoring the same
/// non-custodial semantics as the NArk-backed service: players are addresses,
/// fees are invoices the "client side" must explicitly pay (via
/// <see cref="PayInvoiceFromPlayer"/> — a stand-in for the player's own wallet),
/// and asset holdings live per address. Nothing is auto-paid.
/// </summary>
public class InMemoryChainService : IChainService
{
    /// <summary>Simulated starting balance of every player wallet (the "client side" of the simulation).</summary>
    public const long FaucetSats = 100_000;

    private sealed record Invoice(string PayToAddress, long AmountSats, string Memo)
    {
        public long PaidSats;
    }

    private readonly ConcurrentDictionary<string, string> _playerAddresses = new();   // playerId → address
    private readonly ConcurrentDictionary<string, string> _addressOwners = new();     // address → playerId
    private readonly ConcurrentDictionary<string, long> _playerBalances = new();      // playerId → sats (simulated client wallet)
    private readonly ConcurrentDictionary<string, string> _assetHolders = new();      // hero assetId → playerId
    private readonly ConcurrentDictionary<string, string> _itemAssets = new();        // itemId → assetId
    private readonly ConcurrentDictionary<(string PlayerId, string ItemId), ulong> _itemHoldings = new();
    private readonly ConcurrentDictionary<string, Invoice> _invoices = new();
    private long _treasuryBalance;

    private static string NewId(string prefix)
        => $"{prefix}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant()}";

    public Task<ChainInfo> GetInfoAsync(CancellationToken ct = default)
        => Task.FromResult(new ChainInfo("InMemory", "simnet", "sim-treasury", "sim-species-asset"));

    // ── Players = addresses ────────────────────────────────────────────

    public Task RegisterPlayerAddressAsync(string playerId, string arkadeAddress, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(arkadeAddress))
            throw new InvalidOperationException("An Arkade address is required.");
        if (_addressOwners.TryGetValue(arkadeAddress, out var existing) && existing != playerId)
            throw new InvalidOperationException("Address already registered to another player.");
        _playerAddresses[playerId] = arkadeAddress;
        _addressOwners[arkadeAddress] = playerId;
        _playerBalances.TryAdd(playerId, FaucetSats);
        return Task.CompletedTask;
    }

    public Task<string> GetPlayerAddressAsync(string playerId, CancellationToken ct = default)
        => _playerAddresses.TryGetValue(playerId, out var address)
            ? Task.FromResult(address)
            : throw new InvalidOperationException($"Player {playerId} has no registered address.");

    public Task<long> GetAddressBalanceSatsAsync(string playerId, CancellationToken ct = default)
        => Task.FromResult(_playerBalances.GetValueOrDefault(playerId));

    // ── Fees ───────────────────────────────────────────────────────────

    public Task<FeeInvoice> CreateFeeInvoiceAsync(string memo, long amountSats, CancellationToken ct = default)
    {
        if (amountSats < 0) throw new ArgumentOutOfRangeException(nameof(amountSats));
        var invoiceId = NewId("sim-invoice");
        var address = NewId("sim-treasury-sub");
        _invoices[invoiceId] = new Invoice(address, amountSats, memo);
        return Task.FromResult(new FeeInvoice(invoiceId, address, amountSats, memo));
    }

    public Task<bool> IsInvoicePaidAsync(string invoiceId, CancellationToken ct = default)
        => Task.FromResult(_invoices.TryGetValue(invoiceId, out var invoice)
                           && invoice.PaidSats >= invoice.AmountSats);

    /// <summary>
    /// Simulation of the CLIENT wallet paying an invoice — the in-memory
    /// counterpart of a real player's own wallet sending to the invoice
    /// address. Deducts the player's simulated balance; the server-side code
    /// path (invoice verification) is identical to the real chain mode.
    /// </summary>
    public void PayInvoiceFromPlayer(string playerId, string invoiceId)
    {
        if (!_invoices.TryGetValue(invoiceId, out var invoice))
            throw new InvalidOperationException($"Unknown invoice {invoiceId}.");
        var paid = false;
        _playerBalances.AddOrUpdate(playerId,
            _ => throw new InvalidOperationException($"Player {playerId} has no wallet."),
            (_, balance) =>
            {
                if (balance < invoice.AmountSats) return balance;
                paid = true;
                return balance - invoice.AmountSats;
            });
        if (!paid)
            throw new InvalidOperationException(
                $"Insufficient simulated balance for invoice of {invoice.AmountSats} sats ({invoice.Memo}).");
        Interlocked.Add(ref invoice.PaidSats, invoice.AmountSats);
        Interlocked.Add(ref _treasuryBalance, invoice.AmountSats);
    }

    // ── Treasury-signed actions ────────────────────────────────────────

    public async Task<HeroMintResult> MintHeroAssetAsync(string toPlayerId, HeroMintData data, CancellationToken ct = default)
    {
        await GetPlayerAddressAsync(toPlayerId, ct); // must be registered
        var assetId = NewId("sim-asset");
        _assetHolders[assetId] = toPlayerId;
        return new HeroMintResult(assetId, NewId("sim-arktx"));
    }

    public async Task<ItemDeliveryResult> DeliverItemAssetAsync(string toPlayerId, string itemId, string itemName, CancellationToken ct = default)
    {
        await GetPlayerAddressAsync(toPlayerId, ct);
        var assetId = _itemAssets.GetOrAdd(itemId, _ => NewId("sim-item"));
        _itemHoldings.AddOrUpdate((toPlayerId, itemId), 1UL, (_, count) => count + 1);
        return new ItemDeliveryResult(assetId, NewId("sim-arktx"));
    }

    private readonly ConcurrentDictionary<string, ulong> _xpHoldings = new(); // playerId → XP units

    public async Task<ulong> DeliverXpAsync(string toPlayerId, ulong amount, CancellationToken ct = default)
    {
        await GetPlayerAddressAsync(toPlayerId, ct);
        return _xpHoldings.AddOrUpdate(toPlayerId, amount, (_, held) => held + amount);
    }

    public Task<ulong> GetXpBalanceAsync(string playerId, CancellationToken ct = default)
        => Task.FromResult(_xpHoldings.GetValueOrDefault(playerId));

    public async Task<string> PayoutAsync(string toPlayerId, long amountSats, string memo, CancellationToken ct = default)
    {
        if (amountSats < 0) throw new ArgumentOutOfRangeException(nameof(amountSats));
        await GetPlayerAddressAsync(toPlayerId, ct);
        if (Interlocked.Add(ref _treasuryBalance, -amountSats) < 0)
        {
            Interlocked.Add(ref _treasuryBalance, amountSats);
            throw new InvalidOperationException($"Treasury cannot cover payout of {amountSats} sats ({memo}).");
        }
        _playerBalances.AddOrUpdate(toPlayerId, amountSats, (_, balance) => balance + amountSats);
        return NewId("sim-payout");
    }

    /// <summary>
    /// Simulation of the CLIENT wallet moving a hero asset to another address —
    /// the in-memory stand-in for a real player-signed asset spend.
    /// </summary>
    public void TransferAssetFromPlayer(string fromPlayerId, string toPlayerId, string assetId)
    {
        if (!_playerAddresses.ContainsKey(toPlayerId))
            throw new InvalidOperationException($"Player {toPlayerId} has no registered address.");
        if (!_assetHolders.TryUpdate(assetId, toPlayerId, fromPlayerId))
            throw new InvalidOperationException($"Asset {assetId} is not held by {fromPlayerId}.");
    }

    // ── Covenant wager escrows (simulated) ─────────────────────────────

    private sealed record Escrow(
        string ChallengerId, string DefenderId, long StakeSats, string OraclePkHex,
        string CommitmentHex, long RefundAfterUnixSeconds)
    {
        public bool ChallengerStaked;
        public bool DefenderStaked;
        public bool Settled;
    }

    private readonly ConcurrentDictionary<string, Escrow> _escrows = new();

    public async Task<WagerEscrowInfo> CreateWagerEscrowAsync(
        string matchId, string challengerPlayerId, string defenderPlayerId,
        long stakeSats, byte[] seedCommitment32, string oraclePubKeyHex,
        long refundAfterUnixSeconds, CancellationToken ct = default)
    {
        await GetPlayerAddressAsync(challengerPlayerId, ct);
        await GetPlayerAddressAsync(defenderPlayerId, ct);
        _escrows[matchId] = new Escrow(challengerPlayerId, defenderPlayerId, stakeSats, oraclePubKeyHex,
            Convert.ToHexString(seedCommitment32).ToLowerInvariant(), refundAfterUnixSeconds);
        return new WagerEscrowInfo(matchId,
            $"sim-escrow-{matchId}-challenger", $"sim-escrow-{matchId}-defender",
            stakeSats, stakeSats * 2, refundAfterUnixSeconds);
    }

    public Task<bool> IsEscrowFundedAsync(string matchId, CancellationToken ct = default)
        => Task.FromResult(_escrows.TryGetValue(matchId, out var escrow)
                           && escrow is { ChallengerStaked: true, DefenderStaked: true });

    public async Task<Covenants.WagerEscrowParams?> GetWagerEscrowParamsAsync(string matchId, CancellationToken ct = default)
    {
        if (!_escrows.TryGetValue(matchId, out var escrow)) return null;
        return new Covenants.WagerEscrowParams(
            escrow.CommitmentHex,
            await GetPlayerAddressAsync(escrow.ChallengerId, ct),
            await GetPlayerAddressAsync(escrow.DefenderId, ct),
            escrow.StakeSats,
            escrow.OraclePkHex,
            matchId,
            escrow.RefundAfterUnixSeconds);
    }

    /// <summary>
    /// Simulated timelocked refund (the InMemory stand-in for the client's
    /// covenant refund spend). Enforces the same rules the covenant + operator
    /// do: only a party, only their own staked amount, only after expiry
    /// (the FORFEIT_CLOSURE_LOCKED analogue), never after settlement, never twice.
    /// </summary>
    public void RefundEscrowFromPlayer(string playerId, string matchId)
    {
        if (!_escrows.TryGetValue(matchId, out var escrow))
            throw new InvalidOperationException($"Unknown escrow {matchId}.");
        var isChallenger = escrow.ChallengerId == playerId;
        var isDefender = escrow.DefenderId == playerId;
        if (!isChallenger && !isDefender)
            throw new InvalidOperationException("Not a party to this escrow.");
        if (escrow.Settled)
            throw new InvalidOperationException("Escrow already settled — nothing to refund.");
        if (!(isChallenger ? escrow.ChallengerStaked : escrow.DefenderStaked))
            throw new InvalidOperationException("Nothing staked to refund.");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (now < escrow.RefundAfterUnixSeconds)
            throw new InvalidOperationException(
                $"Refund locked until {escrow.RefundAfterUnixSeconds} (chain time {now}).");

        if (isChallenger) escrow.ChallengerStaked = false;
        else escrow.DefenderStaked = false;
        _playerBalances.AddOrUpdate(playerId, escrow.StakeSats, (_, b) => b + escrow.StakeSats);
    }

    /// <summary>Simulated client-wallet stake into the escrow (the InMemory stand-in for paying the escrow address).</summary>
    public void StakeEscrowFromPlayer(string playerId, string matchId)
    {
        if (!_escrows.TryGetValue(matchId, out var escrow))
            throw new InvalidOperationException($"Unknown escrow {matchId}.");
        var isChallenger = escrow.ChallengerId == playerId;
        var isDefender = escrow.DefenderId == playerId;
        if (!isChallenger && !isDefender)
            throw new InvalidOperationException("Not a party to this escrow.");

        var paid = false;
        _playerBalances.AddOrUpdate(playerId,
            _ => throw new InvalidOperationException($"Player {playerId} has no wallet."),
            (_, balance) =>
            {
                if (balance < escrow.StakeSats) return balance;
                paid = true;
                return balance - escrow.StakeSats;
            });
        if (!paid)
            throw new InvalidOperationException($"Insufficient simulated balance for the {escrow.StakeSats}-sat stake.");
        if (isChallenger) escrow.ChallengerStaked = true;
        else escrow.DefenderStaked = true;
    }

    public Task<string> SettleWagerEscrowAsync(
        string matchId, bool challengerWon, byte[] serverSeed, byte[] oracleSignature64,
        CancellationToken ct = default)
    {
        if (!_escrows.TryGetValue(matchId, out var escrow))
            throw new InvalidOperationException($"Unknown escrow {matchId}.");
        if (!escrow.ChallengerStaked || !escrow.DefenderStaked)
            throw new InvalidOperationException($"Escrow for {matchId} is not fully funded.");
        if (escrow.Settled)
            throw new InvalidOperationException("Escrow already settled.");

        // The simulation enforces the same oracle rule the covenant does:
        // a BIP340 signature over THIS branch's settle message.
        var message = Covenants.ArkadeCovenants.SettleMessage(matchId, challengerWon);
        if (!NBitcoin.Secp256k1.ECXOnlyPubKey.TryCreate(Convert.FromHexString(escrow.OraclePkHex), out var oraclePk)
            || oraclePk is null
            || !NBitcoin.Secp256k1.SecpSchnorrSignature.TryCreate(oracleSignature64, out var signature)
            || signature is null
            || !oraclePk.SigVerifyBIP340(signature, message))
            throw new InvalidOperationException("Oracle signature does not authorize this settle branch.");

        escrow.Settled = true;
        var winner = challengerWon ? escrow.ChallengerId : escrow.DefenderId;
        _playerBalances.AddOrUpdate(winner, escrow.StakeSats * 2, (_, b) => b + escrow.StakeSats * 2);
        return Task.FromResult(NewId("sim-covenant-settle"));
    }

    // ── Covenant breeding escrows (simulated) ──────────────────────────

    private sealed record BreedEscrow(
        string PlayerId, string ParentAId, string ParentBId, long FeeSats, string OraclePkHex, long RefundAfterUnixSeconds)
    {
        public bool Funded;
        public bool Executed;
    }

    private readonly ConcurrentDictionary<string, BreedEscrow> _breedEscrows = new();

    public async Task<BreedEscrowInfo> CreateBreedEscrowAsync(
        string breedingId, string playerId, string parentAAssetId, string parentBAssetId,
        long feeSats, string oraclePubKeyHex, long refundAfterUnixSeconds, CancellationToken ct = default)
    {
        await GetPlayerAddressAsync(playerId, ct);
        _breedEscrows[breedingId] = new BreedEscrow(
            playerId, parentAAssetId, parentBAssetId, feeSats, oraclePubKeyHex, refundAfterUnixSeconds);
        return new BreedEscrowInfo(breedingId, $"sim-breed-escrow-{breedingId}", feeSats, refundAfterUnixSeconds);
    }

    public Task<bool> IsBreedEscrowFundedAsync(string breedingId, CancellationToken ct = default)
        => Task.FromResult(_breedEscrows.TryGetValue(breedingId, out var e) && e.Funded);

    /// <summary>Simulated client-wallet deposit of both parents + fee into the breed escrow.</summary>
    public void FundBreedEscrowFromPlayer(string playerId, string breedingId)
    {
        if (!_breedEscrows.TryGetValue(breedingId, out var escrow))
            throw new InvalidOperationException($"Unknown breed escrow {breedingId}.");
        if (escrow.PlayerId != playerId)
            throw new InvalidOperationException("Not the breeding player.");
        if (_assetHolders.GetValueOrDefault(escrow.ParentAId) != playerId
            || _assetHolders.GetValueOrDefault(escrow.ParentBId) != playerId)
            throw new InvalidOperationException("The player does not hold both parents.");
        var paid = false;
        _playerBalances.AddOrUpdate(playerId, _ => throw new InvalidOperationException("No wallet."),
            (_, bal) => { if (bal < escrow.FeeSats) return bal; paid = true; return bal - escrow.FeeSats; });
        if (!paid) throw new InvalidOperationException($"Insufficient balance for the {escrow.FeeSats}-sat fee.");
        escrow.Funded = true;
    }

    public async Task<HeroMintResult> ExecuteBreedCovenantAsync(
        string breedingId, HeroMintData childData, byte[] oracleSignature64, CancellationToken ct = default)
    {
        if (!_breedEscrows.TryGetValue(breedingId, out var escrow))
            throw new InvalidOperationException($"Unknown breed escrow {breedingId}.");
        if (!escrow.Funded) throw new InvalidOperationException($"Breed escrow {breedingId} is not funded.");
        if (escrow.Executed) throw new InvalidOperationException("Breeding already executed.");

        // Enforce the same oracle rule the covenant does: a BIP340 signature
        // over the child's metadata Merkle root.
        var root = Covenants.ArkadeCovenants.MetadataMerkleRoot(Covenants.BreedEscrowContracts.ChildMetadata(
            childData.GenomeHex, childData.Generation, childData.ParentAId ?? "", childData.ParentBId ?? "",
            childData.ServerSeedHex ?? "", childData.PlayerNonce ?? ""));
        if (!NBitcoin.Secp256k1.ECXOnlyPubKey.TryCreate(Convert.FromHexString(escrow.OraclePkHex), out var oraclePk)
            || oraclePk is null
            || !NBitcoin.Secp256k1.SecpSchnorrSignature.TryCreate(oracleSignature64, out var signature)
            || signature is null
            || !oraclePk.SigVerifyBIP340(signature, root))
            throw new InvalidOperationException("Oracle signature does not authorize this breed.");

        escrow.Executed = true;
        // Parents retained (they stay with the player); child minted to player.
        var assetId = NewId("sim-asset");
        _assetHolders[assetId] = escrow.PlayerId;
        await Task.CompletedTask;
        return new HeroMintResult(assetId, NewId("sim-breed-covenant"));
    }

    public Task<Covenants.BreedEscrowParams?> GetBreedEscrowParamsAsync(string breedingId, CancellationToken ct = default)
    {
        if (!_breedEscrows.TryGetValue(breedingId, out var e)) return Task.FromResult<Covenants.BreedEscrowParams?>(null);
        return Task.FromResult<Covenants.BreedEscrowParams?>(new Covenants.BreedEscrowParams(
            $"sim-player-{e.PlayerId}", e.ParentAId, e.ParentBId, "sim-species",
            "sim-treasury", e.FeeSats, e.FeeSats + 660, e.OraclePkHex, breedingId, e.RefundAfterUnixSeconds));
    }

    // ── On-chain reads ─────────────────────────────────────────────────

    public Task<bool> VerifyHeroOwnershipAsync(string playerId, string assetId, CancellationToken ct = default)
        => Task.FromResult(_assetHolders.TryGetValue(assetId, out var holder) && holder == playerId);

    public Task<ulong> GetItemAssetBalanceAsync(string playerId, string itemId, CancellationToken ct = default)
        => Task.FromResult(_itemHoldings.GetValueOrDefault((playerId, itemId)));
}
