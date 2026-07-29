using ArkadeHeroes.Chain.Covenants;
using ArkadeHeroes.Chain.NArk;
using NArk.Abstractions;
using NArk.Abstractions.Assets;
using NArk.Abstractions.VTXOs;
using NArk.Core.Services;
using NBitcoin;

namespace ArkadeHeroes.Tests.E2E;

/// <summary>
/// GO/NO-GO for making item-offer marketplace fees COUNTABLE without touching the offer covenant.
///
/// The server under-reports marketplace income because ReconcileOfferAsync only sees that an offer
/// stopped being funded, which is equally true of a sale and of a seller reclaim — so it books
/// nothing, on the sound principle that over-stating a treasury holding real bitcoin is the
/// unsurvivable direction.
///
/// But the two paths are ALREADY structurally different on-chain: the fulfil leaf pins a
/// <c>fee</c>-sat output to the treasury script (OfferFulfillFlow's vout 1), and the reclaim leaf
/// emits exactly one output, to the seller. What was missing is only that nothing in the server ever
/// READ that difference — no code passes <c>includeSpent: true</c> or looks at
/// <see cref="ArkVtxo.SpentByTransactionId"/>.
///
/// The whole design rests on two unproven assumptions about what arkd reports back, and if either
/// fails the approach pivots HERE, before any server wiring:
///   • after an offer VTXO is spent, re-polling the offer script still returns it with
///     <c>SpentByTransactionId</c> populated;
///   • the VTXO the fulfil CREATES at the treasury script carries that same id as its
///     <c>TransactionId</c> — i.e. the two ends of one Arkade transaction agree on one id.
///
/// Proves, on one FUNGIBLE asset the seller keeps spare units of (the exact case where inferring a
/// sale from balances or item counts flips into an over-count):
///   • a fulfil is attributable — a treasury VTXO carries the spending id and exactly the fee;
///   • a reclaim is NOT — no treasury VTXO carries the reclaim's spending id;
///   • both still co-sign.
/// </summary>
public class OfferSpendAttributionProbeTests : IAsyncLifetime
{
    private static readonly Uri EmulatorUri = new("http://localhost:7073/");
    private const string EsploraApi = "http://localhost:8999/api/v1";
    private const long Ask = 6_000;
    private const long Fee = 700;

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
        var dbPath = Path.Combine(Path.GetTempPath(), $"ah-offerattr-{Guid.NewGuid():N}.db");
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

