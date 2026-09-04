using ArkadeHeroes.Chain.Covenants;
using ArkadeHeroes.Chain.NArk;
using NArk.Abstractions;
using NArk.Abstractions.Assets;
using NArk.Core.Assets;
using NArk.Core.Services;
using NBitcoin;

namespace ArkadeHeroes.Tests.E2E;

/// <summary>
/// GO/NO-GO for collecting the marketplace fee IN THE COVENANT instead of as a separate invoice the
/// seller pre-pays. Breed and merge have always routed their treasury cut structurally
/// (BreedRetainAuthorized); the offer covenant never had a fee leg, which is the only reason the
/// listing fee was bolted on as a server-side invoice — and that invoice is what could strand a
/// deposited hero in a listing that never went live.
///
/// The whole design rests on one unproven assumption: that the emulator will enforce TWO PayTo legs
/// in a single leaf. Nothing in this codebase has ever asked it to. If it will not, the approach
/// pivots HERE, before any flow or UI is wired to it.
///
/// Proves three things about the fee-bearing fulfil leaf:
///   • skipping the treasury entirely (pay the seller the FULL ask, the pre-fee shape) → REFUSED;
///   • paying the fee to the BUYER instead of the treasury → REFUSED (the treasury leg is
///     SCRIPT-pinned, not merely amount-matched — the difference between a fee and a fee you can
///     redirect to yourself);
///   • the honest split — seller ask−fee, treasury fee, item conserved — → CO-SIGNED.
/// </summary>
public class CovenantOfferFeeLegProbeTests : IAsyncLifetime
{
    private static readonly Uri EmulatorUri = new("http://localhost:7073/");
    private const long Ask = 5_000;
    private const long Fee = 500;                 // seller absorbs: they receive Ask − Fee
    private const long SellerProceeds = Ask - Fee;

    private SelfCustodyWallet _seller = null!;
    private SelfCustodyWallet _buyer = null!;
    private SelfCustodyWallet _treasury = null!;
    private readonly List<string> _dbPaths = [];

    public async Task InitializeAsync()
    {
        await RegtestHelper.WaitForArkdReadyAsync(TimeSpan.FromSeconds(30));
        _seller = await NewWalletAsync();
        _buyer = await NewWalletAsync();
        _treasury = await NewWalletAsync();
        await RegtestHelper.ArkSend(_seller.Address, 200_000);
        await RegtestHelper.ArkSend(_buyer.Address, 100_000);
        await _seller.WaitForBalanceAsync(200_000, TimeSpan.FromSeconds(60));
        await _buyer.WaitForBalanceAsync(100_000, TimeSpan.FromSeconds(60));
    }

