using ArkadeHeroes.Chain.NArk;
using NArk.Abstractions;
using NArk.Core.Assets;
using NBitcoin;

namespace ArkadeHeroes.Chain.Covenants;

/// <summary>
/// The SELLER side of cancelling an unsold item offer: rebuilds the offer
/// covenant from its public params, locates the resting offer VTXO (item +
/// carrier dust), gates on the chain's clock, and spends the offer's timelocked
/// <c>reclaim</c> leaf EXACTLY ONCE to return the item to the seller.
///
/// Same submit-once discipline as <see cref="EscrowRefundFlow"/>: the canonical
/// reclaim tx is deterministic, and arkd permanently poisons a txid's event
/// stream on ANY refused submission, so this flow refuses to submit until the
/// chain's median-time-past has reached the expiry and never retries itself.
/// Trustless: the contract is rebuilt locally, and the reclaim leaf script-pins
/// BOTH the item and its carrier output to the seller's own address (covenant-v2
/// AssetAtOutput) — a lying server can make this fail, never steal.
/// </summary>
public static class OfferReclaimFlow
{
    /// <returns>The emulator's co-signed response for the reclaim transaction.</returns>
    public static async Task<EmulatorSubmitResponse> ReclaimAsync(
        SelfCustodyWallet seller,
        Uri emulatorUri,
        OfferParams offer,
        Func<CancellationToken, Task<long>> chainMedianTime,
        TimeSpan? vtxoTimeout = null,
        CancellationToken ct = default)
    {
        var transport = seller.GetService<global::NArk.Core.Transport.IClientTransport>();
        var serverInfo = await transport.GetServerInfoAsync(ct);
        var emulatorInfo = await new EmulatorClient(emulatorUri).GetInfoAsync(ct);

        if (seller.Address != offer.SellerAddress)
            throw new InvalidOperationException(
                $"This wallet ({seller.Address}) is not the seller of offer {offer.OfferId}.");

        var contract = OfferContracts.Build(offer, serverInfo.SignerKey, emulatorInfo.SignerPubkey);
        var sellerScript = ArkAddress.Parse(offer.SellerAddress).ScriptPubKey;
        var item = AssetId.FromString(offer.ItemAssetId);

        IReadOnlyList<global::NArk.Abstractions.VTXOs.ArkVtxo> vtxos;
        try
        {
            vtxos = await CovenantSpender.WaitForVtxosAsync(
                seller, contract, 1, vtxoTimeout ?? TimeSpan.FromSeconds(20), ct);
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException(
                $"No VTXO at the offer address for {offer.OfferId} — nothing listed, already sold, or already reclaimed.");
        }
        var offerVtxo = vtxos.FirstOrDefault(v => v.Assets?.Any(a => a.AssetId == offer.ItemAssetId) == true)
            ?? throw new InvalidOperationException(
                $"No item-carrying VTXO at the offer address for {offer.OfferId}.");

        // Gate on the CHAIN's clock, never the wall clock (see EscrowRefundFlow).
        var chainNow = await chainMedianTime(ct);
        if (chainNow < offer.RefundAfterUnixSeconds)
            throw new RefundNotYetDueException(offer.RefundAfterUnixSeconds, chainNow);

        // Single canonical submission: the item passes through to the seller's
        // output (asset conservation), whose sats the reclaim leaf pins.
        var packet = Packet.Create(
        [
            AssetGroup.Create(item, null,
                [AssetInput.Create(0, 1)], [AssetOutput.Create(0, 1)], []),
        ]);
        return await CovenantSpender.SpendManyAsync(
            seller, emulatorUri,
            [
                // The covenant-v2 reclaim leaf is fully baked (AssetAtOutput: item → output 0
                // paying the seller, script-pinned) — EMPTY witness.
                new CovenantSpender.CovenantInput(
                    contract, "reclaim", [], offerVtxo,
                    LockTime: new LockTime((uint)offer.RefundAfterUnixSeconds)),
            ],
            [new TxOut(Money.Satoshis(offer.OfferValueSats), sellerScript)],
            extraPackets: [packet],
            ct: ct);
    }
}