    [Fact]
    public async Task AFulfilIsAttributableToTheTreasuryLeg_AReclaimIsNot_AndBothCoSign()
    {
        var transport = _seller.GetService<global::NArk.Core.Transport.IClientTransport>();
        var serverInfo = await transport.GetServerInfoAsync();
        var emulatorInfo = await new EmulatorClient(EmulatorUri).GetInfoAsync();
        var isMain = serverInfo.Network == Network.Main;
        var dust = serverInfo.Dust.Satoshi;

        // ONE fungible asset with spare units left in the seller's wallet throughout — the case where
        // counting units or reading balances cannot tell a sale from a reclaim.
        var mgr = _seller.GetService<IAssetManager>();
        var itemId = (await mgr.IssueAsync(_seller.WalletId, new IssuanceParams(Amount: 3))).AssetId;
        await _seller.WaitForAssetAsync(itemId, TimeSpan.FromSeconds(45));

        // Two offers of the SAME asset: one will sell, one will be reclaimed unsold.
        var soldOffer = new OfferParams(
            _seller.Address, itemId, Ask, dust, "offer-attr-sold",
            DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
            FeeSats: Fee, TreasuryFeeAddress: _treasury.Address);
        var reclaimAfter = DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeSeconds();
        var unsoldOffer = new OfferParams(
            _seller.Address, itemId, Ask, dust, "offer-attr-unsold", reclaimAfter,
            FeeSats: Fee, TreasuryFeeAddress: _treasury.Address);

        var soldContract = OfferContracts.Build(soldOffer, serverInfo.SignerKey, emulatorInfo.SignerPubkey);
        var unsoldContract = OfferContracts.Build(unsoldOffer, serverInfo.SignerKey, emulatorInfo.SignerPubkey);
        var soldScript = soldContract.GetArkAddress().ScriptPubKey.ToHex();
        var unsoldScript = unsoldContract.GetArkAddress().ScriptPubKey.ToHex();
        var treasuryScript = ArkAddress.Parse(_treasury.Address).ScriptPubKey.ToHex();

        await _seller.SendAssetAsync(soldContract.GetArkAddress().ToString(isMain), itemId, 1);
        await CovenantSpender.WaitForVtxosAsync(_seller, soldContract, 1, TimeSpan.FromSeconds(45));
        await _seller.SendAssetAsync(unsoldContract.GetArkAddress().ToString(isMain), itemId, 1);
        await CovenantSpender.WaitForVtxosAsync(_seller, unsoldContract, 1, TimeSpan.FromSeconds(45));

        // The decisive control for what ArkTxid MEANS: read it while the offer is still RESTING. If it
        // names the tx that CREATED the vtxo it is fixed now and will not move; if it names the tx that
        // SPENDS it, it must change when the buyer fulfils. Attribution rests on that distinction, so it
        // is measured, not assumed.
        var restingArkTxid = (await ReadOfferVtxoAsync(
            _seller.GetService<VtxoSynchronizationService>(), _seller.GetService<IVtxoStorage>(),
            soldScript, itemId))?.ArkTxid;

        // ── The SALE: the real shipped buyer flow, so what co-signs is what production emits.
        var fulfil = await OfferFulfillFlow.FulfillAsync(_buyer, EmulatorUri, soldOffer);
        Assert.False(string.IsNullOrEmpty(fulfil.SignedArkTx));
        await _treasury.WaitForBalanceAsync(Fee, TimeSpan.FromSeconds(60));

        // ── The RECLAIM: the real shipped seller flow, on an offer of the SAME asset.
        using var esploraHttp = new HttpClient();
        await RegtestHelper.WaitForChainTimeAsync(reclaimAfter, TimeSpan.FromSeconds(180));
        var reclaim = await OfferReclaimFlow.ReclaimAsync(
            _seller, EmulatorUri, unsoldOffer,
            ct => EsploraChainTime.GetMedianTimeAsync(esploraHttp, EsploraApi, ct));
        Assert.False(string.IsNullOrEmpty(reclaim.SignedArkTx));

        // ── Now look with the SERVER's eyes: a third party that holds neither side's keys and can only
        //    poll scripts and read VTXO records back, exactly as NArkChainService does.
        var sync = _treasury.GetService<VtxoSynchronizationService>();
        var storage = _treasury.GetService<IVtxoStorage>();

        var soldVtxo = await WaitForSpentAsync(sync, storage, soldScript, itemId, TimeSpan.FromSeconds(90));
        var unsoldVtxo = await WaitForSpentAsync(sync, storage, unsoldScript, itemId, TimeSpan.FromSeconds(90));

        await sync.PollScriptsForVtxos(new HashSet<string> { treasuryScript });
        var treasuryVtxos = (await storage.GetVtxos(scripts: [treasuryScript], includeSpent: true))
            .DistinctBy(v => v.OutPoint).ToList();

        // The seller's own script carries a SECOND, independent output of each of the same two
        // transactions — the sale payout (ask − fee) and the reclaimed item. Cross-checking those
        // against the same id is what turns one coincidence into a rule.
        var sellerScript = ArkAddress.Parse(_seller.Address).ScriptPubKey.ToHex();
        await sync.PollScriptsForVtxos(new HashSet<string> { sellerScript });
        var sellerVtxos = (await storage.GetVtxos(scripts: [sellerScript], includeSpent: true))
            .DistinctBy(v => v.OutPoint).ToList();

        var saleTxid = soldVtxo.ArkTxid;
        var reclaimTxid = unsoldVtxo.ArkTxid;
        var paidBySale = treasuryVtxos.Where(v => v.TransactionId == saleTxid).ToList();
        var paidByReclaim = treasuryVtxos.Where(v => v.TransactionId == reclaimTxid).ToList();
        var sellerFromSale = sellerVtxos.Where(v => v.TransactionId == saleTxid).ToList();
        var sellerFromReclaim = sellerVtxos.Where(v => v.TransactionId == reclaimTxid).ToList();

        // Written to a file, not just the console: xunit v2 does not reliably surface Console output
        // per test, and this probe's whole value is the verbatim record of what arkd reported back.
        var report = new System.Text.StringBuilder()
            .AppendLine("== offer spend attribution ==================================")
            .AppendLine($"item asset (fungible, seller keeps spare units) : {itemId}")
            .AppendLine($"seller units still held after both offers       : {await SellerUnitsAsync(itemId)}")
            .AppendLine($"ArkTxid of the SOLD offer while still RESTING   : {restingArkTxid ?? "<null>"}")
            .AppendLine($"SOLD   offer vtxo {soldVtxo.OutPoint}")
            .AppendLine($"       SpentBy={soldVtxo.SpentByTransactionId ?? "<null>"} SettledBy={soldVtxo.SettledByTransactionId ?? "<null>"} ArkTxid={soldVtxo.ArkTxid ?? "<null>"}")
            .AppendLine($"UNSOLD offer vtxo {unsoldVtxo.OutPoint}")
            .AppendLine($"       SpentBy={unsoldVtxo.SpentByTransactionId ?? "<null>"} SettledBy={unsoldVtxo.SettledByTransactionId ?? "<null>"} ArkTxid={unsoldVtxo.ArkTxid ?? "<null>"}")
            .AppendLine($"treasury vtxos ({treasuryVtxos.Count}):");
        foreach (var v in treasuryVtxos)
            report.AppendLine($"       txid={v.TransactionId} vout={v.TransactionOutputIndex} amount={v.Amount} spent={v.IsSpent()}");
        report.AppendLine($"seller vtxos ({sellerVtxos.Count}):");
        foreach (var v in sellerVtxos)
            report.AppendLine($"       txid={v.TransactionId} vout={v.TransactionOutputIndex} amount={v.Amount} assets={v.Assets?.Count ?? 0} spent={v.IsSpent()}");
        report.AppendLine($"treasury outputs whose txid == SALE    ArkTxid : {paidBySale.Count}")
              .AppendLine($"treasury outputs whose txid == RECLAIM ArkTxid : {paidByReclaim.Count}")
              .AppendLine($"seller   outputs whose txid == SALE    ArkTxid : {sellerFromSale.Count}")
              .AppendLine($"seller   outputs whose txid == RECLAIM ArkTxid : {sellerFromReclaim.Count}")
              .AppendLine("============================================================");
        var reportPath = Path.Combine(Path.GetTempPath(), "offer-spend-attribution.txt");
        await File.WriteAllTextAsync(reportPath, report.ToString());
        Console.WriteLine(report.ToString());

        // ── What ArkTxid means. It moved when the buyer fulfilled, so it names the tx that SPENDS the
        //    vtxo, not the one that created it — the premise the whole attribution rests on.
        Assert.False(string.IsNullOrEmpty(saleTxid), "the SOLD offer vtxo reports no Arkade txid");
        Assert.False(string.IsNullOrEmpty(reclaimTxid), "the RECLAIMED offer vtxo reports no Arkade txid");
        Assert.NotEqual(restingArkTxid, saleTxid);
        Assert.NotEqual(saleTxid, reclaimTxid);

        // ── Cross-check: each spend's OTHER output agrees on the same id, so the match is a property of
        //    the transaction and not an artefact of one record.
        var sellerPayout = Assert.Single(sellerFromSale);
        Assert.Equal((ulong)(Ask - Fee), sellerPayout.Amount);
        var reclaimedItem = Assert.Single(sellerFromReclaim);
        Assert.Contains(reclaimedItem.Assets ?? [], a => a.AssetId == itemId);

        // ── The point: the sale is attributable to THIS offer for exactly the fee the covenant pinned…
        var saleLeg = Assert.Single(paidBySale);
        Assert.Equal((ulong)Fee, saleLeg.Amount);

        // …and the reclaim is not — nothing the treasury holds came from the reclaim's transaction.
        Assert.Empty(paidByReclaim);
    }

