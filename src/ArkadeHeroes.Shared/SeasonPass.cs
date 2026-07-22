namespace ArkadeHeroes.Shared;

/// <summary>A player's standing on the season pass: points earned this season, the tier they've reached,
/// how far into the next one they are, and the titles that carries.</summary>
public record SeasonPassProgress(
    int Points, int Tier, int PointsIntoTier, int PointsToNextTier,
    string? Title, string? NextTitle, int MaxTier);

/// <summary>
/// The season-long goal: a renewable reason to keep playing after the daily is claimed. Points come from
/// the SAME deeds the daily quests recognise, counted across the season window instead of asked once a day,
/// so there's no second rulebook to keep in sync.
///
/// DELIBERATELY NON-INFLATIONARY: the pass pays out titles, never sats. The treasury cannot print, and the
/// daily faucet is only solvent because its rewards are gated behind fee-paying actions (see
/// EconomySolvencyTests) — a season-long sats reward would be a second, ungated faucet. Prestige scales for
/// free; sats don't.
/// </summary>
public static class SeasonPass
{
    public const int PointsPerTier = 10;
    public const int MaxTier = 10;

    /// <summary>What one qualifying deed is worth. Winners-only deeds (a duel or death-match WON, a gauntlet
    /// cleared) are harder than simply breeding, so they pay more.</summary>
    public static int PointsFor(DailyQuestDef quest) => quest.WinnersOnly ? 3 : 2;

    /// <summary>Titles by tier — sparse on purpose, so reaching one is an event rather than a drip.</summary>
    public static readonly IReadOnlyDictionary<int, string> TitleByTier = new Dictionary<int, string>
    {
        [3] = "Contender",
        [6] = "Season Veteran",
        [10] = "Season Sovereign",
    };

    /// <summary>
    /// Score a player's season from their in-window receipts. Each receipt type maps to exactly one quest
    /// in the catalog, so a deed is counted once. Points cap at the top tier — past that the pass is
    /// finished, and grinding further earns nothing.
    /// </summary>
    public static SeasonPassProgress Progress(
        IEnumerable<ProgressionReceiptDto> receiptsInSeason, ISet<string> playerHeroIds)
    {
        var receipts = receiptsInSeason as IReadOnlyCollection<ProgressionReceiptDto> ?? receiptsInSeason.ToList();

        var raw = 0;
        foreach (var quest in DailyQuests.Catalog)
        {
            var deeds = receipts.Count(r => DailyQuests.Matches(quest, r, playerHeroIds));
            raw += deeds * PointsFor(quest);
        }

        var cap = MaxTier * PointsPerTier;
        var points = Math.Min(raw, cap);
        var tier = Math.Min(MaxTier, points / PointsPerTier);

        var title = TitleByTier.Where(kv => kv.Key <= tier)
            .OrderByDescending(kv => kv.Key).Select(kv => kv.Value).FirstOrDefault();
        var nextTitle = TitleByTier.Where(kv => kv.Key > tier)
            .OrderBy(kv => kv.Key).Select(kv => kv.Value).FirstOrDefault();

        // At the cap there's no "next tier" to fill, so the remainder reads as a finished bar, not a reset.
        var intoTier = tier >= MaxTier ? PointsPerTier : points % PointsPerTier;
        var toNext = tier >= MaxTier ? 0 : PointsPerTier - intoTier;

        return new SeasonPassProgress(points, tier, intoTier, toNext, title, nextTitle, MaxTier);
    }
}
