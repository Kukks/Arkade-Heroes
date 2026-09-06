using AngleSharp.Dom;
using ArkadeHeroes.Shared;
using ArkadeHeroes.Web.Pages;
using Bunit;

namespace ArkadeHeroes.Tests.Web;

/// <summary>
/// The append-only audit log has been written since it existed and read by nothing: server and SDK complete,
/// no caller anywhere in the browser. An operator wanting to know what happened to a hero, a match or a
/// player had to query SQLite by hand.
/// </summary>
public class AdminAuditTests
{
    private const string Token = "operator-token";

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

    private static AuditEventDto Event(long seq, string type, string? actor = "player-1") =>
        new(seq, AtUnixSeconds: 1_760_000_000, ActorPlayerId: actor, EventType: type,
            SubjectIds: ["hero-1"], PayloadJson: "{\"sats\":500}");

    private static (PageTestContext Ctx, IRenderedComponent<Admin> Cut) Console(AuditPageDto? audit = null)
    {
        var ctx = new PageTestContext();
        ctx.Api.Get("/api/admin/overview", Overview);
        ctx.Api.Get("/api/admin/audit",
            audit ?? new AuditPageDto([Event(1, "hero.minted")], NextAfter: 1, WriteFailures: 0));

        var cut = ctx.Render<Admin>();
        cut.Find("input").Input(Token);
        cut.Find("button").Click();
        cut.WaitForAssertion(() => Assert.Contains("Audit log", cut.Markup));
        return (ctx, cut);
    }

    private static IElement Button(IRenderedComponent<Admin> cut, string text) =>
        cut.FindAll("button").First(b => b.TextContent.Contains(text, StringComparison.Ordinal));

    [Fact]
    public void ReadingTheLogShowsWhatTheServerDid()
    {
        var (ctx, cut) = Console();
        using var _ = ctx;

        Button(cut, "Read").Click();

        cut.WaitForAssertion(() => Assert.Contains("hero.minted", cut.Markup));
        Assert.Contains("player-1", cut.Markup);
        Assert.Contains("sats", cut.Markup);
    }

    [Fact]
    public void AnEventWithNoActorReadsAsTheServer()
    {
        // A null actor is the server acting on its own — a lazy season settle, an expiry the chain forced.
        // Rendering it blank would read as missing data rather than as "nobody did this".
        var (ctx, cut) = Console(new AuditPageDto([Event(1, "match.expired", actor: null)], 1, 0));
        using var _ = ctx;

        Button(cut, "Read").Click();

        cut.WaitForAssertion(() => Assert.Contains("server", cut.Markup));
    }

    [Fact]
    public void AFailedAuditWrite_IsRaisedAsHistoryBeingLost()
    {
        // The log's own health, and the reason it is worth surfacing: every other number on this page still
        // looks fine while actions are happening that history cannot account for.
        var (ctx, cut) = Console(new AuditPageDto([Event(1, "hero.minted")], 1, WriteFailures: 3));
        using var _ = ctx;

        Button(cut, "Read").Click();

        cut.WaitForAssertion(() => Assert.Contains("FAILED", cut.Markup));
        Assert.Contains("3", cut.Markup);
    }

    [Fact]
    public void AnEmptyLogSaysSo_RatherThanLookingBroken()
    {
        var (ctx, cut) = Console(new AuditPageDto([], NextAfter: 0, WriteFailures: 0));
        using var _ = ctx;

        Button(cut, "Read").Click();

        cut.WaitForAssertion(() => Assert.Contains("Nothing recorded", cut.Markup));
    }

    [Fact]
    public void OlderStopsBeingOffered_OnceThereIsNothingOlder()
    {
        var (ctx, cut) = Console();
        using var _ = ctx;
        Button(cut, "Read").Click();
        cut.WaitForAssertion(() => Assert.Contains("hero.minted", cut.Markup));

        ctx.Api.Get("/api/admin/audit", new AuditPageDto([], NextAfter: 1, WriteFailures: 0));
        Button(cut, "Older").Click();

        cut.WaitForAssertion(() =>
            Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Contains("Older", StringComparison.Ordinal)));
    }

    [Fact]
    public void EditingAFilterDoesNotRepageTheOldResults()
    {
        var (ctx, cut) = Console();
        using var _ = ctx;
        Button(cut, "Read").Click();
        cut.WaitForAssertion(() => Assert.Contains("hero.minted", cut.Markup));

        cut.FindAll("input").First(i => i.GetAttribute("placeholder")!.Contains("subject")).Change("hero-9");
        ctx.Api.Requested.Clear();
        Button(cut, "Older").Click();

        cut.WaitForAssertion(() =>
            Assert.DoesNotContain(ctx.Api.Requested, r => r.Contains("/audit/subjects/", StringComparison.Ordinal)));
    }

    [Fact]
    public void ASubjectFilter_AsksTheSubjectEndpoint()
    {
        // "Everything that ever happened to THIS hero" is its own indexed endpoint, not a client-side filter.
        var (ctx, cut) = Console();
        using var _ = ctx;
        ctx.Api.Get("/api/admin/audit/subjects/hero-1",
            new AuditPageDto([Event(7, "hero.transferred")], NextAfter: 7, WriteFailures: 0));

        cut.FindAll("input").First(i => i.GetAttribute("placeholder")!.Contains("subject")).Change("hero-1");
        Button(cut, "Read").Click();

        cut.WaitForAssertion(() =>
            Assert.Contains(ctx.Api.Requested, r => r.Contains("/audit/subjects/hero-1", StringComparison.Ordinal)));
    }
}