    private async Task<SelfCustodyWallet> NewWalletAsync()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"ah-offerfee-{Guid.NewGuid():N}.db");
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
        await _treasury.DisposeAsync();
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
    public async Task FeeLeg_SkippingOrRedirectingTheTreasuryRefused_HonestSplitCoSigned()
    {
        var transport = _seller.GetService<global::NArk.Core.Transport.IClientTransport>();
        var serverInfo = await transport.GetServerInfoAsync();
        var emulatorInfo = await EmulatorEndpoint.Client(EmulatorUri).GetInfoAsync();
        var isMain = serverInfo.Network == Network.Main;

        var itemId = await IssueItemAsync();
        var item = AssetId.FromString(itemId);
        var sellerScript = ArkAddress.Parse(_seller.Address).ScriptPubKey;
        var buyerScript = ArkAddress.Parse(_buyer.Address).ScriptPubKey;
        var treasuryScript = ArkAddress.Parse(_treasury.Address).ScriptPubKey;

        var refundAfter = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var offer = new OfferParams(
            _seller.Address, itemId, Ask, serverInfo.Dust.Satoshi, "offer-fee-leg", refundAfter,
            FeeSats: Fee, TreasuryFeeAddress: _treasury.Address);
        var contract = OfferContracts.Build(offer, serverInfo.SignerKey, emulatorInfo.SignerPubkey);
        await _seller.SendAssetAsync(contract.GetArkAddress().ToString(isMain), itemId, 1);
        var offerVtxo = (await CovenantSpender.WaitForVtxosAsync(_buyer, contract, 1, TimeSpan.FromSeconds(45)))
            .First(v => v.Assets?.Any(a => a.AssetId == itemId) == true);

        // The buyer's own coins fund the ask, exactly as the real flow selects them.
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
        Assert.True(fundedSats >= Ask, "buyer lacks funds for the fulfil attempts");
        var offerDust = (long)offerVtxo.Amount;
        // The buyer pays the sticker Ask either way — only the split differs.
        var buyerChange = offerDust + fundedSats - Ask;

        // ── Cheat A: the PRE-FEE shape — pay the seller the full ask, no treasury output at all.
        //    The fee index is pointed at the seller's output, the only payout that exists.
        var skipped = await Assert.ThrowsAnyAsync<Exception>(() => CovenantSpender.SpendManyAsync(
            _buyer, EmulatorUri,
            [new CovenantSpender.CovenantInput(contract, "fulfill",
                // bottom→top: [itemOutIdx, feeOutIdx, sellerOutIdx]
                [ArkadeCovenants.EncodeIndex(1), ArkadeCovenants.EncodeIndex(0), ArkadeCovenants.EncodeIndex(0)],
                offerVtxo)],
            [
                new TxOut(Money.Satoshis(Ask), sellerScript),                    // seller keeps the fee (vout 0)
                new TxOut(Money.Satoshis(offerDust + fundedSats - Ask), buyerScript), // change + item (vout 1)
            ],
            extraPackets: [Packet.Create(
                [AssetGroup.Create(item, null, [AssetInput.Create(0, 1)], [AssetOutput.Create(1, 1)], [])])],
            fundingCoins: funding));
        Assert.Contains("Emulator tx failed", skipped.Message);

        // ── Cheat B (the sharper one): pay the RIGHT amounts, but route the fee to the BUYER.
        //    If only the amount were pinned this would pass and the "fee" would be self-refunding.
        var redirected = await Assert.ThrowsAnyAsync<Exception>(() => CovenantSpender.SpendManyAsync(
            _buyer, EmulatorUri,
            [new CovenantSpender.CovenantInput(contract, "fulfill",
                [ArkadeCovenants.EncodeIndex(2), ArkadeCovenants.EncodeIndex(1), ArkadeCovenants.EncodeIndex(0)],
                offerVtxo)],
            [
                new TxOut(Money.Satoshis(SellerProceeds), sellerScript), // seller correct (vout 0)
                new TxOut(Money.Satoshis(Fee), buyerScript),             // fee to the BUYER (vout 1)
                new TxOut(Money.Satoshis(buyerChange), buyerScript),     // change + item (vout 2)
            ],
            extraPackets: [Packet.Create(
                [AssetGroup.Create(item, null, [AssetInput.Create(0, 1)], [AssetOutput.Create(2, 1)], [])])],
            fundingCoins: funding));
        Assert.Contains("Emulator tx failed", redirected.Message);

        // ── Honest: through the REAL buyer flow, not a hand-built spend. OfferFulfillFlow reads the
        //    fee off the params and emits the 3-element witness itself, so this co-signing is also the
        //    proof that the shipped flow and the covenant agree on the split and the output order.
        var honest = await OfferFulfillFlow.FulfillAsync(_buyer, EmulatorUri, offer);
        Assert.False(string.IsNullOrEmpty(honest.SignedArkTx));

        // The item really moved, and the treasury really got paid — the point of the whole exercise.
        await _buyer.WaitForAssetAsync(itemId, TimeSpan.FromSeconds(60));
        await _treasury.WaitForBalanceAsync(Fee, TimeSpan.FromSeconds(60));
    }
}
