using ArkadeHeroes.Web.Wallet;
using Microsoft.Extensions.Options;
using NArk.Abstractions;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Intents;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core;
using NArk.Core.Contracts;
using NArk.Core.Fees;
using NArk.Core.Models.Options;
using NArk.Core.Scripts;
using NArk.Core.Services;
using NArk.Core.Transport;
using NBitcoin;
using NBitcoin.Secp256k1;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace ArkadeHeroes.Tests.Web;

/// <summary>
/// What the wallet pays to keep its coins alive, and how often it decides to pay it.
///
/// <para>Arkade coins expire. The SDK's background loop re-boards ("renews") a coin before its expiry
/// by spending it back to the player, and the operator charges an intent fee to do so — on this stack
/// <c>amount * 0.01</c> per offchain input, i.e. 1% of everything renewed. That fee is real money, so
/// the only question that matters is how often the wallet decides a renewal is due.</para>
///
/// <para>The bug these tests pin: <see cref="SimpleIntentScheduler"/> renews whenever
/// <c>expiry - threshold &lt; now</c>. The wallet configures a one-DAY threshold, and this stack's coins
/// live ~28 minutes — so that test is true the instant a coin is born, and every intent-generation
/// cycle renewed the entire balance again. The loop runs a cycle immediately on start, and the wallet
/// starts the SDK on every WASM boot, so a player who only reloaded the page paid 1% of their balance.</para>
/// </summary>
public class WalletRenewalTests
{
    // This stack's ACTUAL fee schedule, read from the live arkd at GET /v1/info:
    // "fees":{"intentFee":{"offchainInput":"amount * 0.01", ... }}
    private const string OffchainInputFee = "amount * 0.01";
    private const string WalletId = "w1";

    // The wallet's configured renewal threshold (src/ArkadeHeroes.Web/Program.cs).
    private static readonly TimeSpan Threshold = TimeSpan.FromDays(1);

    // This stack's real coin lifetime: arkd reported next_expiration "28 minutes".
    private static readonly TimeSpan RegtestLife = TimeSpan.FromMinutes(28);

    // ── The mechanism: 1% of everything renewed, rounded up ──────────────────────────────────────

    [Theory]
    [InlineData(752_990, 7_530)]   // Bravo's observed drop, to the satoshi
    [InlineData(740_180, 7_402)]   // Alpha's observed drop, to the satoshi
    public async Task RenewingTheWholeBalanceCostsOnePercentOfIt(long balance, long expectedFee)
    {
        // Reproduces the live observation from the operator's own fee expression: the estimator sums
        // amount * 0.01 over every input and rounds the total UP, which is why 7,529.9 was charged 7,530.
        var fee = await FeeEstimator().EstimateFeeAsync(
            new ArkIntentSpec([Coin(balance)], [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1)));

        Assert.Equal(expectedFee, fee);
    }

    // ── The defect: a brand-new coin was already "due" for renewal ───────────────────────────────

    [Fact]
    public async Task TheSdkSchedulerAloneRenewsACoinThatWasBornSecondsAgo()
    {
        // Characterises what the wallet did before the guard, and why a page reload cost 1%: with a
        // one-day threshold against a 28-minute coin, "approaching expiry" is true from birth.
        var fresh = Coin(752_990, age: TimeSpan.Zero, life: RegtestLife);

        Assert.Single(await SdkScheduler().GetIntentsToSubmit([fresh]));
    }

    [Fact]
    public async Task AFreshCoinIsNotRenewed()
    {
        // The fix: a coin with most of its life left is not worth 1% to re-board. This is the boot case —
        // the tab opening is not a reason to pay.
        var fresh = Coin(752_990, age: TimeSpan.Zero, life: RegtestLife);

        Assert.Empty(await GuardedScheduler().GetIntentsToSubmit([fresh]));
    }

    [Fact]
    public async Task ACoinCloseToExpiryIsStillRenewed()
    {
        // The guard must never stop a renewal that is actually needed — that would let the coin expire.
        var expiring = Coin(752_990, age: RegtestLife - TimeSpan.FromMinutes(4), life: RegtestLife);

        Assert.Single(await GuardedScheduler().GetIntentsToSubmit([expiring]));
    }

