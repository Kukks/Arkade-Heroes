using ArkadeHeroes.Chain.Covenants;
using ArkadeHeroes.Chain.NArk;
using NArk.Core.Assets;
using NBitcoin;

namespace ArkadeHeroes.Tests.E2E;

/// <summary>
/// The item marketplace live on regtest (banco's non-interactive-swap shape):
/// a seller rests an offer VTXO carrying an item asset; ANYONE may fulfil it,
/// but only by paying the seller the exact ask in the same transaction — funded
/// from the BUYER's own wallet coins (CovenantSpender extended to carry actor
/// funding inputs; after ConstructArkTransaction reorders inputs by BIP69 the
/// EmulatorPacket is rebuilt post-construction with corrected vins so it survives
/// NArk's asset-vin remap, then the funding inputs are re-signed over the fixed
/// outputs). Underpayment is refused; an honest fulfil pays the seller and
/// delivers the item to the buyer.
/// </summary>
public class CovenantOfferProbeTests : IAsyncLifetime
{
    private static readonly Uri EmulatorUri = new("http://localhost:7073/");

    private SelfCustodyWallet _seller = null!;
    private SelfCustodyWallet _buyer = null!;
    private readonly List<string> _dbPaths = [];

    public async Task InitializeAsync()
    {
        await RegtestHelper.WaitForArkdReadyAsync(TimeSpan.FromSeconds(30));
        _seller = await NewWalletAsync();
        _buyer = await NewWalletAsync();
        await RegtestHelper.ArkSend(_seller.Address, 50_000);
        await RegtestHelper.ArkSend(_buyer.Address, 50_000);
        await _seller.WaitForBalanceAsync(50_000, TimeSpan.FromSeconds(60));
        await _buyer.WaitForBalanceAsync(50_000, TimeSpan.FromSeconds(60));
    }

    public async Task DisposeAsync()
    {
        await _seller.DisposeAsync();
        await _buyer.DisposeAsync();
        foreach (var p in _dbPaths)
            try { if (File.Exists(p)) File.Delete(p); } catch { /* windows lock */ }
    }

