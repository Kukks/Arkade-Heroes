using ArkadeHeroes.Chain.NArk;
using Microsoft.Extensions.DependencyInjection;
using NArk.Abstractions;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Safety;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core.Assets;
using NArk.Core.Services;
using NBitcoin;

namespace ArkadeHeroes.Chain.Covenants;

/// <summary>
/// The BUYER side of an item offer: rebuild the offer covenant from its public
/// params, spend the resting offer VTXO through its <c>fulfill</c> leaf while
/// funding the seller's ask from the buyer's OWN wallet coins, and take the
/// item. The emulator co-signs the offer input only if the seller is paid
/// exactly the ask (underpayment is refused); the buyer's funding coins are
/// signed by the buyer's own key. Trustless by construction — the buyer rebuilds
/// the contract locally and can verify the address matches the listing.
/// </summary>
public static class OfferFulfillFlow
{
    /// <summary>Fulfils the offer from a <see cref="SelfCustodyWallet"/> (console/tests).</summary>
    public static Task<EmulatorSubmitResponse> FulfillAsync(
        SelfCustodyWallet buyer, Uri emulatorUri, OfferParams offer, CancellationToken ct = default)
        => FulfillAsync(buyer.Services, buyer.WalletId, buyer.Address, emulatorUri, offer, ct);

    /// <summary>
    /// Service-level fulfil — runs against any NArk service graph (a player wallet's isolated
    /// container OR a browser's Blazor DI). Pays the seller the ask from the buyer's own coins
    /// and delivers the item to <paramref name="buyerAddress"/>.
    /// </summary>
    public static async Task<EmulatorSubmitResponse> FulfillAsync(
        IServiceProvider services, string walletId, string buyerAddress,
        Uri emulatorUri, OfferParams offer, CancellationToken ct = default)
    {
        var transport = services.GetRequiredService<global::NArk.Core.Transport.IClientTransport>();
        var serverInfo = await transport.GetServerInfoAsync(ct);
        var emulatorInfo = await new EmulatorClient(emulatorUri).GetInfoAsync(ct);

        var contract = OfferContracts.Build(offer, serverInfo.SignerKey, emulatorInfo.SignerPubkey);
        var offerVtxo = (await CovenantSpender.WaitForVtxosCoreAsync(
                services.GetRequiredService<VtxoSynchronizationService>(),
                services.GetRequiredService<IVtxoStorage>(),
                contract, 1, TimeSpan.FromSeconds(20), ct))
            .FirstOrDefault(v => v.Assets?.Any(a => a.AssetId == offer.ItemAssetId) == true)
            ?? throw new InvalidOperationException($"Offer {offer.OfferId} is not funded with the item, or already fulfilled/reclaimed.");

        var sellerScript = ArkAddress.Parse(offer.SellerAddress).ScriptPubKey;
        var buyerScript = ArkAddress.Parse(buyerAddress).ScriptPubKey;

        // The buyer's funding coins cover the ask (the offer's own carrier dust
        // covers the item output). Select from the buyer's spendable wallet.
        var spending = services.GetRequiredService<ISpendingService>();
        var (funding, fundedSats) = SelectBuyerFunding(
            await spending.GetAvailableCoins(walletId, ct), offer.AskSats);
        if (fundedSats < offer.AskSats)
            throw new InvalidOperationException(
                $"Insufficient funds to fulfil offer {offer.OfferId}: need {offer.AskSats} sats, have {fundedSats}.");

        // The buyer always pays the sticker ask; when a marketplace fee is in force the covenant
        // SPLITS it — the seller absorbs the cut, so their payout is ask − fee and the treasury is
        // paid the rest at its own pinned output. Buyer change is identical either way.
        var offerDust = (long)offerVtxo.Amount;
        var buyerChange = offerDust + fundedSats - offer.AskSats;
        var fee = string.IsNullOrEmpty(offer.TreasuryFeeAddress) ? 0 : offer.FeeSats;

        byte[][] witness;
        TxOut[] outputs;
        ushort itemVout;
        if (fee > 0)
        {
            // Witness (bottom→top): [itemOutIdx=2, feeOutIdx=1, sellerOutIdx=0] — each PayTo's DUP
            // consumes the top index in script order (seller first, then treasury), and
            // AssetAtWitnessOutput consumes the item index beneath both.
            itemVout = 2;
            witness =
            [
                ArkadeCovenants.EncodeIndex(2), ArkadeCovenants.EncodeIndex(1), ArkadeCovenants.EncodeIndex(0),
            ];
            outputs =
            [
                new TxOut(Money.Satoshis(offer.AskSats - fee), sellerScript),                        // vout 0
                new TxOut(Money.Satoshis(fee), ArkAddress.Parse(offer.TreasuryFeeAddress!).ScriptPubKey), // vout 1
                new TxOut(Money.Satoshis(buyerChange), buyerScript),                                 // vout 2
            ];
        }
        else
        {
            // Witness (bottom→top): [itemOutIdx=1, payToOutIdx=0] — the item rides the
            // buyer's output 1; the seller is paid at output 0 (PayTo consumes the top).
            itemVout = 1;
            witness = [ArkadeCovenants.EncodeIndex(1), ArkadeCovenants.EncodeIndex(0)];
            outputs =
            [
                new TxOut(Money.Satoshis(offer.AskSats), sellerScript),  // seller paid (vout 0)
                new TxOut(Money.Satoshis(buyerChange), buyerScript),     // item carrier + change (vout 1)
            ];
        }

        // The item rides the buyer's output (group input at the offer vin 0), and so does anything
        // the buyer's own funding coins carry — see BuildFulfillPacket for why that is mandatory
        // rather than defensive on this code path.
        var packet = BuildFulfillPacket(
            offer.ItemAssetId, itemVout, funding, buyerChange, serverInfo.Dust.Satoshi);

        return await CovenantSpender.SpendManyCoreAsync(
            transport,
            services.GetRequiredService<ISafetyService>(),
            services.GetRequiredService<IWalletProvider>(),
            services.GetRequiredService<IIntentStorage>(),
            walletId, emulatorUri,
            [new CovenantSpender.CovenantInput(contract, "fulfill", witness, offerVtxo)],
            outputs,
            extraPackets: [packet],
            fundingCoins: funding,
            ct: ct);
    }

