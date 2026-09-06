using AngleSharp.Dom;
using ArkadeHeroes.Shared;
using ArkadeHeroes.Web.Pages;
using Bunit;

namespace ArkadeHeroes.Tests.Web;

/// <summary>
/// Payouts the game could not complete. The record and its endpoint both predate any client; nothing in the
/// browser ever read them, so sats owed to a named player were visible only to whoever could open the
/// container's SQLite file.
/// </summary>
public class AdminPayoutFailureTests
{
    private const string Token = "operator-token";
    private const string Route = "/api/admin/payout-failures";

    private static AdminOverviewDto Overview => new(
        GeneratedAtUnix: 0,
        Economy: new EconomyHealthDto(0, 0, 0, new Dictionary<string, long>(), new Dictionary<string, long>(), 0),
        Players: new AdminPlayersDto(0, 0, 0, 0),
        HeroesByGeneration: [],
        HeroesByRarity: [],
        Market: new AdminMarketDto(0, 0, 0, 0, 0),
        Flows: [],
        Season: new SeasonLeaderboardDto(1, 0, 0, [], null),
        Tournaments: []);

    private static PayoutFailureDto Row(long id, string outcome, long sats = 500, string player = "player-1") =>
        new(id, AtUnixSeconds: 1_760_000_000, PlayerId: player, AmountSats: sats,
            PayoutTag: $"tournament:t-{id}:rank1", Outcome: outcome, InvoiceId: null, Failure: "chain refused");

    private static (PageTestContext Ctx, IRenderedComponent<Admin> Cut) Console(PayoutFailurePageDto? page = null)
    {
        var ctx = new PageTestContext();
        ctx.Api.Get("/api/admin/overview", Overview);
        ctx.Api.Get("/api/admin/audit", new AuditPageDto([], NextAfter: 0, WriteFailures: 0));
        ctx.Api.Get(Route, page ?? new PayoutFailurePageDto(
            [Row(1, PayoutFailureOutcome.Owed)], NextAfter: 1, WriteFailures: 0));

        var cut = ctx.Render<Admin>();
        cut.Find("input").Input(Token);
        cut.Find("button").Click();
        cut.WaitForAssertion(() => Assert.Contains("Failed payouts", cut.Markup));
        return (ctx, cut);
    }

    private static IElement Button(IRenderedComponent<Admin> cut, string text) =>
        cut.FindAll("button").First(b => b.TextContent.Contains(text, StringComparison.Ordinal));

    [Fact]
    public void ReadingShowsWhoIsOwedWhat()
    {
        var (ctx, cut) = Console();
        using var _ = ctx;

        Button(cut, "failures").Click();

        cut.WaitForAssertion(() => Assert.Contains("player-1", cut.Markup));
        Assert.Contains("tournament:t-1:rank1", cut.Markup);
        Assert.Contains("settle by hand", cut.Markup);
    }

    [Fact]
    public void APaidButUnbookedRow_SaysNotToPayItAgain()
    {
        // The whole reason the outcome column exists: this row looks like a debt and is the one that must
        // never be settled. Rendering the bare status string would leave that to the reader.
        var (ctx, cut) = Console(new PayoutFailurePageDto(
            [Row(1, PayoutFailureOutcome.PaidNotBooked)], 1, 0));
        using var _ = ctx;

        Button(cut, "failures").Click();

        cut.WaitForAssertion(() => Assert.Contains("do NOT re-pay", cut.Markup));
    }

    [Fact]
    public void TheOwedTotal_CountsOnlyTheRowsThatAreActuallyOwed()
    {
        var (ctx, cut) = Console(new PayoutFailurePageDto(
            [Row(1, PayoutFailureOutcome.Owed, sats: 700),
             Row(2, PayoutFailureOutcome.PaidNotBooked, sats: 9_000),
             Row(3, PayoutFailureOutcome.Unknown, sats: 4_000)], 3, 0));
        using var _ = ctx;

        Button(cut, "failures").Click();

        cut.WaitForAssertion(() => Assert.Contains("700 sat owed", cut.Markup));
        Assert.DoesNotContain("13,700 sat owed", cut.Markup);
    }

    [Fact]
    public void NothingOnThisPageOffersToPayAnything()
    {
        // Read-only by design — a paid-not-booked row would be paid twice. Pins the ABSENCE, because the
        // natural next feature request is the one that must be refused.
        var (ctx, cut) = Console();
        using var _ = ctx;

        Button(cut, "failures").Click();
        cut.WaitForAssertion(() => Assert.Contains("player-1", cut.Markup));

        Assert.DoesNotContain(cut.FindAll("button"), b =>
            b.TextContent.Contains("Retry", StringComparison.OrdinalIgnoreCase)
            || b.TextContent.Contains("Pay", StringComparison.OrdinalIgnoreCase)
            || b.TextContent.Contains("Settle", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AFailedWriteIsRaised_BecauseDebtsAreMissingEntirely()
    {
        var (ctx, cut) = Console(new PayoutFailurePageDto(
            [Row(1, PayoutFailureOutcome.Owed)], 1, WriteFailures: 2));
        using var _ = ctx;

        Button(cut, "failures").Click();

        cut.WaitForAssertion(() => Assert.Contains("never captured", cut.Markup));
    }

    [Fact]
    public void FilteringToOwed_AsksTheServerForOwed()
    {
        var (ctx, cut) = Console();
        using var _ = ctx;
        ctx.Api.RequestedUrls.Clear();

        Button(cut, "owed").Click();

        cut.WaitForAssertion(() => Assert.Contains(ctx.Api.RequestedUrls, r =>
            r.Contains($"outcome={PayoutFailureOutcome.Owed}", StringComparison.Ordinal)));
    }

    [Fact]
    public void OlderStopsBeingOffered_OnceThereIsNothingOlder()
    {
        var (ctx, cut) = Console();
        using var _ = ctx;
        Button(cut, "failures").Click();
        cut.WaitForAssertion(() => Assert.Contains("player-1", cut.Markup));

        ctx.Api.Get(Route, new PayoutFailurePageDto([], NextAfter: 1, WriteFailures: 0));
        Button(cut, "Older").Click();

        cut.WaitForAssertion(() =>
            Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Contains("Older", StringComparison.Ordinal)));
    }

    [Fact]
    public void EditingThePlayerFilter_DoesNotRepageTheOldResults()
    {
        var (ctx, cut) = Console();
        using var _ = ctx;
        Button(cut, "failures").Click();
        cut.WaitForAssertion(() => Assert.Contains("player-1", cut.Markup));

        cut.FindAll("input").First(i => i.GetAttribute("placeholder") == "player id").Change("player-9");
        ctx.Api.RequestedUrls.Clear();
        Button(cut, "Older").Click();

        cut.WaitForAssertion(() =>
            Assert.DoesNotContain(ctx.Api.RequestedUrls, r => r.Contains("player-9", StringComparison.Ordinal)));
    }
}
