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

    public Task<FeeInvoice?> GetFeeInvoiceAsync(string invoiceId, CancellationToken ct = default)
        => Task.FromResult(_invoices.TryGetValue(invoiceId, out var invoice)
            ? new FeeInvoice(invoiceId, invoice.PayToAddress, invoice.AmountSats, invoice.Memo)
            : null);

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

    /// <summary>Dev/test lever: credit the simulated treasury directly (the daily faucet draws on it,
    /// and a fresh treasury has no fee income yet). InMemory only — NArk funds a real treasury address.</summary>
    public void FundTreasury(long sats) => Interlocked.Add(ref _treasuryBalance, sats);

    public Task<long> TreasuryBalanceAsync(CancellationToken ct = default) =>
        Task.FromResult(Interlocked.Read(ref _treasuryBalance));

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

    public Task<WagerEscrowFunding?> GetWagerEscrowFundingAsync(string matchId, CancellationToken ct = default)
        => Task.FromResult(_escrows.TryGetValue(matchId, out var escrow)
            ? new WagerEscrowFunding(escrow.ChallengerStaked, escrow.DefenderStaked) : null);

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

    /// <summary>Simulated timelocked breed refund: both parents were never moved out of the player's holdings, so this only returns the fee and clears the funded flag — gated after expiry, never after execution.</summary>
    public void RefundBreedEscrowFromPlayer(string playerId, string breedingId)
    {
        if (!_breedEscrows.TryGetValue(breedingId, out var escrow))
            throw new InvalidOperationException($"Unknown breed escrow {breedingId}.");
        if (escrow.PlayerId != playerId)
            throw new InvalidOperationException("Not the breeding player.");
        if (escrow.Executed)
            throw new InvalidOperationException("Breeding already executed — nothing to refund.");
        if (!escrow.Funded)
            throw new InvalidOperationException("Nothing deposited to refund.");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (now < escrow.RefundAfterUnixSeconds)
            throw new InvalidOperationException($"Refund locked until {escrow.RefundAfterUnixSeconds} (chain time {now}).");
        escrow.Funded = false;
        _playerBalances.AddOrUpdate(playerId, escrow.FeeSats, (_, b) => b + escrow.FeeSats);
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

    // ── Merge / fusion escrows (simulated) — inputs burned, fused minted ──

    private sealed record MergeEscrow(
        string PlayerId, string BaseAssetId, string SacrificeAssetId, long FeeSats, string OraclePkHex, long RefundAfterUnixSeconds)
    {
        public bool Funded;
        public bool Executed;
    }

    private readonly ConcurrentDictionary<string, MergeEscrow> _mergeEscrows = new();

    public async Task<string> CreateMergeEscrowAsync(
        string mergeId, string playerId, string baseAssetId, string sacrificeAssetId,
        long feeSats, string oraclePubKeyHex, long refundAfterUnixSeconds, CancellationToken ct = default)
    {
        await GetPlayerAddressAsync(playerId, ct);
        _mergeEscrows[mergeId] = new MergeEscrow(
            playerId, baseAssetId, sacrificeAssetId, feeSats, oraclePubKeyHex, refundAfterUnixSeconds);
        return $"sim-merge-escrow-{mergeId}";
    }

    public Task<bool> IsMergeEscrowFundedAsync(string mergeId, CancellationToken ct = default)
        => Task.FromResult(_mergeEscrows.TryGetValue(mergeId, out var e) && e.Funded);

    /// <summary>Simulated client-wallet deposit of base + sacrifice + fee into the merge escrow.</summary>
    public void FundMergeEscrowFromPlayer(string playerId, string mergeId)
    {
        if (!_mergeEscrows.TryGetValue(mergeId, out var escrow))
            throw new InvalidOperationException($"Unknown merge escrow {mergeId}.");
        if (escrow.PlayerId != playerId)
            throw new InvalidOperationException("Not the merging player.");
        if (_assetHolders.GetValueOrDefault(escrow.BaseAssetId) != playerId
            || _assetHolders.GetValueOrDefault(escrow.SacrificeAssetId) != playerId)
            throw new InvalidOperationException("The player does not hold both the base and the sacrifice.");
        var paid = false;
        _playerBalances.AddOrUpdate(playerId, _ => throw new InvalidOperationException("No wallet."),
            (_, bal) => { if (bal < escrow.FeeSats) return bal; paid = true; return bal - escrow.FeeSats; });
        if (!paid) throw new InvalidOperationException($"Insufficient balance for the {escrow.FeeSats}-sat fee.");
        escrow.Funded = true;
    }

    /// <summary>Simulated timelocked merge refund: base + sacrifice were never moved out of the player's holdings, so this only returns the fee and clears the funded flag — gated after expiry, never after execution.</summary>
    public void RefundMergeEscrowFromPlayer(string playerId, string mergeId)
    {
        if (!_mergeEscrows.TryGetValue(mergeId, out var escrow))
            throw new InvalidOperationException($"Unknown merge escrow {mergeId}.");
        if (escrow.PlayerId != playerId)
            throw new InvalidOperationException("Not the merging player.");
        if (escrow.Executed)
            throw new InvalidOperationException("Merge already executed — nothing to refund.");
        if (!escrow.Funded)
            throw new InvalidOperationException("Nothing deposited to refund.");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (now < escrow.RefundAfterUnixSeconds)
            throw new InvalidOperationException($"Refund locked until {escrow.RefundAfterUnixSeconds} (chain time {now}).");
        escrow.Funded = false;
        _playerBalances.AddOrUpdate(playerId, escrow.FeeSats, (_, b) => b + escrow.FeeSats);
    }

    public async Task<HeroMintResult> ExecuteMergeAsync(
        string mergeId, HeroMintData fusedData, byte[] oracleSignature64, CancellationToken ct = default)
    {
        if (!_mergeEscrows.TryGetValue(mergeId, out var escrow))
            throw new InvalidOperationException($"Unknown merge escrow {mergeId}.");
        if (!escrow.Funded) throw new InvalidOperationException($"Merge escrow {mergeId} is not funded.");
        if (escrow.Executed) throw new InvalidOperationException("Merge already executed.");

        // Enforce the same oracle rule rung 2's covenant will: a BIP340 signature over the
        // fused hero's metadata Merkle root (same shape as a bred child — base as parentA,
        // sacrifice as parentB). The server signs this exact root; the covenant reads it on-chain.
        var root = Covenants.ArkadeCovenants.MetadataMerkleRoot(Covenants.BreedEscrowContracts.ChildMetadata(
            fusedData.GenomeHex, fusedData.Generation, fusedData.ParentAId ?? "", fusedData.ParentBId ?? "",
            fusedData.ServerSeedHex ?? "", fusedData.PlayerNonce ?? ""));
        if (!NBitcoin.Secp256k1.ECXOnlyPubKey.TryCreate(Convert.FromHexString(escrow.OraclePkHex), out var oraclePk)
            || oraclePk is null
            || !NBitcoin.Secp256k1.SecpSchnorrSignature.TryCreate(oracleSignature64, out var signature)
            || signature is null
            || !oraclePk.SigVerifyBIP340(signature, root))
            throw new InvalidOperationException("Oracle signature does not authorize this merge.");

        escrow.Executed = true;
        // Both inputs BURNED (removed entirely — a true sink); the fused hero minted to the player.
        _assetHolders.TryRemove(escrow.BaseAssetId, out _);
        _assetHolders.TryRemove(escrow.SacrificeAssetId, out _);
        var assetId = NewId("sim-asset");
        _assetHolders[assetId] = escrow.PlayerId;
        await Task.CompletedTask;
        return new HeroMintResult(assetId, NewId("sim-merge"));
    }

    public Task<Covenants.MergeEscrowParams?> GetMergeEscrowParamsAsync(string mergeId, CancellationToken ct = default)
    {
        if (!_mergeEscrows.TryGetValue(mergeId, out var e)) return Task.FromResult<Covenants.MergeEscrowParams?>(null);
        return Task.FromResult<Covenants.MergeEscrowParams?>(new Covenants.MergeEscrowParams(
            $"sim-player-{e.PlayerId}", e.BaseAssetId, e.SacrificeAssetId, "sim-species",
            "sim-treasury", e.FeeSats, e.FeeSats + 660, e.OraclePkHex, mergeId, e.RefundAfterUnixSeconds));
    }

    // ── Hero-bid escrows (simulated) — the offer covenant with the roles swapped ──

    private sealed record BidEscrow(
        string BidderId, string OwnerId, string HeroAssetId,
        long BidSats, long FeeSats, long RefundAfterUnixSeconds)
    {
        public bool Funded;
        public bool Settled;
    }

    private readonly ConcurrentDictionary<string, BidEscrow> _bidEscrows = new();

    public async Task<string> CreateBidEscrowAsync(
        string bidId, string bidderPlayerId, string ownerPlayerId, string heroAssetId,
        long bidSats, long feeSats, long refundAfterUnixSeconds, CancellationToken ct = default)
    {
        await GetPlayerAddressAsync(bidderPlayerId, ct);
        await GetPlayerAddressAsync(ownerPlayerId, ct);
        _bidEscrows[bidId] = new BidEscrow(
            bidderPlayerId, ownerPlayerId, heroAssetId, bidSats, feeSats, refundAfterUnixSeconds);
        return $"sim-bid-escrow-{bidId}";
    }

    public Task<bool> IsBidEscrowFundedAsync(string bidId, CancellationToken ct = default)
        => Task.FromResult(_bidEscrows.TryGetValue(bidId, out var e) && e.Funded);

    /// <summary>Simulated bidder deposit: the sats leave their balance and are held by the covenant, NOT
    /// by the treasury — the whole point.</summary>
    public void FundBidEscrowFromPlayer(string playerId, string bidId)
    {
        if (!_bidEscrows.TryGetValue(bidId, out var escrow))
            throw new InvalidOperationException($"Unknown bid escrow {bidId}.");
        if (escrow.BidderId != playerId) throw new InvalidOperationException("Not the bidder.");
        var paid = false;
        _playerBalances.AddOrUpdate(playerId, _ => throw new InvalidOperationException("No wallet."),
            (_, bal) => { if (bal < escrow.BidSats) return bal; paid = true; return bal - escrow.BidSats; });
        if (!paid) throw new InvalidOperationException($"Insufficient balance for the {escrow.BidSats}-sat bid.");
        escrow.Funded = true;
    }

    public Task<bool> WasBidSettledAsync(string bidId, CancellationToken ct = default)
        => Task.FromResult(_bidEscrows.TryGetValue(bidId, out var e) && e.Settled);

    /// <summary>Simulated OWNER-side settle — counterpart of <see cref="FulfillOfferFromBuyer"/>, and a dev
    /// helper rather than an interface member for the same reason.</summary>
    public string SettleBidFromOwner(string bidId)
    {
        if (!_bidEscrows.TryGetValue(bidId, out var escrow))
            throw new InvalidOperationException($"Unknown bid escrow {bidId}.");
        if (!escrow.Funded) throw new InvalidOperationException($"Bid escrow {bidId} is not funded.");
        if (escrow.Settled) throw new InvalidOperationException("Bid escrow already settled.");
        // The covenant will not co-sign a partial settle, so the simulation refuses one too: the owner
        // cannot take the sats while still holding the hero.
        if (_assetHolders.GetValueOrDefault(escrow.HeroAssetId) != escrow.OwnerId)
            throw new InvalidOperationException("The owner no longer holds the hero this bid was accepted on.");

        escrow.Settled = true;
        _assetHolders[escrow.HeroAssetId] = escrow.BidderId;
        _playerBalances.AddOrUpdate(escrow.OwnerId, escrow.BidSats - escrow.FeeSats,
            (_, b) => b + escrow.BidSats - escrow.FeeSats);
        Interlocked.Add(ref _treasuryBalance, escrow.FeeSats);
        return NewId("sim-bid-settle");
    }

    public Task<Covenants.BidEscrowParams?> GetBidEscrowParamsAsync(string bidId, CancellationToken ct = default)
    {
        if (!_bidEscrows.TryGetValue(bidId, out var e)) return Task.FromResult<Covenants.BidEscrowParams?>(null);
        return Task.FromResult<Covenants.BidEscrowParams?>(new Covenants.BidEscrowParams(
            $"sim-player-{e.BidderId}", $"sim-player-{e.OwnerId}", e.HeroAssetId, e.BidSats,
            bidId, e.RefundAfterUnixSeconds, e.FeeSats, "sim-treasury"));
    }

    // ── Death-match escrows (simulated) — ONE JOINT escrow, burn the loser's hero ──

    private sealed record DeathMatchJointEscrow(
        string ChallengerPlayerId, string ChallengerHeroAssetId,
        string DefenderPlayerId, string DefenderHeroAssetId,
        string CommitmentHex, string OraclePkHex, long RefundAfterUnixSeconds,
        IReadOnlyList<string> ChallengerGearItemIds, IReadOnlyList<string> DefenderGearItemIds)
    {
        public bool ChallengerFunded;
        public bool DefenderFunded;
        /// <summary>itemId → units held IN the escrow (moved out of the staker's holdings at fund time — a staked unit cannot be sold).</summary>
        public readonly Dictionary<string, ulong> StakedGear = new(StringComparer.Ordinal);
    }

    private readonly ConcurrentDictionary<string, DeathMatchJointEscrow> _deathMatchEscrows = new();
    private readonly ConcurrentDictionary<string, bool> _deathMatchSettled = new();

    public async Task<string> CreateDeathMatchJointEscrowAsync(
        string deathMatchId, string challengerPlayerId, string challengerHeroAssetId,
        string defenderPlayerId, string defenderHeroAssetId,
        byte[] seedCommitment32, string oraclePubKeyHex, long refundAfterUnixSeconds,
        IReadOnlyList<string>? challengerGearItemIds = null, IReadOnlyList<string>? defenderGearItemIds = null,
        bool absorb = false, string speciesId = "",
        CancellationToken ct = default)
    {
        _ = (absorb, speciesId); // the sim picks keep-vs-mint by which settle method the server calls
        await GetPlayerAddressAsync(challengerPlayerId, ct);
        _deathMatchEscrows[deathMatchId] = new DeathMatchJointEscrow(
            challengerPlayerId, challengerHeroAssetId, defenderPlayerId, defenderHeroAssetId,
            Convert.ToHexString(seedCommitment32).ToLowerInvariant(), oraclePubKeyHex, refundAfterUnixSeconds,
            challengerGearItemIds ?? [], defenderGearItemIds ?? []);
        return $"sim-dm-escrow-{deathMatchId}";
    }

    public Task<bool> IsDeathMatchEscrowFundedAsync(string deathMatchId, CancellationToken ct = default)
        => Task.FromResult(_deathMatchEscrows.TryGetValue(deathMatchId, out var e) && e.ChallengerFunded && e.DefenderFunded);

    /// <summary>Simulated client-wallet stake of the staker's hero AND their baked gear units into the ONE joint death-match escrow (role selects which party stakes). Gear units move OUT of the player's holdings into the escrow — a staked unit cannot be sold.</summary>
    public void FundDeathMatchEscrowFromPlayer(string playerId, string deathMatchId, string role)
    {
        if (!_deathMatchEscrows.TryGetValue(deathMatchId, out var escrow))
            throw new InvalidOperationException($"Unknown death-match escrow {deathMatchId}.");
        var (party, heroAsset, gearItemIds) = role == "challenger"
            ? (escrow.ChallengerPlayerId, escrow.ChallengerHeroAssetId, escrow.ChallengerGearItemIds)
            : (escrow.DefenderPlayerId, escrow.DefenderHeroAssetId, escrow.DefenderGearItemIds);
        if (party != playerId)
            throw new InvalidOperationException("Not the staking player.");
        if (_assetHolders.GetValueOrDefault(heroAsset) != playerId)
            throw new InvalidOperationException("The player does not hold the staked hero.");
        foreach (var itemId in gearItemIds)
        {
            if (_itemHoldings.GetValueOrDefault((playerId, itemId)) < 1)
                throw new InvalidOperationException($"The player does not hold a free unit of '{itemId}' to stake.");
        }
        foreach (var itemId in gearItemIds)
        {
            _itemHoldings.AddOrUpdate((playerId, itemId), _ => throw new InvalidOperationException("Race: unit vanished."), (_, count) => count - 1);
            escrow.StakedGear[itemId] = escrow.StakedGear.GetValueOrDefault(itemId) + 1;
        }
        if (role == "challenger") escrow.ChallengerFunded = true; else escrow.DefenderFunded = true;
    }

    /// <summary>Simulated timelocked per-side reclaim: after expiry, return THIS side's staked hero (never moved out of holdings) + gear units and clear THIS side's funded flag. Works for both half- and fully-funded (each side reclaims its own). Refused before expiry or after settle.</summary>
    public void ReclaimDeathMatchFromPlayer(string playerId, string deathMatchId)
    {
        if (!_deathMatchEscrows.TryGetValue(deathMatchId, out var escrow))
            throw new InvalidOperationException($"Unknown death-match escrow {deathMatchId}.");
        if (_deathMatchSettled.ContainsKey(deathMatchId))
            throw new InvalidOperationException("Death-match already settled — nothing to reclaim.");
        var isChallenger = escrow.ChallengerPlayerId == playerId;
        var isDefender = escrow.DefenderPlayerId == playerId;
        if (!isChallenger && !isDefender)
            throw new InvalidOperationException("Not a party to this death-match.");
        var funded = isChallenger ? escrow.ChallengerFunded : escrow.DefenderFunded;
        if (!funded) throw new InvalidOperationException("Nothing staked to reclaim.");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (now < escrow.RefundAfterUnixSeconds)
            throw new InvalidOperationException($"Reclaim locked until {escrow.RefundAfterUnixSeconds} (chain time {now}).");
        var gearIds = isChallenger ? escrow.ChallengerGearItemIds : escrow.DefenderGearItemIds;
        foreach (var itemId in gearIds)
        {
            if (!escrow.StakedGear.TryGetValue(itemId, out var units) || units < 1) continue;
            escrow.StakedGear[itemId] = units - 1;
            _itemHoldings.AddOrUpdate((playerId, itemId), 1UL, (_, count) => count + 1);
        }
        if (isChallenger) escrow.ChallengerFunded = false; else escrow.DefenderFunded = false;
    }

    public Task<string> SettleDeathMatchAsync(
        string deathMatchId, bool challengerWon, byte[] serverSeed, byte[] oracleSignature64, CancellationToken ct = default)
    {
        if (!_deathMatchEscrows.TryGetValue(deathMatchId, out var escrow))
            throw new InvalidOperationException($"Death-match {deathMatchId} escrow is not created.");
        if (!escrow.ChallengerFunded || !escrow.DefenderFunded)
            throw new InvalidOperationException($"Death-match {deathMatchId} is not fully staked.");
        if (_deathMatchSettled.ContainsKey(deathMatchId))
            throw new InvalidOperationException("Death-match already settled.");

        // The same oracle rule the covenant enforces: a BIP340 signature over THIS
        // branch's death-match settle message.
        var message = Covenants.ArkadeCovenants.DeathMatchSettleMessage(deathMatchId, challengerWon);
        if (!NBitcoin.Secp256k1.ECXOnlyPubKey.TryCreate(Convert.FromHexString(escrow.OraclePkHex), out var oraclePk)
            || oraclePk is null
            || !NBitcoin.Secp256k1.SecpSchnorrSignature.TryCreate(oracleSignature64, out var signature)
            || signature is null
            || !oraclePk.SigVerifyBIP340(signature, message))
            throw new InvalidOperationException("Oracle signature does not authorize this death-match settle.");

        _deathMatchSettled[deathMatchId] = true;
        // The LOSER's hero is BURNED (removed); the winner's hero stays the winner's.
        var loserHeroAsset = challengerWon ? escrow.DefenderHeroAssetId : escrow.ChallengerHeroAssetId;
        _assetHolders.TryRemove(loserHeroAsset, out _);
        // ALL staked gear (both sides') goes to the winner — the covenant's routing.
        var winnerPlayerId = challengerWon ? escrow.ChallengerPlayerId : escrow.DefenderPlayerId;
        foreach (var (itemId, units) in escrow.StakedGear)
            _itemHoldings.AddOrUpdate((winnerPlayerId, itemId), units, (_, count) => count + units);
        escrow.StakedGear.Clear();
        return Task.FromResult(NewId("sim-dm-settle"));
    }

    public Task<HeroMintResult> SettleDeathMatchAbsorbMintAsync(
        string deathMatchId, bool challengerWon, HeroMintData absorbedData,
        byte[] serverSeed, byte[] outcomeSignature64, byte[] rootSignature64, CancellationToken ct = default)
    {
        if (!_deathMatchEscrows.TryGetValue(deathMatchId, out var escrow))
            throw new InvalidOperationException($"Death-match {deathMatchId} escrow is not created.");
        if (!escrow.ChallengerFunded || !escrow.DefenderFunded)
            throw new InvalidOperationException($"Death-match {deathMatchId} is not fully staked.");
        if (_deathMatchSettled.ContainsKey(deathMatchId))
            throw new InvalidOperationException("Death-match already settled.");

        // The same TWO oracle gates the covenant enforces: the absorb-mint OUTCOME message
        // (which side won) AND the absorbed metadata ROOT (the correct genome).
        var outcomeMsg = Covenants.ArkadeCovenants.DeathMatchAbsorbMintMessage(deathMatchId, challengerWon);
        var root = Covenants.ArkadeCovenants.MetadataMerkleRoot(Covenants.BreedEscrowContracts.ChildMetadata(
            absorbedData.GenomeHex, absorbedData.Generation, absorbedData.ParentAId ?? "", absorbedData.ParentBId ?? "",
            absorbedData.ServerSeedHex ?? "", absorbedData.PlayerNonce ?? ""));
        if (!VerifyOracleSig(escrow.OraclePkHex, outcomeMsg, outcomeSignature64)
            || !VerifyOracleSig(escrow.OraclePkHex, root, rootSignature64))
            throw new InvalidOperationException("Oracle signatures do not authorize this absorb-mint settle.");

        _deathMatchSettled[deathMatchId] = true;
        var winnerPlayerId = challengerWon ? escrow.ChallengerPlayerId : escrow.DefenderPlayerId;
        // BURN BOTH heroes; MINT the absorbed hero to the winner (a NEW asset).
        _assetHolders.TryRemove(escrow.ChallengerHeroAssetId, out _);
        _assetHolders.TryRemove(escrow.DefenderHeroAssetId, out _);
        var absorbedAssetId = NewId("sim-asset");
        _assetHolders[absorbedAssetId] = winnerPlayerId;
        // ALL staked gear (both sides') → the winner — the covenant's routing.
        foreach (var (itemId, units) in escrow.StakedGear)
            _itemHoldings.AddOrUpdate((winnerPlayerId, itemId), units, (_, count) => count + units);
        escrow.StakedGear.Clear();
        return Task.FromResult(new HeroMintResult(absorbedAssetId, NewId("sim-absorb")));
    }

    private static bool VerifyOracleSig(string oraclePkHex, byte[] message, byte[] signature64) =>
        NBitcoin.Secp256k1.ECXOnlyPubKey.TryCreate(Convert.FromHexString(oraclePkHex), out var pk) && pk is not null
        && NBitcoin.Secp256k1.SecpSchnorrSignature.TryCreate(signature64, out var sig) && sig is not null
        && pk.SigVerifyBIP340(sig, message);

    public Task<Covenants.DeathMatchJointEscrowParams?> GetDeathMatchEscrowParamsAsync(string deathMatchId, CancellationToken ct = default)
    {
        if (!_deathMatchEscrows.TryGetValue(deathMatchId, out var e))
            return Task.FromResult<Covenants.DeathMatchJointEscrowParams?>(null);
        static IReadOnlyList<Covenants.GearStake> ToStakes(IReadOnlyList<string> itemIds) => itemIds
            .GroupBy(id => id, StringComparer.Ordinal)
            .Select(g => new Covenants.GearStake(g.Key, g.Count(), g.Key)) // sim asset id == item id
            .ToList();
        return Task.FromResult<Covenants.DeathMatchJointEscrowParams?>(new Covenants.DeathMatchJointEscrowParams(
            $"sim-player-{e.ChallengerPlayerId}", e.ChallengerHeroAssetId,
            $"sim-player-{e.DefenderPlayerId}", e.DefenderHeroAssetId,
            e.CommitmentHex, e.OraclePkHex, deathMatchId, 330, e.RefundAfterUnixSeconds,
            ChallengerGear: ToStakes(e.ChallengerGearItemIds), DefenderGear: ToStakes(e.DefenderGearItemIds)));
    }

    // ── Covenant offers (simulated) — fungible items and unique heroes ──

    /// <summary>Carrier dust deposited with a resting offer's asset (the sim's stand-in for serverInfo.Dust).</summary>
    private const long SimOfferDust = 660;

    /// <summary>Kind "item" (fungible, keyed by ItemId) or "hero" (unique, keyed by HeroAssetId).</summary>
    private sealed record SimOffer(
        string SellerId, string Kind, string ItemId, string HeroAssetId,
        long AskSats, long OfferValueSats, long RefundAfterUnixSeconds, long FeeSats = 0)
    {
        public bool Funded;
        public bool Closed; // fulfilled or reclaimed
        public bool Sold;   // closed by a BUYER's fulfil, not a seller reclaim — the treasury was paid
        public bool IsHero => Kind == "hero";
    }

    private readonly ConcurrentDictionary<string, SimOffer> _offers = new();

    public async Task<OfferInfo> CreateOfferAsync(
        string offerId, string sellerPlayerId, string itemId, long askSats,
        long refundAfterUnixSeconds, long feeSats = 0, CancellationToken ct = default)
    {
        if (askSats <= 0) throw new InvalidOperationException("The ask must be positive.");
        RequireFeeBelowAsk(askSats, feeSats);
        await GetPlayerAddressAsync(sellerPlayerId, ct);
        var assetId = _itemAssets.GetOrAdd(itemId, _ => NewId("sim-item"));
        _offers[offerId] = new SimOffer(sellerPlayerId, "item", itemId, "", askSats, SimOfferDust, refundAfterUnixSeconds, feeSats);
        return new OfferInfo(offerId, $"sim-offer-{offerId}", assetId, askSats, SimOfferDust, refundAfterUnixSeconds);
    }

    public async Task<OfferInfo> CreateHeroOfferAsync(
        string offerId, string sellerPlayerId, string heroAssetId, long askSats,
        long refundAfterUnixSeconds, long feeSats = 0, CancellationToken ct = default)
    {
        if (askSats <= 0) throw new InvalidOperationException("The ask must be positive.");
        RequireFeeBelowAsk(askSats, feeSats);
        await GetPlayerAddressAsync(sellerPlayerId, ct);
        _offers[offerId] = new SimOffer(sellerPlayerId, "hero", "", heroAssetId, askSats, SimOfferDust, refundAfterUnixSeconds, feeSats);
        return new OfferInfo(offerId, $"sim-offer-{offerId}", heroAssetId, askSats, SimOfferDust, refundAfterUnixSeconds);
    }

    /// <summary>The covenant's own guard, mirrored so the sim refuses what the chain would: a fee at or
    /// above the ask leaves the seller nothing, and PayTo rejects a non-positive amount outright.</summary>
    private static void RequireFeeBelowAsk(long askSats, long feeSats)
    {
        if (feeSats < 0 || feeSats >= askSats)
            throw new InvalidOperationException(
                $"The marketplace fee ({feeSats}) must be non-negative and below the ask ({askSats}).");
    }

    public Task<bool> IsOfferFundedAsync(string offerId, CancellationToken ct = default)
        => Task.FromResult(_offers.TryGetValue(offerId, out var o) && o.Funded && !o.Closed);

    /// <summary>Mirrors the chain's proof: only a buyer's fulfil pays the treasury, and only a
    /// fee-bearing offer leaves anything to attribute.</summary>
    public Task<bool> WasOfferSoldAsync(string offerId, CancellationToken ct = default)
        => Task.FromResult(_offers.TryGetValue(offerId, out var o) && o.Sold && o.FeeSats > 0);

    public Task<Covenants.OfferParams?> GetOfferParamsAsync(string offerId, CancellationToken ct = default)
    {
        if (!_offers.TryGetValue(offerId, out var o)) return Task.FromResult<Covenants.OfferParams?>(null);
        var assetId = o.IsHero ? o.HeroAssetId : _itemAssets.GetValueOrDefault(o.ItemId, $"sim-item-{o.ItemId}");
        return Task.FromResult<Covenants.OfferParams?>(new Covenants.OfferParams(
            $"sim-player-{o.SellerId}", assetId, o.AskSats, o.OfferValueSats, offerId, o.RefundAfterUnixSeconds,
            o.FeeSats, o.FeeSats > 0 ? "sim-treasury" : null));
    }

    /// <summary>Simulated seller-wallet deposit of the offered asset (+ carrier dust) into the offer.</summary>
    public void FundOfferFromSeller(string sellerPlayerId, string offerId)
    {
        var offer = RequireOwnOffer(sellerPlayerId, offerId);
        if (offer.Funded) throw new InvalidOperationException("Offer already funded.");
        if (offer.IsHero)
        {
            if (_assetHolders.GetValueOrDefault(offer.HeroAssetId) != sellerPlayerId)
                throw new InvalidOperationException("Seller does not hold this hero.");
            _assetHolders[offer.HeroAssetId] = $"__offer__{offerId}"; // escrowed in the offer
        }
        else
        {
            var moved = false;
            _itemHoldings.AddOrUpdate((sellerPlayerId, offer.ItemId), _ => throw new InvalidOperationException("Seller holds none of this item."),
                (_, count) => { if (count < 1) return count; moved = true; return count - 1; });
            if (!moved) throw new InvalidOperationException("Seller holds none of this item to sell.");
        }
        offer.Funded = true;
    }

    /// <summary>Simulated buyer-wallet fulfilment: pays the seller the ask and takes the offered asset.</summary>
    public void FulfillOfferFromBuyer(string buyerPlayerId, string offerId)
    {
        if (!_offers.TryGetValue(offerId, out var offer))
            throw new InvalidOperationException($"Unknown offer {offerId}.");
        if (!offer.Funded) throw new InvalidOperationException("Offer is not funded.");
        if (offer.Closed) throw new InvalidOperationException("Offer already fulfilled or reclaimed.");
        if (offer.SellerId == buyerPlayerId) throw new InvalidOperationException("Cannot buy your own offer.");
        var paid = false;
        _playerBalances.AddOrUpdate(buyerPlayerId, _ => throw new InvalidOperationException("Buyer has no wallet."),
            (_, bal) => { if (bal < offer.AskSats) return bal; paid = true; return bal - offer.AskSats; });
        if (!paid) throw new InvalidOperationException($"Insufficient balance for the {offer.AskSats}-sat ask.");
        // The buyer paid the full ask; the covenant SPLITS it. The seller absorbs the marketplace fee, so
        // they receive ask − fee and the treasury takes the rest. Mirrored here because the sim is what
        // every InMemory marketplace test measures — without the split those tests would assert a payout
        // the real chain never makes.
        _playerBalances.AddOrUpdate(offer.SellerId, offer.AskSats - offer.FeeSats,
            (_, bal) => bal + offer.AskSats - offer.FeeSats);
        if (offer.FeeSats > 0) Interlocked.Add(ref _treasuryBalance, offer.FeeSats);
        if (offer.IsHero) _assetHolders[offer.HeroAssetId] = buyerPlayerId;
        else _itemHoldings.AddOrUpdate((buyerPlayerId, offer.ItemId), 1UL, (_, count) => count + 1);
        offer.Closed = true;
        offer.Sold = true;
    }

    /// <summary>Simulated seller reclaim of an unsold offer after expiry — the asset returns to the seller.</summary>
    public void ReclaimOfferToSeller(string sellerPlayerId, string offerId)
    {
        var offer = RequireOwnOffer(sellerPlayerId, offerId);
        if (!offer.Funded) throw new InvalidOperationException("Offer is not funded.");
        if (offer.Closed) throw new InvalidOperationException("Offer already fulfilled or reclaimed.");
        if (offer.IsHero) _assetHolders[offer.HeroAssetId] = sellerPlayerId;
        else _itemHoldings.AddOrUpdate((sellerPlayerId, offer.ItemId), 1UL, (_, count) => count + 1);
        offer.Closed = true;
    }

    private SimOffer RequireOwnOffer(string sellerPlayerId, string offerId)
    {
        if (!_offers.TryGetValue(offerId, out var offer))
            throw new InvalidOperationException($"Unknown offer {offerId}.");
        if (offer.SellerId != sellerPlayerId) throw new InvalidOperationException("Not the offer's seller.");
        return offer;
    }

    // ── On-chain reads ─────────────────────────────────────────────────

    public Task<bool> VerifyHeroOwnershipAsync(string playerId, string assetId, CancellationToken ct = default)
        => Task.FromResult(_assetHolders.TryGetValue(assetId, out var holder) && holder == playerId);

    public Task<ulong> GetItemAssetBalanceAsync(string playerId, string itemId, CancellationToken ct = default)
        => Task.FromResult(_itemHoldings.GetValueOrDefault((playerId, itemId)));
}
