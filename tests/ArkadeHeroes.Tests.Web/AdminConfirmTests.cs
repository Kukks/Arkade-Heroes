using AngleSharp.Dom;
using ArkadeHeroes.Shared;
using ArkadeHeroes.Web.Pages;
using Bunit;

namespace ArkadeHeroes.Tests.Web;

/// <summary>
/// The operator console asks before the one action a mis-click cannot take back — and only that one.
///
/// <para>The classification is the whole point, and getting it wrong in EITHER direction is a defect. A
/// prompt in front of an idempotent no-op is not free caution: it teaches an operator that prompts on this
/// page are noise, which is exactly how the one that matters gets clicked through. So "no prompt on settle"
/// is pinned here as deliberately as "prompt on refund".</para>
///
/// <list type="bullet">
/// <item><b>Expire abandoned matches</b> — moves no money, and the same lazy reconcile already runs on every
/// <c>/api/matches</c> listing. No prompt.</item>
/// <item><b>Settle ended seasons</b> — pays real sats, but it is the same call an anonymous read of the
/// season board makes, and the settled marker advances under a lock before a sat moves. Cannot double-pay,
/// grants no capability a logged-out visitor lacks. No prompt.</item>
/// <item><b>Refund entrants</b> — marks the bracket refunded DURABLY before paying, so the bracket can never
/// be played afterwards even if whatever stranded it comes back. Irreversible. Prompts.</item>
/// </list>
/// </summary>
public class AdminConfirmTests
{
    private const string Token = "operator-token";
    private const string Bracket = "t-stranded";
    private const string RefundRoute = "POST /api/admin/tournaments/t-stranded/refund";

    /// <summary>A FULL bracket with no fill-time snapshots — the shape the server's own gate calls stranded.</summary>
    private static AdminTournamentDto Stranded => new(Bracket, "full", BuyInSats: 1_000, Size: 4, Entrants: 4,
        HasEntrantSnapshots: false);

    private static AdminOverviewDto Overview => new(
        GeneratedAtUnix: 0,
        Economy: new EconomyHealthDto(0, 0, 0, new Dictionary<string, long>(), new Dictionary<string, long>(), 0),
        Players: new AdminPlayersDto(0, 0, 0, 0),
        HeroesByGeneration: [],
        HeroesByRarity: [],
        Market: new AdminMarketDto(0, 0, 0, 0, 0),
        Flows: [],
        Season: new SeasonLeaderboardDto(1, 0, 0, [], null),
        Tournaments: [Stranded]);

    /// <summary>Signs into the console and waits for the management surface, so each test starts at the row.</summary>
    private static (PageTestContext Ctx, IRenderedComponent<Admin> Cut) Console()
    {
        var ctx = new PageTestContext();
        ctx.Api.Get("/api/admin/overview", Overview);

        var cut = ctx.Render<Admin>();
        cut.Find("input").Input(Token);
        cut.Find("button").Click();
        cut.WaitForAssertion(() => Assert.Contains("Management", cut.Markup));
        return (ctx, cut);
    }

    /// <summary>Buttons are found by what they SAY: the console's rendered-component internals are not public,
    /// and the label is the thing an operator actually acts on.</summary>
    private static IElement Button(IRenderedComponent<Admin> cut, string text) =>
        cut.FindAll("button").First(b => b.TextContent.Contains(text));

    private static IElement? MaybeButton(IRenderedComponent<Admin> cut, string text) =>
        cut.FindAll("button").FirstOrDefault(b => b.TextContent.Contains(text));

    /// <summary>The two management actions are both a button that says "Run", so they are told apart by the
    /// row they sit in — which is also the only thing distinguishing them on screen.</summary>
    private static IElement RowButton(IRenderedComponent<Admin> cut, string rowLabel) =>
        cut.FindAll(".match-row")
            .First(r => r.TextContent.Contains(rowLabel))
            .QuerySelectorAll("button")
            .First(b => b.TextContent.Contains("Run"));

