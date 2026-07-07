using ArkadeHeroes.Chain.NArk;
using NArk.Abstractions;
using NArk.Abstractions.Assets;
using NArk.Core.Assets;
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
    public static async Task<EmulatorSubmitResponse> ReclaimAsync(
        SelfCustodyWallet wallet,
        Uri emulatorUri,
        BreedEscrowParams parameters,
        Func<CancellationToken, Task<long>> chainMedianTime,
        TimeSpan? vtxoTimeout = null,
        CancellationToken ct = default)
    {
        var transport = wallet.GetService<global::NArk.Core.Transport.IClientTransport>();
        var serverInfo = await transport.GetServerInfoAsync(ct);
        var emulatorInfo = await new EmulatorClient(emulatorUri).GetInfoAsync(ct);
        var contract = BreedEscrowContracts.Build(parameters, serverInfo.SignerKey, emulatorInfo.SignerPubkey);

        if (wallet.Address != parameters.PlayerAddress)
            throw new InvalidOperationException(
                $"This wallet ({wallet.Address}) is not the breeding player for {parameters.BreedingId}.");
        var playerScript = ArkAddress.Parse(wallet.Address).ScriptPubKey;
        var parentA = AssetId.FromString(parameters.ParentAId);
        var parentB = AssetId.FromString(parameters.ParentBId);

        IReadOnlyList<global::NArk.Abstractions.VTXOs.ArkVtxo> vtxos;
        try
        {
            vtxos = await CovenantSpender.WaitForVtxosAsync(
                wallet, contract, 1, vtxoTimeout ?? TimeSpan.FromSeconds(20), ct);
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
        return await CovenantSpender.SpendManyAsync(
            wallet, emulatorUri, inputs,
            [new TxOut(Money.Satoshis(total), playerScript)],
            extraPackets: [packet], ct: ct);
    }
}
