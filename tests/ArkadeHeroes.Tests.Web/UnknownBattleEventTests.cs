using ArkadeHeroes.Shared;
using ArkadeHeroes.Web.Components;
using Bunit;

namespace ArkadeHeroes.Tests.Web;

/// <summary>
/// A combat beat this build has never heard of must not vanish from the replay.
///
/// <para><c>BattleEventKind</c> is an enum the engine APPENDS to — its own comment says so, and the
/// innate-v2 kinds were added exactly that way — and it crosses the wire as a STRING. So "the server emits
/// a kind this bundle does not know" is a routine consequence of deploying the two separately, not a
/// corruption. The arena's replay switch had no <c>default:</c>, so such a beat set no banner, moved
/// nothing, and logged a row whose entire text was <c>"T7 · "</c>.</para>
///
/// <para>That matters more than a cosmetic gap: the replay is the FAIRNESS PROOF. A fight that silently
/// omits a turn renders as a complete, correct fight, and there is nothing on screen to tell a player their
/// client could not read part of what it was asked to verify.</para>
/// </summary>
public class UnknownBattleEventTests
{
    /// <summary>The replay opens on a 900ms "FIGHT!" bell before the first beat, so every assertion here
    /// waits well past it. Generous rather than tight: this asserts CONTENT, never a rate, so a slow shared
    /// runner should make it take longer — never make it flake.</summary>
    private static readonly TimeSpan Replay = TimeSpan.FromSeconds(10);

    /// <summary>A kind from a future engine. Not a typo and not garbage — the shape of a real deploy skew.</summary>
    private const string FutureKind = "Petrified";

    private static HeroDto Red => Fixtures.Hero("hero-a", "Ashfang");
    private static HeroDto Blue => Fixtures.Hero("hero-b", "Direbloom", ownerId: "player-2");

    /// <summary>One beat aimed at BLUE. <paramref name="targetHpAfter"/> is deliberately a number the blue
    /// bar does not already show, so a test can tell "left alone" from "happened to match".</summary>
    private static BattleEventDto Beat(string kind, int targetHpAfter = 100, int damage = 0) => new(
        Turn: 1, ActorId: "hero-a", TargetId: "hero-b", Kind: kind, SkillId: "ember-jab",
        Damage: damage, Crit: false, Healed: 0, TargetHpAfter: targetHpAfter, Note: null);

    private static BattleResultDto Fight(params BattleEventDto[] events) =>
        new("hero-a", "hero-b", events.Length, events, WinnerRemainingHp: 100, WinnerMaxHp: 100);

    private static IRenderedComponent<BattleArena> Arena(BunitContext ctx, BattleResultDto fight) =>
        ctx.Render<BattleArena>(p => p
            .Add(a => a.A, Red)
            .Add(a => a.B, Blue)
            .Add(a => a.Fight, fight));

    /// <summary>The blue corner's HP readout, e.g. "100/100".</summary>
    private static string BlueHp(IRenderedComponent<BattleArena> cut) => cut.FindAll(".f-hp")[1].TextContent;

    /// <summary>
    /// The gap is NAMED. The kind is quoted because it is the single fact that makes an unreadable beat
    /// diagnosable from a screenshot — without it the line cannot tell anyone which event went unrendered.
    /// </summary>
    [Fact]
    public void AnUnrecognisedBeat_IsNamedInTheLogRatherThanRenderedAsAnEmptyRow()
    {
        using var ctx = new PageTestContext();

        var cut = Arena(ctx, Fight(Beat(FutureKind)));

        cut.WaitForAssertion(() => Assert.Contains("unrecognised beat", cut.Markup), Replay);
        Assert.Contains(FutureKind, cut.Markup);
    }

    /// <summary>
    /// And nothing is INVENTED for it. TargetHpAfter is truthful for every kind the engine emits today, but
    /// for a kind we cannot interpret we also cannot know TargetId names either of these two fighters — and
    /// the side lookup falls back to blue, so trusting the field would let an unreadable beat drain the blue
    /// bar to a number the engine never reported about it. A stale-but-honest bar beats a confident wrong one.
    /// </summary>
    [Fact]
    public void AnUnrecognisedBeat_DoesNotMoveTheHpBarsOnAGuess()
    {
        using var ctx = new PageTestContext();

        // 7 of 100: unmissable if the replay ever decides to trust this field for a beat it cannot read.
        var cut = Arena(ctx, Fight(Beat(FutureKind, targetHpAfter: 7, damage: 93)));

        cut.WaitForAssertion(() => Assert.Contains("unrecognised beat", cut.Markup), Replay);
        Assert.Equal("100/100", BlueHp(cut));
    }

    /// <summary>
    /// The other half of the same fence: a default that is too greedy would swallow the kinds this client
    /// DOES know and narrate the whole fight as unreadable. A recognised beat still tells its own story, and
    /// still moves the bar off the engine's authoritative number.
    /// </summary>
    [Fact]
    public void ARecognisedBeat_StillNarratesItselfAndStillMovesHp()
    {
        using var ctx = new PageTestContext();

        var cut = Arena(ctx, Fight(Beat("SkillUsed", targetHpAfter: 40, damage: 60)));

        cut.WaitForAssertion(() => Assert.Contains("hits with", cut.Markup), Replay);
        Assert.DoesNotContain("unrecognised beat", cut.Markup);
        Assert.Equal("40/100", BlueHp(cut));
    }
}