    /// <summary>
    /// Selects the buyer's funding coins, largest first until the ask is covered: PURE-BTC coins
    /// first, so an ordinary buy never drags a hero into the spend, then — only if those fall
    /// short — the buyer's asset CARRIERS. Returns the selection and its total sats; the caller
    /// rejects a shortfall. Recoverable coins (swept or past expiry) are EXCLUDED —
    /// arkd rejects a spend that includes one with <c>VTXO_RECOVERABLE</c> — using the
    /// SDK's fallback chain time (now, height 0), which catches both swept and
    /// time-expired coins; the same guard as <see cref="SelfCustodyWallet"/>'s selection.
    ///
    /// <para>The carrier fallback is the same fix the browser's <c>GameWallet</c> (#211) and the
    /// console's <see cref="SelfCustodyWallet"/> (#217) already carry, and it is needed for the
    /// same reason: a batch settle consolidates the whole wallet into ONE VTXO holding the sats
    /// AND every hero the player owns, after which there is no pure-BTC coin left and a player who
    /// owns any hero can never buy anything again — the buy fails with "need N sats, have 0" while
    /// the balance pill still reads the full amount. Whatever the chosen carriers hold is routed
    /// straight back to the buyer's own output by <see cref="BuildFulfillPacket"/>; unlike a plain
    /// wallet send there is no SDK-computed change output here to catch it, so selecting a carrier
    /// is only safe BECAUSE that packet names it.</para>
    /// </summary>
    public static (List<ArkCoin> Funding, long FundedSats) SelectBuyerFunding(
        IEnumerable<ArkCoin> coins, long askSats)
    {
        var now = new TimeHeight(DateTimeOffset.UtcNow, 0);
        var available = coins.Where(c => c.CanSpendOffchain(now)).ToList();
        var funding = new List<ArkCoin>();
        long fundedSats = 0;
        foreach (var coin in available.Where(c => c.Assets is null or { Count: 0 }).OrderByDescending(c => c.Amount)
                     .Concat(available.Where(c => c.Assets is { Count: > 0 }).OrderByDescending(c => c.Amount)))
        {
            if (fundedSats >= askSats) break;
            funding.Add(coin);
            fundedSats += coin.Amount.Satoshi;
        }
        return (funding, fundedSats);
    }