    [Fact]
    public async Task TheFloorRenewsAShortLivedCoinEarlierThanAQuarterOfItsLifeWould()
    {
        // Twelve minutes left of a 28-minute coin: OUTSIDE the quarter-life window (7 minutes) but INSIDE
        // the floor, so only the floor can be renewing it. That margin is what a tab has to survive being
        // hidden or frozen — renewing every cycle used to buy a whole lifetime of it, and this buys back
        // a usable part of that without going back to paying on every boot.
        var coin = Coin(752_990, age: RegtestLife - TimeSpan.FromMinutes(12), life: RegtestLife);

        Assert.Single(await GuardedScheduler().GetIntentsToSubmit([coin]));
    }

    [Fact]
    public void TheRenewalWindowLeavesRoomForSeveralPollsInsideIt()
    {
        // The floor is only a margin if the loop actually runs inside it. These two numbers are a pair:
        // lengthening the poll interval without widening the floor silently narrows the margin to nothing.
        Assert.True(
            BatchRenewalScheduler.MinimumRenewalWindow >= BatchRenewalScheduler.PollInterval * 5,
            $"a {BatchRenewalScheduler.MinimumRenewalWindow} renewal window leaves too few "
            + $"{BatchRenewalScheduler.PollInterval} polls to catch a coin before it expires");
    }

    [Fact]
    public async Task ReloadingRepeatedlyCostsNothingUntilTheCoinIsActuallyNearExpiry()
    {
        // Ten boots in quick succession, the scenario that made this urgent: ten renewals at 1% each
        // would have cost roughly a tenth of the balance for doing nothing.
        var fresh = Coin(752_990, age: TimeSpan.Zero, life: RegtestLife);
        var scheduler = GuardedScheduler();

        for (var boot = 0; boot < 10; boot++)
            Assert.Empty(await scheduler.GetIntentsToSubmit([fresh]));
    }

    // ── Mainnet-shaped coins must keep the behaviour they already had ────────────────────────────

    [Fact]
    public async Task AMainnetCoinInsideTheOneDayThresholdIsStillRenewed()
    {
        // A four-week coin with twelve hours left is inside the configured one-day threshold. The guard
        // must not delay this: on a long-lived coin the one-day threshold was already the right answer.
        var life = TimeSpan.FromDays(28);
        var nearExpiry = Coin(752_990, age: life - TimeSpan.FromHours(12), life: life);

        Assert.Single(await GuardedScheduler().GetIntentsToSubmit([nearExpiry]));
    }

    [Fact]
    public async Task AMainnetCoinWithWeeksLeftIsNotRenewed()
    {
        var life = TimeSpan.FromDays(28);
        var young = Coin(752_990, age: TimeSpan.FromDays(1), life: life);

        Assert.Empty(await GuardedScheduler().GetIntentsToSubmit([young]));
    }

    // ── Fail open: anything the guard cannot reason about is left to the SDK ─────────────────────

    // These assert what the guard FORWARDS, not what the SDK then decides: the point is that the guard
    // never withholds one of these coins. What the inner scheduler makes of it is the SDK's own call
    // (a height-gated coin, for instance, needs a height threshold this wallet doesn't configure).

    [Fact]
    public async Task ARecoverableCoinIsAlwaysPassedThrough()
    {
        // Swept/expired coins must be batched to be recovered at all; holding one back strands funds.
        var swept = Coin(752_990, age: TimeSpan.Zero, life: RegtestLife, swept: true);

        Assert.Equal([swept], await Forwarded(swept));
    }

    [Fact]
    public async Task AnUnrolledCoinIsAlwaysPassedThrough()
    {
        // Unrolled coins sit on-chain racing the exit delay — the SDK batches them ASAP by design.
        var unrolled = Coin(752_990, age: TimeSpan.Zero, life: RegtestLife, unrolled: true);

        Assert.Equal([unrolled], await Forwarded(unrolled));
    }

