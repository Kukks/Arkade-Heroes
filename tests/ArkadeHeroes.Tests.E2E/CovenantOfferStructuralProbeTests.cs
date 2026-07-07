using ArkadeHeroes.Chain.Covenants;
using ArkadeHeroes.Chain.NArk;
using NArk.Abstractions;
using NArk.Abstractions.Assets;
using NArk.Core.Assets;
using NArk.Core.Services;
using NBitcoin;

namespace ArkadeHeroes.Tests.E2E;

/// <summary>
/// The structural TEETH of the offer covenant-v2 leaves, live on regtest. The existing
/// <see cref="CovenantOfferProbeTests"/>/<see cref="OfferFulfillCovenantTests"/> prove the
/// honest paths through the real flows; this proves the emulator REFUSES the cheats the old
/// PayTo-only leaves allowed:
///   • fulfil-BURN — pay the seller the exact ask but destroy the item (input, no output) →
///     AssetAtWitnessOutput refused;
///   • reclaim-THEFT (the headline) — post-expiry, pay the offer value but route it (and the
///     item) to a THIEF instead of the seller → AssetAtOutput's 0xd1 refused. Under the OLD
///     sats-only reclaim leaf this stole a hero/item for ~330 dust.
/// The honest fulfil and honest post-expiry reclaim (through the REAL flows) co-sign.
/// </summary>
public class CovenantOfferStructuralProbeTests : IAsyncLifetime
{
    private static readonly Uri EmulatorUri = new("http://localhost:7073/");
    private const string EsploraApi = "http://localhost:8999/api/v1";
    private const long Ask = 5_000;

    private SelfCustodyWallet _seller = null!;
    private SelfCustodyWallet _buyer = null!;
    private SelfCustodyWallet _thief = null!;
    private readonly List<string> _dbPaths = [];

    public async Task InitializeAsync()
    {
        await RegtestHelper.WaitForArkdReadyAsync(TimeSpan.FromSeconds(30));
        _seller = await NewWalletAsync();
        _buyer = await NewWalletAsync();
        _thief = await NewWalletAsync();
        await RegtestHelper.ArkSend(_seller.Address, 200_000);
        await RegtestHelper.ArkSend(_buyer.Address, 100_000);
        await _seller.WaitForBalanceAsync(200_000, TimeSpan.FromSeconds(60));
        await _buyer.WaitForBalanceAsync(100_000, TimeSpan.FromSeconds(60));
    }

