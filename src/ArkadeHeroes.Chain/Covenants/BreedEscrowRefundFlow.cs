using ArkadeHeroes.Chain.NArk;
using Microsoft.Extensions.DependencyInjection;
using NArk.Abstractions;
using NArk.Abstractions.Assets;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Safety;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Arkade.Emulator;
using NArk.Core.Assets;
using NArk.Core.Services;
using NBitcoin;

namespace ArkadeHeroes.Chain.Covenants;

/// <summary>
/// The CLIENT side of the breed-escrow refund: rebuilds the breed contract from
/// the public <see cref="BreedEscrowParams"/> (trusting only the operator +
/// emulator keys it fetches itself), sweeps EVERY VTXO at the escrow — the two
/// parent carriers AND the fee sats VTXO — gates on the chain clock, and submits
/// the canonical refund EXACTLY ONCE. The refund leaf routes both parents back to
/// the player at output 0 (script-pinned), so a lying server can make this fail,
/// never steal. Mirrors <see cref="MergeEscrowRefundFlow"/>.
/// </summary>
public static class BreedEscrowRefundFlow
{
    /// <summary>Reclaims from a <see cref="SelfCustodyWallet"/> (console/tests).</summary>
    public static Task<EmulatorSubmitTxResult> ReclaimAsync(
        SelfCustodyWallet wallet,
        Uri emulatorUri,
        BreedEscrowParams parameters,
        Func<CancellationToken, Task<long>> chainMedianTime,
        TimeSpan? vtxoTimeout = null,
        CancellationToken ct = default)
        => ReclaimAsync(wallet.Services, wallet.WalletId, wallet.Address,
            emulatorUri, parameters, chainMedianTime, vtxoTimeout, ct);

    /// <summary>
    /// Service-level reclaim — runs against any NArk service graph (a player wallet's isolated
    /// container OR a browser's Blazor DI), so the console and the browser share ONE implementation
    /// of this covenant spend rather than each carrying its own.
    /// </summary>
    public static async Task<EmulatorSubmitTxResult> ReclaimAsync(
        IServiceProvider services,
        string walletId,
        string playerAddress,
        Uri emulatorUri,
        BreedEscrowParams parameters,
        Func<CancellationToken, Task<long>> chainMedianTime,
        TimeSpan? vtxoTimeout = null,
        CancellationToken ct = default)
    {
        var transport = services.GetRequiredService<global::NArk.Core.Transport.IClientTransport>();
        var serverInfo = await transport.GetServerInfoAsync(ct);
        var emulatorInfo = await EmulatorEndpoint.Client(emulatorUri).GetInfoAsync(ct);
        var contract = BreedEscrowContracts.Build(parameters, serverInfo.SignerKey, emulatorInfo.SignerPubkey);

        if (playerAddress != parameters.PlayerAddress)
            throw new InvalidOperationException(
                $"This wallet ({playerAddress}) is not the breeding player for {parameters.BreedingId}.");
        var playerScript = ArkAddress.Parse(playerAddress).ScriptPubKey;
        var parentA = AssetId.FromString(parameters.ParentAId);
        var parentB = AssetId.FromString(parameters.ParentBId);

        IReadOnlyList<global::NArk.Abstractions.VTXOs.ArkVtxo> vtxos;
        try
        {
            vtxos = await CovenantSpender.WaitForVtxosCoreAsync(
                services.GetRequiredService<VtxoSynchronizationService>(),
                services.GetRequiredService<IVtxoStorage>(),
                contract, 1, vtxoTimeout ?? TimeSpan.FromSeconds(20), ct);
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException(
                $"No VTXO at the breed escrow for {parameters.BreedingId} — nothing deposited, already bred, or already reclaimed.");
        }
        var parentAVtxo = vtxos.FirstOrDefault(v => v.Assets?.Any(a => a.AssetId == parameters.ParentAId) == true)
            ?? throw new InvalidOperationException($"Parent A is not at the breed escrow for {parameters.BreedingId}.");
        var parentBVtxo = vtxos.FirstOrDefault(v => v.Assets?.Any(a => a.AssetId == parameters.ParentBId) == true)
            ?? throw new InvalidOperationException($"Parent B is not at the breed escrow for {parameters.BreedingId}.");

        var chainNow = await chainMedianTime(ct);
        if (chainNow < parameters.RefundAfterUnixSeconds)
            throw new RefundNotYetDueException(parameters.RefundAfterUnixSeconds, chainNow);

        // Order inputs so the asset-packet vins are stable: parentA vin0, parentB vin1,
        // every other VTXO (the fee sats carrier) after. All spend the refund leaf.
        var others = vtxos.Where(v => v.OutPoint != parentAVtxo.OutPoint && v.OutPoint != parentBVtxo.OutPoint);
        var ordered = new[] { parentAVtxo, parentBVtxo }.Concat(others).ToList();
        var lockTime = new LockTime((uint)parameters.RefundAfterUnixSeconds);
        var inputs = ordered
            .Select(v => new CovenantSpender.CovenantInput(contract, "refund", [], v, LockTime: lockTime))
            .ToList();
        var total = ordered.Sum(v => (long)v.Amount);

        var packet = Packet.Create(
        [
            AssetGroup.Create(parentA, null, [AssetInput.Create(0, 1)], [AssetOutput.Create(0, 1)], []),
            AssetGroup.Create(parentB, null, [AssetInput.Create(1, 1)], [AssetOutput.Create(0, 1)], []),
        ]);
        // Single canonical submission — no retry (submit-once discipline).
        return await CovenantSpender.SpendManyCoreAsync(
            transport,
            services.GetRequiredService<ISafetyService>(),
            services.GetRequiredService<IWalletProvider>(),
            services.GetRequiredService<IIntentStorage>(),
            walletId, emulatorUri, inputs,
            [new TxOut(Money.Satoshis(total), playerScript)],
            extraPackets: [packet], ct: ct);
    }
}
