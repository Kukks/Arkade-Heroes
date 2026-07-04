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
}
