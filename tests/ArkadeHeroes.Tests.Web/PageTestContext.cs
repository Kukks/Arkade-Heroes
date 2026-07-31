using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Shared;
using ArkadeHeroes.Web.Wallet;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core.Services;
using NArk.Core.Transport;
using NSubstitute;

namespace ArkadeHeroes.Tests.Web;

/// <summary>
/// The DI graph a page actually renders against, assembled once so each test states only the facts it
/// cares about.
///
/// <para>The game services (<see cref="WalletState"/>, <see cref="GameSession"/>, the SDK client) are the
/// REAL types — a page bug and a page's contract with its own services are the same bug, and substituting
/// them would test the substitute. Only the boundary is faked: the NArk storage interfaces underneath the
/// wallet, and the HTTP transport under the SDK client (see <see cref="FakeApi"/>).</para>
///
/// <para><see cref="GameWallet"/> is a concrete class over five NArk interfaces, so it is constructed for
/// real over substituted interfaces rather than mocked itself.</para>
/// </summary>
public class PageTestContext : BunitContext
{
    public FakeApi Api { get; } = new();

    public WalletState State { get; }

    /// <summary>
    /// The player's browser wallet. Substituted at the FACADE rather than under it: its read methods
    /// bottom out in NArk types (a server info record, a derived contract) that cannot be constructed
    /// without a live Ark server, so faking the layer below would mean hand-building most of the SDK.
    /// Defaults to "no wallet in this browser"; <see cref="WithWallet"/> puts one there.
    /// </summary>
    public GameWallet Wallet { get; }

    public PageTestContext(string network = "regtest")
    {
        // bUnit's JSInterop is loose by default in these tests: pages call small helpers (clipboard,
        // localStorage, motion) whose return values none of these assertions depend on. A test that DOES
        // care about an interop call sets it up explicitly.
        JSInterop.Mode = JSRuntimeMode.Loose;

        var wallet = Substitute.For<GameWallet>(
            Substitute.For<IWalletStorage>(),
            Substitute.For<IClientTransport>(),
            Substitute.For<ISpendingService>(),
            Substitute.For<IVtxoStorage>(),
            Substitute.For<IContractService>());
        Wallet = wallet;
        wallet.GetActiveWalletAsync().Returns((ArkWalletInfo?)null);
        wallet.HasWalletAsync().Returns(false);

        State = new WalletState(
            Substitute.For<IVtxoStorage>(),
            Substitute.For<IContractStorage>(),
            JSInterop.JSRuntime,
            wallet);

        var http = Api.CreateClient();
        var sdk = new ArkadeHeroesClient(http);

        Services.AddSingleton(http);
        Services.AddSingleton(sdk);
        Services.AddSingleton(wallet);
        Services.AddSingleton(State);
        Services.AddSingleton<TermsState>();
        Services.AddSingleton(new ArkNetworkInfo(network));
        Services.AddSingleton(sp => new FaucetService(
            new HttpClient(), sp.GetRequiredService<ArkNetworkInfo>(), wallet, State));
        Services.AddSingleton<WalletCredentialStore>();
        Services.AddSingleton(sp => new GameSession(sdk, wallet, State, sp.GetRequiredService<TermsState>(), sp, http));
    }

    /// <summary>Put the page in the signed-in state, with a balance, the way the shell does after login.</summary>
    public PageTestContext SignIn(PlayerDto? player = null, long balanceSats = 0)
    {
        State.SetActiveWallet("wallet-1", TestAddress);
        State.SetPlayer(player ?? Fixtures.Player());
        State.UpdateBalance(balanceSats);
        return this;
    }

    private const string TestAddress = "tark1qtestaddressfortestsonly0000000000000000";

    /// <summary>
    /// Give the browser a loaded wallet holding <paramref name="balanceSats"/>. This is what makes a page
    /// that owns a wallet reach its READY view instead of its "create a wallet" one.
    /// </summary>
    public PageTestContext WithWallet(long balanceSats)
    {
        var info = new ArkWalletInfo("wallet-1", Secret: null, Destination: null,
            WalletType.SingleKey, AccountDescriptor: null, LastUsedIndex: 0);

        Wallet.GetActiveWalletAsync().Returns(info);
        Wallet.HasWalletAsync().Returns(true);
        Wallet.GetWalletsAsync().Returns(new HashSet<ArkWalletInfo> { info });
        Wallet.GetReceiveAddressAsync("wallet-1").Returns(TestAddress);
        Wallet.GetBalanceAsync("wallet-1").Returns(balanceSats);
        Wallet.GetVtxosAsync("wallet-1").Returns(Array.Empty<NArk.Abstractions.VTXOs.ArkVtxo>());
        Wallet.GetAssetsAsync("wallet-1").Returns(Array.Empty<(string AssetId, ulong Amount)>());
        State.UpdateBalance(balanceSats);
        return this;
    }
}
