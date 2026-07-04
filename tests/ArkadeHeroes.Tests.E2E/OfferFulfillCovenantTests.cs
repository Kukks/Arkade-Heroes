using ArkadeHeroes.Chain.Covenants;
using ArkadeHeroes.Chain.NArk;
using NArk.Abstractions;
using NBitcoin;

namespace ArkadeHeroes.Tests.E2E;

/// <summary>
/// The first game-shaped covenant live on regtest — the item_offer pattern
/// (banco): the seller rests an offer VTXO that ANYONE may take, but only by
/// paying the seller the asking price in the same transaction. The taker
/// starts with an EMPTY wallet and ends up with the spread — no seller
/// signature, no server trust, just the covenant. Underpayment is refused by
/// the emulator.
/// </summary>
public class OfferFulfillCovenantTests : IAsyncLifetime
{
    private static readonly Uri EmulatorUri = new("http://localhost:7073/");
    private const long OfferValue = 10_000;
    private const long AskPrice = 6_000;

    private SelfCustodyWallet _seller = null!;
    private SelfCustodyWallet _taker = null!;
    private readonly List<string> _dbPaths = [];

    public async Task InitializeAsync()
    {
        await RegtestHelper.WaitForArkdReadyAsync(TimeSpan.FromSeconds(30));
        _seller = await NewWalletAsync();
        _taker = await NewWalletAsync();

        // Only the SELLER ever gets funded; the taker starts with nothing.
        await RegtestHelper.ArkSend(_seller.Address, 50_000);
        await _seller.WaitForBalanceAsync(50_000, TimeSpan.FromSeconds(60));
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

    public async Task DisposeAsync()
    {
        await _seller.DisposeAsync();
        await _taker.DisposeAsync();
        foreach (var path in _dbPaths)
            try { if (File.Exists(path)) File.Delete(path); } catch { /* windows lock */ }
    }

    [Fact]
    public async Task RestingOfferIsFulfilledTrustlessly_AndUnderpaymentIsRefused()
    {
        var transport = _seller.GetService<global::NArk.Core.Transport.IClientTransport>();
        var serverInfo = await transport.GetServerInfoAsync();
        var emulatorInfo = await new EmulatorClient(EmulatorUri).GetInfoAsync();

        // The offer covenant: whoever spends this VTXO must pay the seller
        // exactly AskPrice at a witness-chosen output index.
        var sellerPkScript = ArkAddress.Parse(_seller.Address).ScriptPubKey;
        var offer = new ArkadeArtifactContract(
            "item-offer", serverInfo.SignerKey, emulatorInfo.SignerPubkey,
            [new ArkadeContractFunction("fulfill", ArkadeCovenants.PayTo(sellerPkScript, AskPrice))]);

        // Seller rests the offer and goes offline (nothing else from them).
        await _seller.SendAsync(offer.GetArkAddress().ToString(serverInfo.Network == Network.Main), OfferValue);
        var offerVtxo = await CovenantSpender.WaitForVtxoAsync(_seller, offer, TimeSpan.FromSeconds(30));

        var takerPkScript = ArkAddress.Parse(_taker.Address).ScriptPubKey;

        // 1. Greedy taker tries to underpay — the emulator refuses to co-sign.
        var underpay = await Assert.ThrowsAnyAsync<Exception>(() => CovenantSpender.SpendAsync(
            _taker, EmulatorUri, offer, "fulfill",
            [ArkadeCovenants.EncodeIndex(0)], offerVtxo,
            [
                new TxOut(Money.Satoshis(AskPrice - 1_000), sellerPkScript),
                new TxOut(Money.Satoshis(OfferValue - AskPrice + 1_000), takerPkScript),
            ]));
        Assert.Contains("Emulator rejected", underpay.Message);

        // 2. Honest fulfillment: seller gets the ask, taker keeps the spread.
        var response = await CovenantSpender.SpendAsync(
            _taker, EmulatorUri, offer, "fulfill",
            [ArkadeCovenants.EncodeIndex(0)], offerVtxo,
            [
                new TxOut(Money.Satoshis(AskPrice), sellerPkScript),
                new TxOut(Money.Satoshis(OfferValue - AskPrice), takerPkScript),
            ]);
        Assert.False(string.IsNullOrEmpty(response.SignedArkTx));

        // The taker — who started with an EMPTY wallet — now holds the spread.
        await _taker.WaitForBalanceAsync(OfferValue - AskPrice, TimeSpan.FromSeconds(45));
    }
}
