using ArkadeHeroes.Shared;
using ArkadeHeroes.Web.Wallet;
using Microsoft.Extensions.DependencyInjection;

namespace ArkadeHeroes.Tests.Web;

/// <summary>
/// A SUCCESSFUL death-match used to end with fourteen console errors behind it.
///
/// <para>Staking is a real spend and takes time to settle into arkd's indexer, so the settle has to wait.
/// The browser's only way to find out whether it could stop waiting was to POST the settle and read the
/// 400 — and Chromium logs a console error for every failed fetch, so a run that worked perfectly looked
/// exactly like fourteen failures. That is not cosmetic: it is how a team learns to stop reading the
/// console, and the browser walk suite added in 5044a1c asserts zero console errors on every page, so it
/// is a gate too.</para>
///
/// <para>These run the REAL <see cref="GameSession"/> over the transport-level <see cref="FakeApi"/>, so
/// what they assert is the sequence of HTTP calls a browser would actually make — which is the thing the
/// console was reporting on.</para>
/// </summary>
public class DeathMatchSettleNoiseTests
{
    private const string Dm = "dm-1";
    private static readonly string Readiness = $"GET /api/deathmatch/{Dm}/readiness";
    private static readonly string Settle = $"POST /api/deathmatch/{Dm}/settle";

    private static GameSession SessionOf(PageTestContext ctx) => ctx.Services.GetRequiredService<GameSession>();

    /// <summary>
    /// A settle response that is well-formed but stamped with a config version this fake server does not
    /// serve, so the fairness audit reports "cannot verify" instead of running. What the outcome SAYS is
    /// not what these tests are about; the request sequence is.
    /// </summary>
    private static DeathMatchSettleResponse SettleResponse() => new(
        Result: new BattleResultDto("hero-a", "hero-b", Turns: 3, Events: [], WinnerRemainingHp: 40, WinnerMaxHp: 100),
        WinnerHeroId: "hero-a",
        LoserHeroId: "hero-b",
        ChallengerSnapshot: Fixtures.Hero("hero-a", "Ashfang"),
        DefenderSnapshot: Fixtures.Hero("hero-b", "Direbloom", ownerId: "player-2"),
        ServerSeedHex: "00",
        EntropyHex: "00",
        Receipt: null,
        ConfigVersion: "aaaaaaaaaaaaaaaa");

    private static DeathMatchReadinessDto Ready() => new(true, true, true, true, false);
    private static DeathMatchReadinessDto NotReady() => new(false, false, false, false, false);

    /// <summary>
    /// The fix, stated as a contract: the client ASKS whether a settle would land before it attempts one.
    /// </summary>
    [Fact]
    public async Task TheSettleAsksWhetherItIsReadyBeforeItAttempts()
    {
        using var ctx = new PageTestContext();
        ctx.SignIn();
        ctx.Api.Get($"/api/deathmatch/{Dm}/readiness", Ready());
        ctx.Api.Post($"/api/deathmatch/{Dm}/settle", SettleResponse());

        await SessionOf(ctx).SettleDeathMatchAsync(Dm);

        var asked = ctx.Api.Requested.IndexOf(Readiness);
        var attempted = ctx.Api.Requested.IndexOf(Settle);
        Assert.True(asked >= 0, $"the client never asked. Calls: {string.Join(", ", ctx.Api.Requested)}");
        Assert.True(attempted > asked,
            $"the client attempted before it asked. Calls: {string.Join(", ", ctx.Api.Requested)}");

        // And having asked, it attempts exactly once — the fourteen were the retries.
        Assert.Equal(1, ctx.Api.Requested.Count(r => r == Settle));
    }

    /// <summary>
    /// The state that produced the noise: stakes in flight, escrow not yet observed funded. Not one settle
    /// may be attempted while the answer is "not yet" — attempting IS the console error.
    /// </summary>
    [Fact]
    public async Task WhileTheEscrowIsUnfunded_NotOneSettleIsAttempted()
    {
        using var ctx = new PageTestContext();
        ctx.SignIn();
        ctx.Api.Get($"/api/deathmatch/{Dm}/readiness", NotReady());
        ctx.Api.Post($"/api/deathmatch/{Dm}/settle", SettleResponse());

        var result = await SessionOf(ctx).WaitForDeathMatchReadyAsync(
            Dm, pollEvery: TimeSpan.FromMilliseconds(10), attempts: 14);

        Assert.Null(result);   // it gave up rather than deciding it was ready
        Assert.Equal(14, ctx.Api.Requested.Count(r => r == Readiness));
        Assert.DoesNotContain(Settle, ctx.Api.Requested);
    }

    /// <summary>It stops the moment the answer changes — a wait that kept polling a ready escrow would
    /// just be a slower flow, and a wait that kept polling a RESOLVED one would never end.</summary>
    [Fact]
    public async Task ItStopsAskingOnceTheAnswerIsYes_AndOnceThereIsNothingLeftToSettle()
    {
        using var ready = new PageTestContext();
        ready.SignIn();
        ready.Api.Get($"/api/deathmatch/{Dm}/readiness", Ready());
        Assert.True((await SessionOf(ready).WaitForDeathMatchReadyAsync(
            Dm, pollEvery: TimeSpan.FromMilliseconds(10), attempts: 14))!.Ready);
        Assert.Equal(1, ready.Api.Requested.Count(r => r == Readiness));

        using var done = new PageTestContext();
        done.SignIn();
        done.Api.Get($"/api/deathmatch/{Dm}/readiness", new DeathMatchReadinessDto(true, true, true, false, true));
        var answer = await SessionOf(done).WaitForDeathMatchReadyAsync(
            Dm, pollEvery: TimeSpan.FromMilliseconds(10), attempts: 14);
        Assert.True(answer!.Completed);
        Assert.Equal(1, done.Api.Requested.Count(r => r == Readiness));
    }

    /// <summary>
    /// A server that has never heard of this endpoint answers 404. That must cost exactly ONE failed read,
    /// not a fresh loop of them — the whole point is fewer console errors, and a wait that hammered a route
    /// that does not exist would have made the thing it was sent to fix strictly worse.
    /// </summary>
    [Fact]
    public async Task AServerWithoutTheEndpointCostsOneFailedRead_ThenHandsBackToTheSettle()
    {
        using var ctx = new PageTestContext();
        ctx.SignIn();
        // No readiness route registered at all: FakeApi answers 404, the way an older server would.
        ctx.Api.Post($"/api/deathmatch/{Dm}/settle", SettleResponse());

        await SessionOf(ctx).SettleDeathMatchAsync(Dm);

        Assert.Equal(1, ctx.Api.Requested.Count(r => r == Readiness));
        Assert.Equal(1, ctx.Api.Requested.Count(r => r == Settle));
    }
}