    [Fact]
    public async Task ACoinWithNoTimeExpiryIsAlwaysPassedThrough()
    {
        // Height-gated expiry: the guard has no wall-clock lifetime to reason about, so it defers.
        var heightOnly = Coin(752_990, age: TimeSpan.Zero, life: RegtestLife, heightExpiryOnly: true);

        Assert.Equal([heightOnly], await Forwarded(heightOnly));
    }

    [Fact]
    public async Task ACoinWhoseBirthIsNotBeforeItsExpiryIsAlwaysPassedThrough()
    {
        // A nonsensical lifetime (clock skew, a storage default) must not be read as "plenty of life left".
        var skewed = Coin(752_990, age: TimeSpan.Zero, life: TimeSpan.Zero);

        Assert.Equal([skewed], await Forwarded(skewed));
    }

    [Fact]
    public async Task AFreshCoinIsNotEvenOfferedToTheInnerScheduler()
    {
        // The other half of the same seam: a coin with life left never reaches the SDK at all, so no
        // intent — and no fee — can be generated for it.
        var fresh = Coin(752_990, age: TimeSpan.Zero, life: RegtestLife);

        Assert.Empty(await Forwarded(fresh));
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────────────────────────

    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static IIntentScheduler GuardedScheduler() =>
        new BatchRenewalScheduler(SdkScheduler(), Blockchain());

    /// <summary>The coins the guard actually handed to the scheduler underneath it.</summary>
    private static async Task<IReadOnlyCollection<ArkCoin>> Forwarded(params ArkCoin[] coins)
    {
        var inner = Substitute.For<IIntentScheduler>();
        inner.GetIntentsToSubmit(Arg.Any<IReadOnlyCollection<ArkCoin>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ArkIntentSpec>());

        await new BatchRenewalScheduler(inner, Blockchain()).GetIntentsToSubmit(coins);

        var call = inner.ReceivedCalls().FirstOrDefault();
        return call is null ? [] : (IReadOnlyCollection<ArkCoin>)call.GetArguments()[0]!;
    }

    private static SimpleIntentScheduler SdkScheduler()
    {
        var contracts = Substitute.For<IContractService>();
        contracts.DeriveContract(Arg.Any<string>(), Arg.Any<NextContractPurpose>(), Arg.Any<ArkContract[]>(),
                Arg.Any<ContractActivityState>(), Arg.Any<Dictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .Returns(Contract());
        return new SimpleIntentScheduler(FeeEstimator(), Transport(), contracts, Blockchain(),
            Options.Create(new SimpleIntentSchedulerOptions { Threshold = Threshold }));
    }

    private static DefaultFeeEstimator FeeEstimator() => new(Transport(), Blockchain());

    private static IBitcoinBlockchain Blockchain()
    {
        var chain = Substitute.For<IBitcoinBlockchain>();
        chain.GetChainTime(Arg.Any<CancellationToken>()).Returns(new TimeHeight(Now, 100));
        return chain;
    }

    private static IClientTransport Transport()
    {
        var transport = Substitute.For<IClientTransport>();
        transport.GetServerInfoAsync(Arg.Any<CancellationToken>()).Returns(ServerInfo());
        return transport;
    }

    private static uint _index;

    private static ArkCoin Coin(
        long sats,
        TimeSpan? age = null,
        TimeSpan? life = null,
        bool swept = false,
        bool unrolled = false,
        bool heightExpiryOnly = false)
    {
        var script = new GenericTapScript([Op.GetPushOp(1), OpcodeType.OP_TRUE]);
        var birth = Now - (age ?? TimeSpan.Zero);
        var expiresAt = birth + (life ?? TimeSpan.FromDays(30));
        return new ArkCoin(
            walletIdentifier: WalletId,
            contract: Contract(),
            birth: birth,
            expiresAt: heightExpiryOnly ? null : expiresAt,
            expiresAtHeight: heightExpiryOnly ? 200u : null,
            outPoint: new OutPoint(uint256.One, _index++),
            txOut: new TxOut(Money.Satoshis(sats), Script.Empty),
            signerDescriptor: null,
            spendingScriptBuilder: script,
            spendingConditionWitness: null,
            lockTime: null,
            sequence: null,
            swept: swept,
            unrolled: unrolled,
            assets: null);
    }

    private static ArkContract Contract()
    {
        var serverKey = ECXOnlyPubKey.Create(new Key().PubKey.TaprootInternalKey.ToBytes());
        return new GenericArkContract(serverKey.ToOutputDescriptor(Network.RegTest),
            [new GenericTapScript([Op.GetPushOp(1), OpcodeType.OP_TRUE])]);
    }

    // ── What the player is told it costs ─────────────────────────────────────────────────────────

    [Fact]
    public async Task TheUpkeepQuoteIsTheOperatorsRealFeeForThisWallet()
    {
        // The quote must be the same arithmetic that actually gets charged, not a hardcoded "1%".
        var quote = await Upkeep(Coin(752_990)).QuoteAsync(WalletId);

        Assert.NotNull(quote);
        Assert.Equal(752_990, quote!.Value.AmountSats);
        Assert.Equal(7_530, quote.Value.FeeSats);
        Assert.Equal(1d, quote.Value.Percent, precision: 2);
    }

    [Fact]
    public async Task TheUpkeepQuoteSumsEveryCoinTheWalletHolds()
    {
        var quote = await Upkeep(Coin(500_000), Coin(252_990)).QuoteAsync(WalletId);

        Assert.Equal(752_990, quote!.Value.AmountSats);
        Assert.Equal(7_530, quote.Value.FeeSats);   // 5,000 + 2,529.9, each rounded up together
    }

    [Fact]
    public async Task TheUpkeepLineNamesTheChargeItsSizeAndWhoTakesIt()
    {
        var summary = (await Upkeep(Coin(752_990)).QuoteAsync(WalletId))!.Value.Summary;

        Assert.Contains("7,530 sat", summary);
        Assert.Contains("1%", summary);
        Assert.Contains("Arkade operator", summary);
        Assert.Contains("expire", summary);
        // The charge is per renewal, not per page view — saying otherwise would be the same lie in reverse.
        Assert.Contains("not per visit", summary);
    }

    [Fact]
    public async Task AnEmptyWalletIsQuotedNothingRatherThanZero()
    {
        Assert.Null(await Upkeep().QuoteAsync(WalletId));
    }

    [Fact]
    public async Task AnUnreadableEstimateSaysNothingRatherThanGuessing()
    {
        var spending = Substitute.For<ISpendingService>();
        spending.GetAvailableCoins(WalletId).ThrowsAsync(new HttpRequestException("arkd unreachable"));

        Assert.Null(await new RenewalUpkeep(spending, FeeEstimator()).QuoteAsync(WalletId));
    }

    private static RenewalUpkeep Upkeep(params ArkCoin[] coins)
    {
        var spending = Substitute.For<ISpendingService>();
        spending.GetAvailableCoins(WalletId).Returns(new HashSet<ArkCoin>(coins));
        return new RenewalUpkeep(spending, FeeEstimator());
    }

    private static ArkServerInfo ServerInfo() => new(
        Dust: Money.Satoshis(330),
        SignerKey: ECXOnlyPubKey.Create(new Key().PubKey.TaprootInternalKey.ToBytes())
            .ToOutputDescriptor(Network.RegTest),
        DeprecatedSigners: [],
        Network: Network.RegTest,
        UnilateralExit: new Sequence(512),
        BoardingExit: new Sequence(4096),
        ForfeitAddress: BitcoinAddress.Create("bcrt1qw508d6qejxtdg4y5r3zarvary0c5xw7kygt080", Network.RegTest),
        ForfeitPubKey: ECXOnlyPubKey.Create(new Key().PubKey.TaprootInternalKey.ToBytes()),
        CheckpointTapScript: new UnilateralPathArkTapScript(new Sequence(512), new NofNMultisigTapScript([])),
        // The live stack's own terms: TxFeeRate, offchain output, onchain output, offchain INPUT, onchain input.
        FeeTerms: new ArkOperatorFeeTerms("0", "0.0", "250.0", OffchainInputFee, OffchainInputFee),
        Digest: "",
        MaxTxWeight: 40_000);
}
