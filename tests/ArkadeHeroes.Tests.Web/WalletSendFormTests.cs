using ArkadeHeroes.Web.Pages;
using Bunit;

namespace ArkadeHeroes.Tests.Web;

/// <summary>
/// REGRESSION 1 — a Send form rendered at zero balance.
///
/// <para>/wallet used to draw its full send form whatever the balance was, so a player with nothing in
/// the wallet got an address field, an amount field and a Send button whose only possible outcome was an
/// error from the network. The useful things at zero — the receive address and the faucet — were already
/// on the page; the form could only waste a click and report a failure the page already knew about.</para>
///
/// <para>The guard is one line in Wallet.razor (<c>@if (State.BalanceSats > 0)</c>), which is exactly the
/// kind of line a refactor drops silently: nothing fails to compile and the whole unit suite stays green,
/// because until this project existed nothing rendered the page.</para>
/// </summary>
public class WalletSendFormTests
{
    [Fact]
    public void AtZeroBalance_TheSendFormIsNotRendered()
    {
        using var ctx = new PageTestContext();
        ctx.WithWallet(balanceSats: 0);

        var cut = ctx.Render<Wallet>();

        cut.WaitForAssertion(() => Assert.Contains("Balance", cut.Markup));

        // The heading, the destination field and the button all have to be absent — a form that renders
        // its inputs but hides the heading is the same dead end.
        Assert.DoesNotContain("tark1… destination address", cut.Markup);
        Assert.Empty(cut.FindAll(".send-form"));
        Assert.DoesNotContain(">Send</h3>", cut.Markup);
    }

    /// <summary>
    /// The other half, and the half that stops the fix from being "delete the form": with sats on hand the
    /// form is the point of the page and must be there.
    /// </summary>
    [Fact]
    public void WithAPositiveBalance_TheSendFormIsRendered()
    {
        using var ctx = new PageTestContext();
        ctx.WithWallet(balanceSats: 5_000);

        var cut = ctx.Render<Wallet>();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".send-form")));
        Assert.Contains("tark1… destination address", cut.Markup);
    }

    /// <summary>
    /// The receive address is what a zero-balance wallet is FOR, so the fix must not have hidden it along
    /// with the form. This is the reason the form is safe to omit, so it is pinned rather than assumed.
    /// </summary>
    [Fact]
    public void AtZeroBalance_TheReceiveAddressIsStillOffered()
    {
        using var ctx = new PageTestContext();
        ctx.WithWallet(balanceSats: 0);

        var cut = ctx.Render<Wallet>();

        cut.WaitForAssertion(
            () => Assert.Contains("tark1qtestaddressfortestsonly0000000000000000", cut.Markup));
    }

    /// <summary>
    /// The other way out of a zero balance, on the one network that has a public faucet. Also pins the
    /// gate itself: on regtest and mainnet the button must NOT appear — offering it would POST a real
    /// receive address to a third-party service for nothing (see FaucetPolicy).
    /// </summary>
    [Theory]
    [InlineData("mutinynet", true)]
    [InlineData("regtest", false)]
    [InlineData("mainnet", false)]
    public void TheFaucetButtonAppearsOnlyWhereThereIsAFaucet(string network, bool expected)
    {
        Assert.Equal(expected, ArkadeHeroes.Shared.FaucetPolicy.IsAvailableOn(network));

        using var ctx = new PageTestContext(network);
        ctx.WithWallet(balanceSats: 0);

        var cut = ctx.Render<Wallet>();

        cut.WaitForAssertion(() => Assert.Contains("Balance", cut.Markup));
        Assert.Equal(expected, cut.Markup.Contains("Get test sats"));
    }
}
