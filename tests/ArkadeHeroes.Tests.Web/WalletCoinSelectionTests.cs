using ArkadeHeroes.Web.Wallet;
using NArk.Abstractions;
using NArk.Abstractions.Blockchain;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core;
using NArk.Core.Contracts;
using NArk.Core.Scripts;
using NArk.Core.Services;
using NArk.Core.Transport;
using NBitcoin;
using NBitcoin.Secp256k1;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace ArkadeHeroes.Tests.Web;

/// <summary>
/// Which coins the browser wallet reaches for, and what it does when arkd refuses them.
///
/// <para>This is the one piece of the wallet that is pure logic, and it is also the piece where a
/// mistake is unrecoverable: the coins carry the player's heroes, so a spend that picks the wrong one
/// hands a hero to whoever the sats were going to. Everything here is asserted against the SPEND that
/// actually reaches the SDK — the inputs handed to <see cref="ISpendingService.Spend(string, ArkCoin[],
/// ArkTxOut[], System.Threading.CancellationToken)"/> — rather than against an internal helper, because
/// the inputs are the thing that ends up signed.</para>
/// </summary>
public class WalletCoinSelectionTests
{
    private const long Dust = 1_000;
    private const string WalletId = "w1";
    private const string HeroAsset = "hero-1";
    // A well-formed regtest Arkade address; the tests never send anywhere real, they only need parsing
    // to succeed so selection is what is under test.
    private static readonly string Destination = Address();

    // ── The two gates that keep a hero off the fee address ───────────────────────────────────────

    [Fact]
    public async Task PureBtcCoinIsPreferred_AndTheHeroCarrierIsLeftAlone()
    {
        var carrier = Coin(50_000, HeroAsset);
        var cash = Coin(20_000);
        var (wallet, spending) = Wallet(carrier, cash);

        await wallet.SendSatsAsync(WalletId, Destination, 5_000);

        Assert.Equal([cash], Inputs(spending));
    }

    [Fact]
    public async Task NoPureBtcCoinLeft_SpendsTheCarrier()
    {
        // The state a settlement batch leaves: one VTXO holding the sats AND the heroes.
        var carrier = Coin(50_000, HeroAsset);
        var (wallet, spending) = Wallet(carrier);

        await wallet.SendSatsAsync(WalletId, Destination, 5_000);

        Assert.Equal([carrier], Inputs(spending));
    }

    [Fact]
    public async Task CarrierWhoseChangeWouldFallBelowDust_IsRefused()
    {
        // 5,400 sats against a 5,000 fee leaves 400 — under dust. The SDK would move that change to an
        // OP_RETURN at vout 0 while the asset packet still assigns asset change to the LAST output, so
        // the hero would land on the fee recipient. Refuse the spend instead.
        var carrier = Coin(5_400, HeroAsset);
        var (wallet, spending) = Wallet(carrier);

        var ex = await Assert.ThrowsAsync<GameWalletException>(
            () => wallet.SendSatsAsync(WalletId, Destination, 5_000));

        Assert.Contains("Not enough spendable sats", ex.Message);
        await spending.DidNotReceiveWithAnyArgs().Spend(default!, default(ArkCoin[])!, default!);
    }

    [Fact]
    public async Task CarrierLeavingExactlyDustAsChange_IsSpent()
    {
        // The boundary the guard is written against: change == dust is a real VTXO, so it is allowed,
        // and the hero rides home on it.
        var carrier = Coin(6_000, HeroAsset);
        var (wallet, spending) = Wallet(carrier);

        await wallet.SendSatsAsync(WalletId, Destination, 5_000);

        Assert.Equal([carrier], Inputs(spending));
    }

    [Fact]
    public async Task BtcCoinsAreTakenUntilTheChangeClearsDust_NotJustUntilTheOutputsAreCovered()
    {
        // 5,400 covers the 5,000 fee but would leave 400 of change — under dust. Sub-dust change is
        // exactly what misplaces asset change, so selection keeps going rather than stopping at "covered".
        var small = Coin(5_400);
        var second = Coin(3_000);
        var (wallet, spending) = Wallet(small, second);

        await wallet.SendSatsAsync(WalletId, Destination, 5_000);

        Assert.Equal(2, Inputs(spending).Length);
    }

    [Fact]
    public async Task OneCoinThatAlreadyClearsDust_DoesNotDragInASpare()
    {
        // The spare used to be taken unconditionally. It is not free: the SDK puts a one-MINUTE lock on
        // every input before submitting and never lifts it, so a coin the spend did not need comes back
        // locked — and that is the coin a retry reaches for after losing a race.
        var big = Coin(50_000);
        var spare = Coin(40_000);
        var (wallet, spending) = Wallet(big, spare);

        await wallet.SendSatsAsync(WalletId, Destination, 5_000);

        Assert.Equal([big], Inputs(spending));
    }