    /// <summary>
    /// The fulfil's asset packet: the ITEM from the offer input (vin 0) to the buyer's output, plus
    /// every asset unit riding the buyer's OWN funding coins routed back to that same output — which
    /// pays the buyer's address, so the buyer's heroes come home.
    ///
    /// <para>This is load-bearing, not defensive. <c>CovenantSpender.SpendManyCoreAsync</c> calls the
    /// SDK's <c>ConstructArkTransaction</c> DIRECTLY, which — unlike the <c>SpendingService</c> path the
    /// two wallet fixes run through — computes NO change output and builds NO asset packet of its own.
    /// The packet here is the whole story: an input asset this packet does not name has nowhere to land
    /// and no <c>AssetPacketBuilder</c> change vout to catch it. So the carrier fallback in
    /// <see cref="SelectBuyerFunding"/> MUST be paired with this, or the first buy funded by a
    /// consolidated wallet would put the buyer's own heroes into a transaction that does not account
    /// for them. The offer covenant does not constrain the group set — its <c>fulfill</c> leaf checks
    /// only the payout and <c>AssetAtWitnessOutput</c>, which looks the item up by (txid, issuance
    /// group index) at one output — so the extra groups are invisible to it.</para>
    ///
    /// <para>Refuses when the buyer's output would land below <paramref name="dustSats"/>: the SDK
    /// rewrites a sub-dust P2TR output into an OP_RETURN in place, which would burn the item and every
    /// hero riding the funding coins. Refusing a buy is always acceptable; losing a hero is not.</para>
    /// </summary>
    /// <param name="itemAssetId">The offered item, taken from the offer VTXO at vin 0.</param>
    /// <param name="itemVout">The buyer's output — carrier for the item, the buyer's change, and any
    /// asset riding the funding coins.</param>
    /// <param name="funding">The buyer's funding coins, in the order they are appended after the
    /// single covenant input, so funding coin <c>j</c> is vin <c>1 + j</c>.</param>
    /// <param name="buyerChangeSats">The sats value of the buyer's output.</param>
    /// <param name="dustSats">The operator's dust threshold.</param>
    internal static Packet BuildFulfillPacket(
        string itemAssetId, ushort itemVout, IReadOnlyList<ArkCoin> funding,
        long buyerChangeSats, long dustSats)
    {
        // Group per asset id, seeded with the item so a buyer who already owns units of the very
        // asset they are buying merges into that one group rather than tripping the packet's
        // duplicate-group rule. Ordinal-sorted, the order the SDK's own builder emits.
        var groups = new SortedDictionary<string, (List<AssetInput> Inputs, ulong Amount)>(StringComparer.Ordinal)
        {
            [itemAssetId] = ([AssetInput.Create(0, 1)], 1),
        };
        for (var j = 0; j < funding.Count; j++)
        {
            foreach (var asset in funding[j].Assets ?? [])
            {
                var group = groups.GetValueOrDefault(asset.AssetId, ([], 0));
                group.Inputs.Add(AssetInput.Create((ushort)(1 + j), asset.Amount));
                groups[asset.AssetId] = (group.Inputs, group.Amount + asset.Amount);
            }
        }

        if (funding.Any(c => c.Assets is { Count: > 0 }) && buyerChangeSats < dustSats)
            throw new InvalidOperationException(
                $"Refusing to fulfil: paying this offer would spend a coin carrying your own heroes, but it " +
                $"leaves only {buyerChangeSats} sats on your output — below the {dustSats}-sat dust floor, so " +
                $"that output would be replaced by an OP_RETURN and the heroes destroyed. Add a few more sats.");

        return Packet.Create(
        [
            .. groups.Select(g => AssetGroup.Create(
                AssetId.FromString(g.Key), null, g.Value.Inputs,
                [AssetOutput.Create(itemVout, g.Value.Amount)], [])),
        ]);
    }
}
