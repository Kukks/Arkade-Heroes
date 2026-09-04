using System.Text.Json;
using ArkadeHeroes.Chain.NArk;
using Microsoft.Extensions.DependencyInjection;
using NArk.Abstractions;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Safety;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Arkade.Emulator;
using NArk.Core.Services;
using NBitcoin;

namespace ArkadeHeroes.Chain.Covenants;

/// <summary>
/// Thrown when the refund's CLTV expiry has not yet passed on the CHAIN's
/// clock. Safe to retry later — nothing was submitted.
/// </summary>
public sealed class RefundNotYetDueException(long dueUnixSeconds, long chainUnixSeconds)
    : InvalidOperationException(
        $"Refund locked until chain time {dueUnixSeconds}; the chain clock (median-time-past) is at {chainUnixSeconds}. Retry later.")
{
    public long DueUnixSeconds { get; } = dueUnixSeconds;
    public long ChainUnixSeconds { get; } = chainUnixSeconds;
}

/// <summary>
/// The CLIENT side of the wager-escrow refund: rebuilds the escrow contracts
/// from the public match params (trusting only the operator + emulator keys it
/// fetches itself), locates the caller's own stake VTXO, gates on the chain's
/// clock, and submits the canonical refund EXACTLY ONCE.
///
/// SUBMIT-ONCE DISCIPLINE (contracts/README, timelock invariant #4): the
/// canonical refund tx is fully deterministic, and arkd permanently poisons a
/// txid's event stream on ANY refused submission — a later accepted retry
/// finalizes at the RPC level but its VTXOs are never created. This flow
/// therefore refuses to submit until the chain's median-time-past has reached
/// the expiry, and never retries a submission itself.
/// </summary>
public static class EscrowRefundFlow
{
    /// <summary>Refunds from a <see cref="SelfCustodyWallet"/> (console/tests).</summary>
    /// <returns>The emulator's co-signed response for the refund transaction.</returns>
    public static Task<EmulatorSubmitTxResult> RefundAsync(
        SelfCustodyWallet wallet,
        Uri emulatorUri,
        WagerEscrowParams parameters,
        Func<CancellationToken, Task<long>> chainMedianTime,
        TimeSpan? vtxoTimeout = null,
        CancellationToken ct = default)
        => RefundAsync(wallet.Services, wallet.WalletId, wallet.Address,
            emulatorUri, parameters, chainMedianTime, vtxoTimeout, ct);

    /// <summary>
    /// Service-level refund — runs against any NArk service graph (a player wallet's isolated
    /// container OR a browser's Blazor DI), so the console and the browser share ONE implementation
    /// of this covenant spend rather than each carrying its own.
    ///
    /// Reclaims the caller's stake from an abandoned covenant match. Trustless by
    /// construction: the contracts are rebuilt locally from <paramref name="parameters"/>,
    /// and the refund leaf can only pay the caller's own address — a lying server can
    /// make this fail, never steal.
    /// </summary>
    /// <returns>The emulator's co-signed response for the refund transaction.</returns>
    public static async Task<EmulatorSubmitTxResult> RefundAsync(
        IServiceProvider services,
        string walletId,
        string playerAddress,
        Uri emulatorUri,
        WagerEscrowParams parameters,
        Func<CancellationToken, Task<long>> chainMedianTime,
        TimeSpan? vtxoTimeout = null,
        CancellationToken ct = default)
    {
        var transport = services.GetRequiredService<global::NArk.Core.Transport.IClientTransport>();
        var serverInfo = await transport.GetServerInfoAsync(ct);
        var emulatorInfo = await EmulatorEndpoint.Client(emulatorUri).GetInfoAsync(ct);

        var (challengerContract, defenderContract) =
            WagerEscrowContracts.Build(parameters, serverInfo.SignerKey, emulatorInfo.SignerPubkey);

        var isChallenger = playerAddress == parameters.ChallengerAddress;
        var isDefender = playerAddress == parameters.DefenderAddress;
        if (!isChallenger && !isDefender)
            throw new InvalidOperationException(
                $"This wallet ({playerAddress}) is not a party to match {parameters.MatchId}.");
        var myContract = isChallenger ? challengerContract : defenderContract;
        var myScript = ArkAddress.Parse(playerAddress).ScriptPubKey;

        IReadOnlyList<global::NArk.Abstractions.VTXOs.ArkVtxo> vtxos;
        try
        {
            vtxos = await CovenantSpender.WaitForVtxosCoreAsync(
                services.GetRequiredService<VtxoSynchronizationService>(),
                services.GetRequiredService<IVtxoStorage>(),
                myContract, 1, vtxoTimeout ?? TimeSpan.FromSeconds(20), ct);
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException(
                $"No VTXO at the escrow address for match {parameters.MatchId} — nothing staked, already settled, or already refunded.");
        }
        var stake = vtxos.FirstOrDefault(v => (long)v.Amount == parameters.StakeSats)
                    ?? throw new InvalidOperationException(
                        $"No exact-stake ({parameters.StakeSats} sat) VTXO at the escrow for match {parameters.MatchId}.");

        // Gate on the CHAIN's clock, never the wall clock: median-time-past is
        // sufficient under either operator blocktime semantic (consensus
        // guarantees tip time > MTP). Read-only — safe to call repeatedly.
        var chainNow = await chainMedianTime(ct);
        if (chainNow < parameters.RefundAfterUnixSeconds)
            throw new RefundNotYetDueException(parameters.RefundAfterUnixSeconds, chainNow);

        // Single canonical submission — no retry, no fallback (see class doc).
        return await CovenantSpender.SpendManyCoreAsync(
            transport,
            services.GetRequiredService<ISafetyService>(),
            services.GetRequiredService<IWalletProvider>(),
            services.GetRequiredService<IIntentStorage>(),
            walletId, emulatorUri,
            [
                new CovenantSpender.CovenantInput(
                    myContract, "refund", [ArkadeCovenants.EncodeIndex(0)], stake,
                    LockTime: new LockTime((uint)parameters.RefundAfterUnixSeconds)),
            ],
            [new TxOut(Money.Satoshis(parameters.StakeSats), myScript)],
            ct: ct);
    }
}

/// <summary>
/// Minimal esplora client for the one chain fact refunds need: the tip's
/// median-time-past. Two GETs against an esplora-compatible REST API (the
/// regtest stack's mempool backend serves one at <c>http://localhost:8999/api/v1</c>).
/// Owned here rather than reusing the SDK's EsploraBlockchain to avoid the
/// HttpClient BaseAddress trailing-slash footgun.
/// </summary>
public static class EsploraChainTime
{
    public static async Task<long> GetMedianTimeAsync(HttpClient http, string apiBase, CancellationToken ct = default)
    {
        var baseUrl = apiBase.TrimEnd('/');
        var tipHash = (await http.GetStringAsync($"{baseUrl}/blocks/tip/hash", ct)).Trim();
        using var block = JsonDocument.Parse(await http.GetStringAsync($"{baseUrl}/block/{tipHash}", ct));
        return block.RootElement.GetProperty("mediantime").GetInt64();
    }
}