    private async Task<SelfCustodyWallet> NewWalletAsync()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"ah-offerstruct-{Guid.NewGuid():N}.db");
        _dbPaths.Add(dbPath);
        return await SelfCustodyWallet.CreateAsync(new SelfCustodyWalletOptions
        {
            ArkUri = "http://localhost:7070",
            DbPath = dbPath,
        });
    }

    public async Task DisposeAsync()
    {
        await _seller.DisposeAsync();
        await _buyer.DisposeAsync();
        await _thief.DisposeAsync();
        foreach (var p in _dbPaths)
            try { if (File.Exists(p)) File.Delete(p); } catch { /* windows lock */ }
    }

    private async Task<string> IssueItemAsync()
    {
        var mgr = _seller.GetService<IAssetManager>();
        var res = await mgr.IssueAsync(_seller.WalletId, new IssuanceParams(Amount: 1));
        await _seller.WaitForAssetAsync(res.AssetId, TimeSpan.FromSeconds(30));
        return res.AssetId;
    }

    [Fact]
    public async Task Fulfil_BurningTheItemRefused_HonestFulfilCoSigned()
    {
        var transport = _seller.GetService<global::NArk.Core.Transport.IClientTransport>();
        var serverInfo = await transport.GetServerInfoAsync();
        var emulatorInfo = await new EmulatorClient(EmulatorUri).GetInfoAsync();
        var isMain = serverInfo.Network == Network.Main;

        var itemId = await IssueItemAsync();
        var item = AssetId.FromString(itemId);
        var sellerScript = ArkAddress.Parse(_seller.Address).ScriptPubKey;
        var buyerScript = ArkAddress.Parse(_buyer.Address).ScriptPubKey;

        var refundAfter = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var offer = new OfferParams(_seller.Address, itemId, Ask, serverInfo.Dust.Satoshi, "offer-struct-fulfil", refundAfter);
        var contract = OfferContracts.Build(offer, serverInfo.SignerKey, emulatorInfo.SignerPubkey);
        await _seller.SendAssetAsync(contract.GetArkAddress().ToString(isMain), itemId, 1);
        var offerVtxo = (await CovenantSpender.WaitForVtxosAsync(_buyer, contract, 1, TimeSpan.FromSeconds(45)))
            .First(v => v.Assets?.Any(a => a.AssetId == itemId) == true);

        // The cheat needs the buyer's funding coins for the ask (as the real flow selects them).
        var spending = _buyer.GetService<ISpendingService>();
        var funding = new List<ArkCoin>();
        long fundedSats = 0;
        foreach (var coin in (await spending.GetAvailableCoins(_buyer.WalletId))
                     .Where(c => c.Assets is null or { Count: 0 }).OrderByDescending(c => c.Amount))
        {
            funding.Add(coin);
            fundedSats += coin.Amount.Satoshi;
            if (fundedSats >= Ask) break;
        }
        Assert.True(fundedSats >= Ask, "buyer lacks funds for the cheat attempt");
        var offerDust = (long)offerVtxo.Amount;
        var buyerChange = offerDust + fundedSats - Ask;

        // ── Cheat: pay the seller the EXACT ask but BURN the item (input, no output).
        //    Under the old PayTo-only fulfil this co-signed; AssetAtWitnessOutput refuses it.
        var burn = await Assert.ThrowsAnyAsync<Exception>(() => CovenantSpender.SpendManyAsync(
            _buyer, EmulatorUri,
            [new CovenantSpender.CovenantInput(contract, "fulfill",
                [ArkadeCovenants.EncodeIndex(1), ArkadeCovenants.EncodeIndex(0)], offerVtxo)],
            [
                new TxOut(Money.Satoshis(Ask), sellerScript),        // seller paid in full (vout 0)
                new TxOut(Money.Satoshis(buyerChange), buyerScript), // change only — NO item (vout 1)
            ],
            extraPackets: [Packet.Create(
                [AssetGroup.Create(item, null, [AssetInput.Create(0, 1)], [], [])])],   // burned
            fundingCoins: funding));
        Assert.Contains("Emulator rejected", burn.Message);

        // ── Honest fulfil through the REAL flow → co-signed, buyer receives the item.
        var response = await OfferFulfillFlow.FulfillAsync(_buyer, EmulatorUri, offer);
        Assert.False(string.IsNullOrEmpty(response.SignedArkTx));
        await _buyer.WaitForAssetAsync(itemId, TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task Reclaim_TheftAndPreExpiryRefused_HonestReclaimCoSigned()
    {
        var transport = _seller.GetService<global::NArk.Core.Transport.IClientTransport>();
        var serverInfo = await transport.GetServerInfoAsync();
        var emulatorInfo = await new EmulatorClient(EmulatorUri).GetInfoAsync();
        var isMain = serverInfo.Network == Network.Main;

        var itemId = await IssueItemAsync();
        var item = AssetId.FromString(itemId);
        var sellerScript = ArkAddress.Parse(_seller.Address).ScriptPubKey;
        var thiefScript = ArkAddress.Parse(_thief.Address).ScriptPubKey;

        var refundAfter = DateTimeOffset.UtcNow.AddSeconds(8).ToUnixTimeSeconds();
        var offer = new OfferParams(_seller.Address, itemId, Ask, serverInfo.Dust.Satoshi, "offer-struct-reclaim", refundAfter);
        var contract = OfferContracts.Build(offer, serverInfo.SignerKey, emulatorInfo.SignerPubkey);
        await _seller.SendAssetAsync(contract.GetArkAddress().ToString(isMain), itemId, 1);
        // The THIEF observes the resting offer (script-addressed — anyone can).
        var offerVtxo = (await CovenantSpender.WaitForVtxosAsync(_thief, contract, 1, TimeSpan.FromSeconds(45)))
            .First(v => v.Assets?.Any(a => a.AssetId == itemId) == true);
        var offerDust = (long)offerVtxo.Amount;

        Packet ItemToOut0() => Packet.Create(
            [AssetGroup.Create(item, null, [AssetInput.Create(0, 1)], [AssetOutput.Create(0, 1)], [])]);

        // ── Pre-expiry: refused by arkd's forfeit-closure gate. Disposable non-canonical
        //    locktime (expiry+1) so the refusal never poisons the canonical reclaim txid.
        var early = await Assert.ThrowsAnyAsync<Exception>(() => CovenantSpender.SpendManyAsync(
            _thief, EmulatorUri,
            [new CovenantSpender.CovenantInput(contract, "reclaim", [], offerVtxo,
                LockTime: new LockTime((uint)(refundAfter + 1)))],
            [new TxOut(Money.Satoshis(offerDust), sellerScript)],
            extraPackets: [ItemToOut0()]));
        Assert.False(early is TimeoutException, $"expected a refusal, got: {early.Message}");

        await RegtestHelper.WaitForChainTimeAsync(refundAfter, TimeSpan.FromSeconds(120));

        // ── THE THEFT (the headline): post-expiry, the thief routes output 0 — the item and
        //    its carrier — to THEMSELVES. Under the OLD sats-only reclaim leaf, paying the
        //    seller ~330 dust elsewhere let this steal the item; AssetAtOutput's 0xd1
        //    output-script pin now refuses it. (Thief-paying output → distinct txid, disposable.)
        var theft = await Assert.ThrowsAnyAsync<Exception>(() => CovenantSpender.SpendManyAsync(
            _thief, EmulatorUri,
            [new CovenantSpender.CovenantInput(contract, "reclaim", [], offerVtxo,
                LockTime: new LockTime((uint)refundAfter))],
            [new TxOut(Money.Satoshis(offerDust), thiefScript)],
            extraPackets: [ItemToOut0()]));
        Assert.Contains("Emulator rejected", theft.Message);

        // ── Honest reclaim through the REAL flow (submit exactly ONCE) → co-signed,
        //    the item returns to the seller.
        using var esploraHttp = new HttpClient();
        var reclaim = await OfferReclaimFlow.ReclaimAsync(
            _seller, EmulatorUri, offer,
            ct => EsploraChainTime.GetMedianTimeAsync(esploraHttp, EsploraApi, ct));
        Assert.False(string.IsNullOrEmpty(reclaim.SignedArkTx));
        await _seller.WaitForAssetAsync(itemId, TimeSpan.FromSeconds(90));
    }
}