    [Fact]
    public async Task SendingOneHeroOffACoinCarryingTwo_IsRefusedWhenTheChangeWouldNotSurvive()
    {
        // 1,500 sats and two heroes. Sending one costs dust (1,000), which leaves 500 of change — under
        // dust, so the SDK would move it to vout 0 while the asset packet still assigns the SECOND hero
        // to the last output. That last output is the recipient.
        var carrier = Coin(1_500, HeroAsset, units: 2);
        var (wallet, spending) = Wallet(carrier);

        var ex = await Assert.ThrowsAsync<GameWalletException>(
            () => wallet.SendAssetAsync(WalletId, Destination, HeroAsset));

        Assert.Contains("another hero", ex.Message);
        await spending.DidNotReceiveWithAnyArgs().Spend(default!, default(ArkCoin[])!, default!);
    }

    [Fact]
    public async Task SendingOneHeroOffACoinCarryingTwo_GoesThroughOnceTheChangeClearsDust()
    {
        // The same send with room to leave a real change output: allowed, and the second hero rides it.
        var carrier = Coin(9_000, HeroAsset, units: 2);
        var (wallet, spending) = Wallet(carrier);

        await wallet.SendAssetAsync(WalletId, Destination, HeroAsset);

        Assert.Equal([carrier], Inputs(spending));
    }

    [Fact]
    public async Task SendingTheLastHeroOffACoin_IsNotBlockedByTheGuard()
    {
        // Nothing is left behind, so there is no asset change to misplace — a thin carrier is fine.
        var carrier = Coin(1_500, HeroAsset);
        var (wallet, spending) = Wallet(carrier);

        await wallet.SendAssetAsync(WalletId, Destination, HeroAsset);

        Assert.Equal([carrier], Inputs(spending));
    }

    // ── Losing the race for your own coins ───────────────────────────────────────────────────────

    [Fact]
    public async Task ArkdRefusingASpentInput_RetriesAgainstWhatTheWalletActuallyHolds()
    {
        var stale = Coin(50_000);
        var fresh = Coin(40_000);
        var transport = Transport();
        var spending = Substitute.For<ISpendingService>();

        // First read offers the coin the batch has already taken; once arkd says so, the wallet re-reads
        // and finds the settlement's replacement.
        var reads = 0;
        spending.GetAvailableCoins(WalletId).Returns(_ =>
            Task.FromResult<IReadOnlySet<ArkCoin>>(++reads == 1 ? new HashSet<ArkCoin> { stale } : [fresh]));

        var attempts = 0;
        spending.Spend(WalletId, Arg.Any<ArkCoin[]>(), Arg.Any<ArkTxOut[]>())
            .Returns(_ => ++attempts == 1
                ? throw new InvalidOperationException(
                    "Status(StatusCode=\"InvalidArgument\", Detail=\"VTXO_ALREADY_SPENT (6): abc:0 already spent\")")
                : Task.FromResult(uint256.One));

        var txId = await NewWallet(transport, spending).SendSatsAsync(WalletId, Destination, 5_000);

        Assert.Equal(uint256.One.ToString(), txId);
        Assert.Equal(2, attempts);
        Assert.Equal([fresh], Inputs(spending, call: 2));
    }

    [Fact]
    public async Task ARaceItNeverWins_TellsThePlayerWhatHappened_NotWhatTheSdkSaid()
    {
        var (wallet, spending) = Wallet(Coin(50_000));
        spending.Spend(WalletId, Arg.Any<ArkCoin[]>(), Arg.Any<ArkTxOut[]>())
            .ThrowsAsync(new InvalidOperationException(
                "net_http_message_not_success_statuscode_reason, 400, VTXO_ALREADY_REGISTERED (4)"));

        var ex = await Assert.ThrowsAsync<GameWalletException>(
            () => wallet.SendSatsAsync(WalletId, Destination, 5_000));

        Assert.DoesNotContain("net_http_message_not_success", ex.Message);
        Assert.DoesNotContain("VTXO_ALREADY_REGISTERED", ex.Message);
        Assert.DoesNotContain("400", ex.Message);
        Assert.Contains("settled", ex.Message);
        // The detail is not thrown away — it rides on the inner exception for the console.
        Assert.Contains("VTXO_ALREADY_REGISTERED", ex.InnerException!.ToString());
    }

    [Fact]
    public async Task AFailureThatIsNotARace_IsNotRetried_AndStillReadsAsEnglish()
    {
        var (wallet, spending) = Wallet(Coin(50_000));
        spending.Spend(WalletId, Arg.Any<ArkCoin[]>(), Arg.Any<ArkTxOut[]>())
            .ThrowsAsync(new HttpRequestException("net_http_message_not_success_statuscode_reason, 500"));

        var ex = await Assert.ThrowsAsync<GameWalletException>(
            () => wallet.SendSatsAsync(WalletId, Destination, 5_000));

        Assert.DoesNotContain("net_http_message_not_success", ex.Message);
        await spending.Received(1).Spend(WalletId, Arg.Any<ArkCoin[]>(), Arg.Any<ArkTxOut[]>());
    }