    private async Task<SelfCustodyWallet> NewWalletAsync()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"ah-offer-{Guid.NewGuid():N}.db");
        _dbPaths.Add(dbPath);
        return await SelfCustodyWallet.CreateAsync(new SelfCustodyWalletOptions
        {
            ArkUri = "http://localhost:7070",
            DbPath = dbPath,
        });
    }

    /// <summary>Mints a fungible "item" asset to the seller via a covenant issuance (the proven rung-1 shape).</summary>
    private async Task<string> MintItemToSellerAsync(NArk.Core.ArkServerInfo serverInfo, EmulatorInfo emulatorInfo)
    {
        var sellerScript = global::NArk.Abstractions.ArkAddress.Parse(_seller.Address).ScriptPubKey;
        var mint = new ArkadeArtifactContract("offer-item-mint", serverInfo.SignerKey, emulatorInfo.SignerPubkey,
            [new ArkadeContractFunction("mint", [0x51])]);
        await _seller.SendAsync(mint.GetArkAddress().ToString(serverInfo.Network == Network.Main), 15_000);
        var vtxo = await CovenantSpender.WaitForVtxoAsync(_seller, mint, TimeSpan.FromSeconds(45));
        var response = await CovenantSpender.SpendManyAsync(
            _seller, EmulatorUri,
            [new CovenantSpender.CovenantInput(mint, "mint", [], vtxo)],
            [new TxOut(Money.Satoshis(15_000), sellerScript)],
            extraPackets: [Packet.Create([AssetGroup.Create(
                assetId: null, controlAsset: null, inputs: [],
                outputs: [AssetOutput.Create(0, 100)],
                metadata: new List<AssetMetadata> { AssetMetadata.Create("item", "rusty-blade") })])]);
        var itemId = AssetId.Create(
            PSBT.Parse(response.SignedArkTx, serverInfo.Network).GetGlobalTransaction().GetHash().ToString(), 0).ToString();
        await _seller.WaitForAssetAsync(itemId, TimeSpan.FromSeconds(45));
        return itemId;
    }

    [Fact]
    public async Task RestingOffer_BuyerFundsAndTakesItem()
    {
        var transport = _seller.GetService<global::NArk.Core.Transport.IClientTransport>();
        var serverInfo = await transport.GetServerInfoAsync();
        var emulatorInfo = await new EmulatorClient(EmulatorUri).GetInfoAsync();
        var isMain = serverInfo.Network == Network.Main;
        const long ask = 8_000, offerDust = 2_000;

        var itemId = await MintItemToSellerAsync(serverInfo, emulatorInfo);

        // The seller rests an offer: deposits one item unit + carrier dust.
        var refundAfter = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var offer = new OfferParams(_seller.Address, itemId, ask, offerDust, "offer-e2e", refundAfter);
        var contract = OfferContracts.Build(offer, serverInfo.SignerKey, emulatorInfo.SignerPubkey);
        await _seller.SendAssetAsync(contract.GetArkAddress().ToString(isMain), itemId, 1);
        await CovenantSpender.WaitForVtxosAsync(_seller, contract, 1, TimeSpan.FromSeconds(45));

        var sellerBefore = await _seller.GetBalanceSatsAsync();

        // Honest fulfil via the buyer flow: the buyer funds the ask from their
        // own wallet, pays the seller, and takes the item.
        await OfferFulfillFlow.FulfillAsync(_buyer, EmulatorUri, offer);
        await _buyer.WaitForAssetAsync(itemId, TimeSpan.FromSeconds(60));
        await _seller.WaitForBalanceAsync(sellerBefore + ask, TimeSpan.FromSeconds(60));
    }

    /// <summary>
    /// The covenant's core fairness guarantee for the buyer-funded item shape: a
    /// greedy buyer who funds the transaction but pays the seller LESS than the
    /// ask cannot get the emulator to co-sign — the offer is unspendable except
    /// on the seller's terms. (The sats-only shape is covered by
    /// OfferFulfillCovenantTests; this proves it holds with buyer funding + an
    /// asset passthrough, where the input-order correction also runs.)
    /// </summary>
    [Fact]
    public async Task RestingOffer_UnderpaymentIsRefused()
    {
        var transport = _seller.GetService<global::NArk.Core.Transport.IClientTransport>();
        var serverInfo = await transport.GetServerInfoAsync();
        var emulatorInfo = await new EmulatorClient(EmulatorUri).GetInfoAsync();
        var isMain = serverInfo.Network == Network.Main;
        const long ask = 8_000;

        var itemId = await MintItemToSellerAsync(serverInfo, emulatorInfo);

        var refundAfter = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var offer = new OfferParams(_seller.Address, itemId, ask, serverInfo.Dust.Satoshi, "offer-underpay", refundAfter);
        var contract = OfferContracts.Build(offer, serverInfo.SignerKey, emulatorInfo.SignerPubkey);
        await _seller.SendAssetAsync(contract.GetArkAddress().ToString(isMain), itemId, 1);
        var offerVtxo = (await CovenantSpender.WaitForVtxosAsync(_buyer, contract, 1, TimeSpan.FromSeconds(45)))
            .First(v => v.Assets?.Any(a => a.AssetId == itemId) == true);

        // The greedy buyer funds the tx from their own wallet but shorts the
        // seller by 1_000 sats, keeping the difference as change.
        var sellerScript = global::NArk.Abstractions.ArkAddress.Parse(_seller.Address).ScriptPubKey;
        var buyerScript = global::NArk.Abstractions.ArkAddress.Parse(_buyer.Address).ScriptPubKey;
        var item = AssetId.FromString(itemId);
        var spending = _buyer.GetService<global::NArk.Core.Services.ISpendingService>();
        var funding = (await spending.GetAvailableCoins(_buyer.WalletId, CancellationToken.None))
            .Where(c => c.Assets is null or { Count: 0 })
            .OrderByDescending(c => c.Amount).Take(1).ToList();
        var offerDust = (long)offerVtxo.Amount;
        var fundedSats = funding.Sum(c => c.Amount.Satoshi);
        const long shortfall = 1_000;
        var packet = Packet.Create(
            [AssetGroup.Create(item, null, [AssetInput.Create(0, 1)], [AssetOutput.Create(1, 1)], [])]);

        var underpay = await Assert.ThrowsAnyAsync<Exception>(() => CovenantSpender.SpendManyAsync(
            _buyer, EmulatorUri,
            [new CovenantSpender.CovenantInput(contract, "fulfill", [ArkadeCovenants.EncodeIndex(0)], offerVtxo)],
            [
                new TxOut(Money.Satoshis(ask - shortfall), sellerScript),                       // seller SHORTED
                new TxOut(Money.Satoshis(offerDust + fundedSats - (ask - shortfall)), buyerScript),
            ],
            extraPackets: [packet],
            fundingCoins: funding));
        Assert.Contains("Emulator rejected", underpay.Message);

        // And the buyer walked away with nothing — the item never moved.
        Assert.DoesNotContain(await _buyer.GetAssetsAsync(), a => a.AssetId == itemId && a.Amount > 0);
    }
}