    /// <summary>Reads the item-carrying VTXO resting at a covenant script, spent or not.</summary>
    private static async Task<ArkVtxo?> ReadOfferVtxoAsync(
        VtxoSynchronizationService sync, IVtxoStorage storage, string script, string assetId)
    {
        await sync.PollScriptsForVtxos(new HashSet<string> { script });
        return (await storage.GetVtxos(scripts: [script], includeSpent: true))
            .DistinctBy(v => v.OutPoint)
            .FirstOrDefault(v => v.Assets?.Any(a => a.AssetId == assetId) == true);
    }

    /// <summary>Polls a covenant script until its item-carrying VTXO reports a spender, as the server would.</summary>
    private static async Task<ArkVtxo> WaitForSpentAsync(
        VtxoSynchronizationService sync, IVtxoStorage storage, string script, string assetId, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        ArkVtxo? seen = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await sync.PollScriptsForVtxos(new HashSet<string> { script });
            seen = (await storage.GetVtxos(scripts: [script], includeSpent: true))
                .DistinctBy(v => v.OutPoint)
                .FirstOrDefault(v => v.Assets?.Any(a => a.AssetId == assetId) == true);
            if (seen is not null && !string.IsNullOrEmpty(seen.SpentByTransactionId)) return seen;
            await Task.Delay(2_000);
        }
        return seen ?? throw new InvalidOperationException(
            $"No item-carrying VTXO was ever observed at {script} — the probe cannot judge attribution.");
    }

    private async Task<ulong> SellerUnitsAsync(string assetId)
    {
        var sync = _seller.GetService<VtxoSynchronizationService>();
        var storage = _seller.GetService<IVtxoStorage>();
        var script = ArkAddress.Parse(_seller.Address).ScriptPubKey.ToHex();
        await sync.PollScriptsForVtxos(new HashSet<string> { script });
        return (await storage.GetVtxos(scripts: [script]))
            .DistinctBy(v => v.OutPoint)
            .Where(v => v.Assets is { Count: > 0 })
            .SelectMany(v => v.Assets!)
            .Where(a => a.AssetId == assetId)
            .Aggregate(0UL, (sum, a) => sum + a.Amount);
    }
}