    // ── The balance the pill promises ────────────────────────────────────────────────────────────

    [Fact]
    public async Task BalanceCountsOnlyWhatASpendCouldActuallyReach()
    {
        var spendable = Coin(30_000);
        var settling = Coin(70_000, swept: true);   // recoverable — CanSpendOffchain is false
        var (wallet, _) = Wallet(spendable, settling);

        Assert.Equal(30_000, await wallet.GetBalanceAsync(WalletId));
        Assert.Equal((30_000, 70_000), await wallet.GetBalanceBreakdownAsync(WalletId));
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────────────────────────

    private static ArkCoin[] Inputs(ISpendingService spending, int call = 1) =>
        (ArkCoin[])spending.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ISpendingService.Spend) && c.GetArguments().Length == 4)
            .ElementAt(call - 1).GetArguments()[1]!;

    private static (GameWallet Wallet, ISpendingService Spending) Wallet(params ArkCoin[] coins)
    {
        var spending = Substitute.For<ISpendingService>();
        spending.GetAvailableCoins(WalletId).Returns(new HashSet<ArkCoin>(coins));
        spending.Spend(WalletId, Arg.Any<ArkCoin[]>(), Arg.Any<ArkTxOut[]>()).Returns(uint256.One);
        return (NewWallet(Transport(), spending), spending);
    }

    private static GameWallet NewWallet(IClientTransport transport, ISpendingService spending)
    {
        // The retry's backoff is real time; tests assert the behaviour, not the wait.
        GameWallet.RetryBackoff = TimeSpan.Zero;
        return new GameWallet(
            Substitute.For<IWalletStorage>(), transport, spending,
            Substitute.For<IVtxoStorage>(), Substitute.For<IContractService>());
    }

    private static IClientTransport Transport()
    {
        var transport = Substitute.For<IClientTransport>();
        transport.GetServerInfoAsync(Arg.Any<CancellationToken>()).Returns(ServerInfo());
        transport.GetVtxosByOutpoints(Arg.Any<IReadOnlyCollection<OutPoint>>(), Arg.Any<bool>(),
            Arg.Any<CancellationToken>()).Returns(AsyncEnumerable.Empty<ArkVtxo>());
        return transport;
    }

    private static uint _index;

    private static ArkCoin Coin(long sats, string? assetId = null, bool swept = false, ulong units = 1)
    {
        var serverKey = ECXOnlyPubKey.Create(new Key().PubKey.TaprootInternalKey.ToBytes());
        var script = new GenericTapScript([Op.GetPushOp(1), OpcodeType.OP_TRUE]);
        var contract = new GenericArkContract(serverKey.ToOutputDescriptor(Network.RegTest), [script]);
        return new ArkCoin(
            walletIdentifier: WalletId,
            contract: contract,
            birth: DateTimeOffset.UtcNow,
            expiresAt: DateTimeOffset.UtcNow.AddDays(30),
            expiresAtHeight: null,
            outPoint: new OutPoint(uint256.One, _index++),
            txOut: new TxOut(Money.Satoshis(sats), Script.Empty),
            signerDescriptor: null,
            spendingScriptBuilder: script,
            spendingConditionWitness: null,
            lockTime: null,
            sequence: null,
            swept: swept,
            unrolled: false,
            assets: assetId is null ? null : [new VtxoAsset(assetId, units)]);
    }

    private static string Address()
    {
        var serverKey = ECXOnlyPubKey.Create(new Key().PubKey.TaprootInternalKey.ToBytes());
        var script = new GenericTapScript([Op.GetPushOp(1), OpcodeType.OP_TRUE]);
        return new GenericArkContract(serverKey.ToOutputDescriptor(Network.RegTest), [script])
            .GetArkAddress().ToString(false);
    }

    private static ArkServerInfo ServerInfo() => new(
        Dust: Money.Satoshis(Dust),
        SignerKey: ECXOnlyPubKey.Create(new Key().PubKey.TaprootInternalKey.ToBytes())
            .ToOutputDescriptor(Network.RegTest),
        DeprecatedSigners: [],
        Network: Network.RegTest,
        UnilateralExit: new Sequence(144),
        BoardingExit: new Sequence(144),
        ForfeitAddress: BitcoinAddress.Create("bcrt1qw508d6qejxtdg4y5r3zarvary0c5xw7kygt080", Network.RegTest),
        ForfeitPubKey: ECXOnlyPubKey.Create(new Key().PubKey.TaprootInternalKey.ToBytes()),
        CheckpointTapScript: new UnilateralPathArkTapScript(new Sequence(144), new NofNMultisigTapScript([])),
        FeeTerms: new ArkOperatorFeeTerms("1", "0", "0", "0", "0"),
        Digest: "");
}