    /// <summary>
    /// The defect itself: one click used to be a refund. Asserted on the REQUEST, not on any rendered text —
    /// a prompt that renders but does not actually hold the call back is no prompt at all.
    /// </summary>
    [Fact]
    public void RefundingABracket_DoesNotFireOnTheFirstClick()
    {
        var (ctx, cut) = Console();

        Button(cut, "Refund entrants").Click();

        Assert.DoesNotContain(RefundRoute, ctx.Api.Requested);
        ctx.Dispose();
    }

    /// <summary>And the second click does go through — a confirm that cannot be completed is a broken button.</summary>
    [Fact]
    public void RefundingABracket_FiresOnTheSecondClick()
    {
        var (ctx, cut) = Console();
        ctx.Api.Post($"/api/admin/tournaments/{Bracket}/refund", new TournamentRefundResponse(
            new TournamentDto(Bracket, "player-1", 1_000, 4, 4, "refunded", [], null),
            EntrantsRefunded: 4, RefundedSats: 4_000));

        Button(cut, "Refund entrants").Click();
        Button(cut, "Yes — end it and pay back").Click();

        cut.WaitForAssertion(() => Assert.Contains(RefundRoute, ctx.Api.Requested));
        ctx.Dispose();
    }

    /// <summary>
    /// The prompt has to say what is being risked, or it is just an extra click. It names the bracket —
    /// these rows differ only by an id nobody reads carefully — and says the thing that makes this action
    /// different from the other two: it cannot be undone.
    /// </summary>
    [Fact]
    public void TheRefundPrompt_SaysWhatCannotBeUndoneAndToWhich()
    {
        var (ctx, cut) = Console();

        Button(cut, "Refund entrants").Click();

        Assert.Contains("cannot be undone", cut.Markup);
        Assert.Contains("permanently ends", cut.Markup);
        Assert.Contains(Bracket, cut.Markup);
        ctx.Dispose();
    }

    /// <summary>Backing out leaves the bracket alone — including not having quietly fired on the way in.</summary>
    [Fact]
    public void CancellingARefund_LeavesTheBracketAlone()
    {
        var (ctx, cut) = Console();

        Button(cut, "Refund entrants").Click();
        Button(cut, "Cancel").Click();

        Assert.DoesNotContain(RefundRoute, ctx.Api.Requested);
        Assert.Null(MaybeButton(cut, "Yes — end it and pay back"));
        ctx.Dispose();
    }

    /// <summary>
    /// The other half of the rule, and the one a later "let's be safe and prompt everywhere" change would
    /// break: settle pays real sats and STILL does not ask, because a public board read already triggers the
    /// same idempotent settle. Pinned so the no-prompt call has to be argued with rather than drifted past.
    /// </summary>
    [Fact]
    public void SettlingSeasons_AsksNothingBecauseAPublicReadAlreadyTriggersIt()
    {
        var (ctx, cut) = Console();
        ctx.Api.Post("/api/admin/actions/settle-seasons", new AdminActionResultDto("settle-seasons", "Nothing was due."));

        RowButton(cut, "Settle ended seasons").Click();

        cut.WaitForAssertion(() => Assert.Contains("POST /api/admin/actions/settle-seasons", ctx.Api.Requested));
        ctx.Dispose();
    }

    /// <summary>Same for the reconcile, which moves no money at all.</summary>
    [Fact]
    public void ExpiringAbandonedMatches_AsksNothingBecauseItMovesNoMoney()
    {
        var (ctx, cut) = Console();
        ctx.Api.Post("/api/admin/actions/reconcile-matches", new AdminActionResultDto("reconcile-matches", "0 → 0."));

        RowButton(cut, "Expire abandoned matches").Click();

        cut.WaitForAssertion(() => Assert.Contains("POST /api/admin/actions/reconcile-matches", ctx.Api.Requested));
        ctx.Dispose();
    }
}
