namespace ArkadeHeroes.Shared;

/// <summary>A daily quest definition. Completion is DERIVED from the signed-receipt log
/// (no counters, no action hooks): a matching in-window receipt for one of the player's heroes.
/// <paramref name="WinnersOnly"/> quests also require the player's hero to be the ResultHeroId.</summary>
public readonly record struct DailyQuestDef(string Id, string Title, string ReceiptType, bool WinnersOnly);

/// <summary>One quest as shown to the client, with its sats bonus and whether it's done today.</summary>
public record DailyQuestDto(string Id, string Title, long BonusSats, bool Done);

/// <summary>Today's daily-loop state for the signed-in player.</summary>
/// <param name="HasHero">The claim's own precondition. Defaults TRUE so an older server that omits it
/// leaves the button as it was — a claim the server would honour must not be blocked by a missing field.</param>
public record DailyStatusDto(
    int DayIndex, long DayEndsUnix, bool ClaimedToday, int Streak,
    long BaseSats, IReadOnlyList<DailyQuestDto> Quests, long ClaimableNowSats, long ProjectedSats,
    bool HasHero = true);

/// <summary>The result of a claim: what was paid and the breakdown.</summary>
public record DailyClaimResultDto(
    long AwardedSats, int Streak, long BaseSats, long QuestBonusSats, int StreakBonusPct,
    IReadOnlyList<string> CompletedQuestIds);

/// <summary>The daily quest catalog + deterministic per-day selection + receipts-derived completion —
/// the daily analogue of <see cref="LeaderboardBuilder"/> (pure, receipt-driven, no server trust).</summary>
public static class DailyQuests
{
    public static readonly IReadOnlyList<DailyQuestDef> Catalog = new[]
    {
        new DailyQuestDef("duel-win",   "Win a duel",        "match",      true),
        new DailyQuestDef("gauntlet",   "Clear a gauntlet",  "gauntlet",   true),
        new DailyQuestDef("breed",      "Breed a hero",      "breeding",   false),
        new DailyQuestDef("merge",      "Merge two heroes",  "merge",      false),
        new DailyQuestDef("deathmatch", "Win a death-match", "deathmatch", true),
    };

    /// <summary>The day's quests — a stable rotation of <paramref name="count"/> from the catalog,
    /// keyed by <paramref name="dayIndex"/> so the set is fixed within a day but varies across days.</summary>
    public static IReadOnlyList<DailyQuestDef> ForDay(int dayIndex, int count)
    {
        count = Math.Clamp(count, 1, Catalog.Count);
        var start = ((dayIndex % Catalog.Count) + Catalog.Count) % Catalog.Count;
        return Enumerable.Range(0, count).Select(k => Catalog[(start + k) % Catalog.Count]).ToList();
    }

    /// <summary>Complete iff a matching in-window receipt exists for one of the player's heroes.
    /// WinnersOnly quests additionally require the player's hero to be the ResultHeroId. A death-match
    /// won with a trait absorb issues an "absorb" receipt (not "deathmatch"), so that counts too.</summary>
    public static bool IsComplete(
        DailyQuestDef quest, IEnumerable<ProgressionReceiptDto> receiptsInWindow, ISet<string> playerHeroIds) =>
        receiptsInWindow.Any(r => Matches(quest, r, playerHeroIds));

    /// <summary>Does this ONE receipt satisfy the quest? Shared with the season pass, which counts matching
    /// deeds rather than asking whether any exists — the absorb aliasing and the winners-only rule are
    /// subtle enough that a second copy would drift.</summary>
    public static bool Matches(DailyQuestDef quest, ProgressionReceiptDto r, ISet<string> playerHeroIds)
    {
        var typeMatches = r.Type == quest.ReceiptType
            || (quest.ReceiptType == "deathmatch" && r.Type == "absorb");
        if (!typeMatches) return false;

        // The result hero counts as participation too. A merge burns both inputs BEFORE its receipt is
        // written, so HeroA and HeroB are already off the roster by the time anyone reads it — leaving the
        // fused hero as the only provable link between the player and the deed.
        return quest.WinnersOnly
            ? r.ResultHeroId is { } w && playerHeroIds.Contains(w)
            : playerHeroIds.Contains(r.HeroAId) || playerHeroIds.Contains(r.HeroBId)
                || (r.ResultHeroId is { } made && playerHeroIds.Contains(made));
    }
}
