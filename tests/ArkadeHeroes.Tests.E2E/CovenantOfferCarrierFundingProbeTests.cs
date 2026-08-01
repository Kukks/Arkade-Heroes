using ArkadeHeroes.Chain.Covenants;
using ArkadeHeroes.Chain.NArk;
using NArk.Core.Assets;
using NBitcoin;

namespace ArkadeHeroes.Tests.E2E;

/// <summary>
/// The buy path for a player who already owns a hero — live against arkd + the emulator.
///
/// <para>A batch settle consolidates a wallet into VTXOs that carry the sats AND the player's
/// heroes, so the coin a buyer must pay with is an asset CARRIER. The old funding selector
/// dropped every carrier, and a player who owned any hero could not buy anything at all
/// ("need N sats, have 0" against a full balance pill). Selecting a carrier is only safe
/// because <c>OfferFulfillFlow.BuildFulfillPacket</c> names the carried assets and routes them
/// to the buyer's own output: this covenant spend calls the SDK's <c>ConstructArkTransaction</c>
/// directly, which builds no asset packet and computes no change output of its own, so an input
/// asset the packet omits has nowhere to land.</para>
///
/// <para>Unit tests pin the packet's shape; only this proves arkd and the emulator accept it and
/// that the buyer's own hero survives the purchase in the buyer's wallet — and not the
/// seller's.</para>
/// </summary>
public class CovenantOfferCarrierFundingProbeTests : IAsyncLifetime
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
        await RegtestHelper.ArkSend(_buyer.Address, 60_000);
        await _seller.WaitForBalanceAsync(50_000, TimeSpan.FromSeconds(60));
        await _buyer.WaitForBalanceAsync(60_000, TimeSpan.FromSeconds(60));
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
        var dbPath = Path.Combine(Path.GetTempPath(), $"ah-carrier-{Guid.NewGuid():N}.db");
        _dbPaths.Add(dbPath);
        return await SelfCustodyWallet.CreateAsync(new SelfCustodyWalletOptions
        {
            ArkUri = "http://localhost:7070",
            DbPath = dbPath,
        });
    }

    /// <summary>
    /// Mints one asset unit to <paramref name="owner"/> on a carrier worth <paramref name="carrierSats"/>
    /// — the covenant issuance shape, whose output is a single VTXO holding those sats AND the new
    /// asset. That is exactly the post-settle consolidated coin this test needs the buyer to own.
    /// <paramref name="mintScript"/> must differ per mint: the contract's address derives from the
    /// tweaked leaf script, NOT from its name, so two mints sharing a script share an address.
    /// </summary>
    private async Task<string> MintCarrierAsync(
        SelfCustodyWallet owner, NArk.Core.ArkServerInfo serverInfo, EmulatorInfo emulatorInfo,
        long carrierSats, string label, byte[] mintScript)
    {
        var ownerScript = global::NArk.Abstractions.ArkAddress.Parse(owner.Address).ScriptPubKey;
        var mint = new ArkadeArtifactContract($"carrier-mint-{label}", serverInfo.SignerKey, emulatorInfo.SignerPubkey,
            [new ArkadeContractFunction("mint", mintScript)]);
        await owner.SendAsync(mint.GetArkAddress().ToString(serverInfo.Network == Network.Main), carrierSats);
        var vtxo = await CovenantSpender.WaitForVtxoAsync(owner, mint, TimeSpan.FromSeconds(45));
        var response = await CovenantSpender.SpendManyAsync(
            owner, EmulatorUri,
            [new CovenantSpender.CovenantInput(mint, "mint", [], vtxo)],
            [new TxOut(Money.Satoshis(carrierSats), ownerScript)],
            extraPackets: [Packet.Create([AssetGroup.Create(
                assetId: null, controlAsset: null, inputs: [],
                outputs: [AssetOutput.Create(0, 1)],
                metadata: new List<AssetMetadata> { AssetMetadata.Create("item", label) })])]);
        var assetId = AssetId.Create(
            PSBT.Parse(response.SignedArkTx, serverInfo.Network).GetGlobalTransaction().GetHash().ToString(), 0).ToString();
        await owner.WaitForAssetAsync(assetId, TimeSpan.FromSeconds(45));
        return assetId;
    }

    private static async Task<ulong> HeldAsync(SelfCustodyWallet wallet, string assetId)
        => (await wallet.GetAssetsAsync()).Where(a => a.AssetId == assetId).Aggregate(0UL, (s, a) => s + a.Amount);

    [Fact]
    public async Task BuyerFundedFromAHeroCarrierBuys_AndKeepsTheirOwnHero()
    {
        var transport = _seller.GetService<global::NArk.Core.Transport.IClientTransport>();
        var serverInfo = await transport.GetServerInfoAsync();
        var emulatorInfo = await new EmulatorClient(EmulatorUri).GetInfoAsync();
        var isMain = serverInfo.Network == Network.Main;
        const long ask = 25_000, offerDust = 2_000;

        // The seller's stock. OP_1.
        var itemId = await MintCarrierAsync(_seller, serverInfo, emulatorInfo, 15_000, "rusty-blade", [0x51]);

        // The buyer's OWN hero, on a 45,000-sat carrier — the consolidated coin. OP_1 OP_DROP OP_1,
        // a different script so this mint gets its own address.
        var buyerHeroId = await MintCarrierAsync(
            _buyer, serverInfo, emulatorInfo, 45_000, "buyers-own-hero", [0x51, 0x75, 0x51]);
        Assert.Equal(1UL, await HeldAsync(_buyer, buyerHeroId));

        // The premise, asserted rather than assumed: the buyer's pure-BTC coins cannot cover the
        // ask on their own, so this buy is only fundable by spending the hero's carrier.
        var spending = _buyer.GetService<global::NArk.Core.Services.ISpendingService>();
        var coins = await spending.GetAvailableCoins(_buyer.WalletId, CancellationToken.None);
        var pureBtc = coins.Where(c => c.Assets is null or { Count: 0 }).Sum(c => c.Amount.Satoshi);
        Assert.True(pureBtc < ask, $"premise broken: buyer holds {pureBtc} pure-BTC sats, ask is {ask}");
        var (funding, fundedSats) = OfferFulfillFlow.SelectBuyerFunding(coins, ask);
        Assert.True(fundedSats >= ask, $"selector left the ask unfunded: {fundedSats} < {ask}");
        Assert.Contains(funding, c => c.Assets is { Count: > 0 });

        // The seller rests the offer.
        var refundAfter = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var offer = new OfferParams(_seller.Address, itemId, ask, offerDust, "offer-carrier-e2e", refundAfter);
        var contract = OfferContracts.Build(offer, serverInfo.SignerKey, emulatorInfo.SignerPubkey);
        await _seller.SendAssetAsync(contract.GetArkAddress().ToString(isMain), itemId, 1);
        await CovenantSpender.WaitForVtxosAsync(_seller, contract, 1, TimeSpan.FromSeconds(45));

        var sellerBefore = await _seller.GetBalanceSatsAsync();

        // The buy — funded by the carrier, co-signed by the emulator, accepted by arkd.
        await OfferFulfillFlow.FulfillAsync(_buyer, EmulatorUri, offer);

        // The buyer got what they paid for...
        await _buyer.WaitForAssetAsync(itemId, TimeSpan.FromSeconds(60));
        await _seller.WaitForBalanceAsync(sellerBefore + ask, TimeSpan.FromSeconds(60));

        // ...and — the whole point — did NOT pay with their own hero.
        await _buyer.SyncAsync();
        await _seller.SyncAsync();
        Assert.Equal(1UL, await HeldAsync(_buyer, buyerHeroId));
        Assert.Equal(0UL, await HeldAsync(_seller, buyerHeroId));
    }
}
