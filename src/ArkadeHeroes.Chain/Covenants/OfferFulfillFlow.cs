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
        var item = AssetId.FromString(offer.ItemAssetId);

        // The buyer's funding coins cover the ask (the offer's own carrier dust
        // covers the item output). Select from the buyer's spendable wallet.
        var spending = services.GetRequiredService<ISpendingService>();
        var (funding, fundedSats) = SelectBuyerFunding(
            await spending.GetAvailableCoins(walletId, ct), offer.AskSats);
        if (fundedSats < offer.AskSats)
            throw new InvalidOperationException(
                $"Insufficient funds to fulfil offer {offer.OfferId}: need {offer.AskSats} sats, have {fundedSats}.");

        // Outputs: [0] seller gets the ask; [1] buyer gets the item carrier +
        // change (offer dust + funding − ask). The fulfill covenant pins output 0.
        var offerDust = (long)offerVtxo.Amount;
        var buyerChange = offerDust + fundedSats - offer.AskSats;
        // The item rides the buyer's output (group input at the offer vin 0).
        var packet = Packet.Create(
        [
            AssetGroup.Create(item, null,
                [AssetInput.Create(0, 1)], [AssetOutput.Create(1, 1)], []),
        ]);

        return await CovenantSpender.SpendManyCoreAsync(
            transport,
            services.GetRequiredService<ISafetyService>(),
            services.GetRequiredService<IWalletProvider>(),
            services.GetRequiredService<IIntentStorage>(),
            walletId, emulatorUri,
            // Witness (bottom→top): [itemOutIdx=1, payToOutIdx=0] — the item rides the
            // buyer's output 1; the seller is paid at output 0 (PayTo consumes the top).
            [new CovenantSpender.CovenantInput(contract, "fulfill",
                [ArkadeCovenants.EncodeIndex(1), ArkadeCovenants.EncodeIndex(0)], offerVtxo)],
            [
                new TxOut(Money.Satoshis(offer.AskSats), sellerScript),  // seller paid (vout 0)
                new TxOut(Money.Satoshis(buyerChange), buyerScript),     // item carrier + change (vout 1)
            ],
            extraPackets: [packet],
            fundingCoins: funding,
            ct: ct);
    }

    /// <summary>
    /// Selects the buyer's pure-BTC funding coins (no asset carriers), largest first
    /// until the ask is covered. Returns the selection and its total sats; the caller
    /// rejects a shortfall. Recoverable coins (swept or past expiry) are EXCLUDED —
    /// arkd rejects a spend that includes one with <c>VTXO_RECOVERABLE</c> — using the
    /// SDK's fallback chain time (now, height 0), which catches both swept and
    /// time-expired coins; the same guard as <see cref="SelfCustodyWallet"/>'s selection.
    /// </summary>
    public static (List<ArkCoin> Funding, long FundedSats) SelectBuyerFunding(
        IEnumerable<ArkCoin> coins, long askSats)
    {
        var now = new TimeHeight(DateTimeOffset.UtcNow, 0);
        var available = coins
            .Where(c => c.CanSpendOffchain(now))
            .Where(c => c.Assets is null or { Count: 0 })
            .OrderByDescending(c => c.Amount)
            .ToList();
        var funding = new List<ArkCoin>();
        long fundedSats = 0;
        foreach (var coin in available)
        {
            funding.Add(coin);
            fundedSats += coin.Amount.Satoshi;
            if (fundedSats >= askSats) break;
        }
        return (funding, fundedSats);
    }
}
