using ArkadeHeroes.Shared;
using ArkadeHeroes.Web.Pages;
using Bunit;

namespace ArkadeHeroes.Tests.Web;

/// <summary>
/// The empty-state-for-a-failed-read defect (see <see cref="RosterEmptyStateTests"/>), on the one page
/// where getting it wrong is worst.
///
/// <para>/reclaim exists so that escrowed value cannot go invisible. "Nothing stranded" and "we could not
/// look" are therefore not two shades of the same sentence — the first tells a player their stuck hero or
/// stake does not exist, and a player who believes it stops looking. The distinction IS the page.</para>
///
/// <para>A live browser walk rated this its least-trusted observation: the page read "Nothing stranded"
/// while <c>GET /api/players/me/reclaimable</c> had logged a 400 at boot. The text happened to be true —
/// that player really had nothing — so the walk proved the sentence, not the distinction. These tests
/// prove the distinction, in all three states the page can be in, with the failure arriving off the wire.</para>
/// </summary>
public class ReclaimEmptyStateTests
{
    private const string Reclaimable = "/api/players/me/reclaimable";

    /// <summary>The lie, in the wording this page can produce.</summary>
    private const string TheLie = "Nothing stranded";

    private static ReclaimableDto Stranded(string id = "wager-1") =>
        new("wager", id, "A duel stake with nowhere to go", ReclaimAfterUnixSeconds: 0);

    /// <summary>
    /// The read failed. Anything escrowed is still escrowed, and the page must say it could not look
    /// rather than that there is nothing to look at.
    /// </summary>
    [Fact]
    public void WhenTheEscrowReadFails_SaysSoInsteadOfNothingStranded()
    {
        using var ctx = new PageTestContext();
        ctx.SignIn();
        ctx.Api.GetFails(Reclaimable);

        var cut = ctx.Render<Reclaim>();

        cut.WaitForAssertion(() => Assert.DoesNotContain("checking your escrows", cut.Markup));

        Assert.Contains("Couldn't check your escrows", cut.Markup);
        Assert.DoesNotContain(TheLie, cut.Markup);
        // And it must offer a way back — a recovery page that dead-ends on a hiccup is still a page that
        // lost you your money.
        Assert.Contains("Try again", cut.Markup);
    }

    /// <summary>A genuinely empty list still has to read as empty: the fix must not turn every quiet
    /// page into an alarm, or the alarm stops meaning anything.</summary>
    [Fact]
    public void WhenThereIsGenuinelyNothingStranded_SaysThat()
    {
        using var ctx = new PageTestContext();
        ctx.SignIn();
        ctx.Api.Get(Reclaimable, Array.Empty<ReclaimableDto>());

        var cut = ctx.Render<Reclaim>();

        cut.WaitForAssertion(() => Assert.Contains(TheLie, cut.Markup));
        Assert.DoesNotContain("Couldn't check your escrows", cut.Markup);
    }

    /// <summary>
    /// The state the live walk actually saw. Signed out there is no bearer token, so asking can only ever
    /// be a 400 — and the page used to ask anyway, which is what put that 400 in the console. It must not
    /// ask, and it must not answer the question either: a signed-out visitor has not been told anything
    /// about their escrows, so neither sentence is available to the page.
    /// </summary>
    [Fact]
    public void WhenSignedOut_NeitherAsksNorClaimsThereIsNothingStranded()
    {
        using var ctx = new PageTestContext();   // deliberately NOT signed in
        ctx.Api.GetFails(Reclaimable, System.Net.HttpStatusCode.BadRequest);

        var cut = ctx.Render<Reclaim>();

        cut.WaitForAssertion(() => Assert.DoesNotContain("checking your escrows", cut.Markup));

        Assert.DoesNotContain($"GET {Reclaimable}", ctx.Api.Requested);
        Assert.DoesNotContain(TheLie, cut.Markup);
        Assert.Contains("come back to check for stranded escrows", cut.Markup);
    }

    /// <summary>
    /// The cold-boot sequence underneath the one above: the shell resumes sign-in AFTER this page has
    /// initialised, so the first pass legitimately skips the read. The answer must arrive when sign-in
    /// does — a page that stays silent forever because it gave up before the token landed strands value
    /// just as effectively as one that lies about it.
    /// </summary>
    [Fact]
    public void WhenSignInResumesAfterTheColdBoot_TheEscrowsAreReadThen()
    {
        using var ctx = new PageTestContext();
        ctx.Api.Get(Reclaimable, new[] { Stranded() });

        var cut = ctx.Render<Reclaim>();
        cut.WaitForAssertion(() => Assert.DoesNotContain("checking your escrows", cut.Markup));
        Assert.DoesNotContain($"GET {Reclaimable}", ctx.Api.Requested);

        // The shell's deferred sign-in lands. WalletState raises OnChange, which is the page's cue.
        cut.InvokeAsync(() => ctx.SignIn());

        cut.WaitForAssertion(() => Assert.Contains("A duel stake with nowhere to go", cut.Markup));
        Assert.DoesNotContain(TheLie, cut.Markup);
    }
}
