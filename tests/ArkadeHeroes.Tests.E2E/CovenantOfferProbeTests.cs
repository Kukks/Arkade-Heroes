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
    private const string EsploraApi = "http://localhost:8999/api/v1";

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

    /// <summary>Mints an asset to the seller via a covenant issuance (the proven rung-1 shape). supply=1 is the unique-hero shape.</summary>
    private async Task<string> MintItemToSellerAsync(
        NArk.Core.ArkServerInfo serverInfo, EmulatorInfo emulatorInfo, ulong supply = 100, string label = "rusty-blade")
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
                outputs: [AssetOutput.Create(0, supply)],
                metadata: new List<AssetMetadata> { AssetMetadata.Create("item", label) })])]);
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

    /// <summary>
    /// Exercises the multi-funding-coin path of the buyer-funded covenant spend:
    /// a buyer whose coins are each SMALLER than the ask must draw on more than
    /// one funding input, so the post-construction correction re-signs each
    /// funding coin's checkpoint + ark-tx input in its per-coin loop (the honest
    /// fulfil above only ever uses one). Proves that loop is correct.
    /// </summary>
    [Fact]
    public async Task RestingOffer_BuyerFundsFromMultipleCoins()
    {
        var transport = _seller.GetService<global::NArk.Core.Transport.IClientTransport>();
        var serverInfo = await transport.GetServerInfoAsync();
        var emulatorInfo = await new EmulatorClient(EmulatorUri).GetInfoAsync();
        var isMain = serverInfo.Network == Network.Main;
        const long ask = 8_000;

        // A buyer with TWO coins each below the ask → fulfilment needs both.
        await using var multiBuyer = await NewWalletAsync();
        await RegtestHelper.ArkSend(multiBuyer.Address, 5_000);
        await RegtestHelper.ArkSend(multiBuyer.Address, 5_000);
        await multiBuyer.WaitForBalanceAsync(10_000, TimeSpan.FromSeconds(60));

        var itemId = await MintItemToSellerAsync(serverInfo, emulatorInfo);
        var refundAfter = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var offer = new OfferParams(_seller.Address, itemId, ask, serverInfo.Dust.Satoshi, "offer-multi", refundAfter);
        var contract = OfferContracts.Build(offer, serverInfo.SignerKey, emulatorInfo.SignerPubkey);
        await _seller.SendAssetAsync(contract.GetArkAddress().ToString(isMain), itemId, 1);
        await CovenantSpender.WaitForVtxosAsync(_seller, contract, 1, TimeSpan.FromSeconds(45));

        var sellerBefore = await _seller.GetBalanceSatsAsync();

        await OfferFulfillFlow.FulfillAsync(multiBuyer, EmulatorUri, offer);

        await multiBuyer.WaitForAssetAsync(itemId, TimeSpan.FromSeconds(60));
        await _seller.WaitForBalanceAsync(sellerBefore + ask, TimeSpan.FromSeconds(60));
    }

    private async Task<ulong> SellerItemBalanceAsync(string itemId)
        => (await _seller.GetAssetsAsync()).Where(a => a.AssetId == itemId).Aggregate(0UL, (s, a) => s + a.Amount);

    /// <summary>
    /// The seller's liveness path: an UNSOLD offer is reclaimable after its
    /// window with no buyer and no server — the timelocked <c>reclaim</c> leaf
    /// returns the item to the seller (asset passthrough, gated on the chain
    /// clock, submitted exactly once per the poisoned-txid discipline). This is
    /// the one covenant path the marketplace shipped that had only InMemory +
    /// client-dispatch coverage; here it is proven live.
    /// </summary>
    [Fact]
    public async Task RestingOffer_SellerReclaimsUnsoldItemAfterExpiry()
    {
        var transport = _seller.GetService<global::NArk.Core.Transport.IClientTransport>();
        var serverInfo = await transport.GetServerInfoAsync();
        var emulatorInfo = await new EmulatorClient(EmulatorUri).GetInfoAsync();
        var isMain = serverInfo.Network == Network.Main;
        const long ask = 8_000;

        var itemId = await MintItemToSellerAsync(serverInfo, emulatorInfo);

        // Short reclaim window so the unsold offer becomes reclaimable in-test.
        var refundAfter = DateTimeOffset.UtcNow.AddSeconds(8).ToUnixTimeSeconds();
        var offer = new OfferParams(_seller.Address, itemId, ask, serverInfo.Dust.Satoshi, "offer-reclaim", refundAfter);
        var contract = OfferContracts.Build(offer, serverInfo.SignerKey, emulatorInfo.SignerPubkey);
        await _seller.SendAssetAsync(contract.GetArkAddress().ToString(isMain), itemId, 1);
        await CovenantSpender.WaitForVtxosAsync(_seller, contract, 1, TimeSpan.FromSeconds(45));

        // The item unit now sits in the offer, not the seller's wallet.
        var heldAfterResting = await SellerItemBalanceAsync(itemId);

        using var esploraHttp = new HttpClient();

        // Pre-expiry: the flow refuses WITHOUT submitting (the canonical reclaim
        // txid must never see a refused submission — arkd poisons it).
        await Assert.ThrowsAsync<RefundNotYetDueException>(() => OfferReclaimFlow.ReclaimAsync(
            _seller, EmulatorUri, offer,
            ct => EsploraChainTime.GetMedianTimeAsync(esploraHttp, EsploraApi, ct)));

        // Let the CHAIN's clock pass expiry, then reclaim once.
        await RegtestHelper.WaitForChainTimeAsync(refundAfter, TimeSpan.FromSeconds(120));
        var reclaim = await OfferReclaimFlow.ReclaimAsync(
            _seller, EmulatorUri, offer,
            ct => EsploraChainTime.GetMedianTimeAsync(esploraHttp, EsploraApi, ct));
        Assert.False(string.IsNullOrEmpty(reclaim.SignedArkTx));

        // The item unit returned to the seller's wallet.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
        while (DateTime.UtcNow < deadline && await SellerItemBalanceAsync(itemId) < heldAfterResting + 1)
            await Task.Delay(1000);
        Assert.True(await SellerItemBalanceAsync(itemId) >= heldAfterResting + 1,
            "the reclaimed item unit did not return to the seller");
    }

    /// <summary>
    /// Hero sales reuse the SAME offer covenant (it only ever sees an asset id).
    /// This proves the hero shape live: a UNIQUE supply-1 asset (a character) is
    /// rested whole and bought — the seller ends holding NONE of it (unlike a
    /// fungible item, where they keep the rest), and the buyer receives the exact
    /// same asset id, metadata intact (the passthrough never re-mints).
    /// </summary>
    [Fact]
    public async Task RestingOffer_SellsAWholeUniqueAsset_TheHeroShape()
    {
        var transport = _seller.GetService<global::NArk.Core.Transport.IClientTransport>();
        var serverInfo = await transport.GetServerInfoAsync();
        var emulatorInfo = await new EmulatorClient(EmulatorUri).GetInfoAsync();
        var isMain = serverInfo.Network == Network.Main;
        const long ask = 12_000;

        // A supply-1 asset — the unique-hero shape (vs. the supply-100 item above).
        var heroAssetId = await MintItemToSellerAsync(serverInfo, emulatorInfo, supply: 1, label: "hero");
        Assert.Equal(1UL, await SellerItemBalanceAsync(heroAssetId));

        var refundAfter = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var offer = new OfferParams(_seller.Address, heroAssetId, ask, serverInfo.Dust.Satoshi, "offer-hero", refundAfter);
        var contract = OfferContracts.Build(offer, serverInfo.SignerKey, emulatorInfo.SignerPubkey);
        // The seller rests the WHOLE unique asset (its only unit).
        await _seller.SendAssetAsync(contract.GetArkAddress().ToString(isMain), heroAssetId, 1);
        await CovenantSpender.WaitForVtxosAsync(_seller, contract, 1, TimeSpan.FromSeconds(45));

        var sellerBefore = await _seller.GetBalanceSatsAsync();

        await OfferFulfillFlow.FulfillAsync(_buyer, EmulatorUri, offer);

        // The buyer holds the exact hero asset; the seller was paid; the seller
        // no longer holds ANY of the unique asset (it changed owner wholesale).
        await _buyer.WaitForAssetAsync(heroAssetId, TimeSpan.FromSeconds(60));
        await _seller.WaitForBalanceAsync(sellerBefore + ask, TimeSpan.FromSeconds(60));
        Assert.Equal(0UL, await SellerItemBalanceAsync(heroAssetId));
    }
}
