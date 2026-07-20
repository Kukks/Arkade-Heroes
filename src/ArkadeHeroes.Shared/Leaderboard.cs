namespace ArkadeHeroes.Shared;

/// <summary>One ranked hero on the leaderboard, computed purely from public receipts.</summary>
public record LeaderboardEntryDto(
    int Rank, string HeroId, string Name, int Level, int Wins, int Matches, string OwnerId);

/// <summary>The current season's ranked ladder — its number, when it ends (so the client can show a
/// "resets in X" countdown), and the standings (staked-match wins within the season window).</summary>
public record SeasonLeaderboardDto(int SeasonNumber, long EndsAtUnix, IReadOnlyList<LeaderboardEntryDto> Standings);

/// <summary>
/// The leaderboard is computed entirely from signed progression receipts —
/// anyone holding the receipt chain can recompute it, so the server's ranking
/// carries no trust of its own (the receipts are the source of truth). Wins and
/// matches come from "match" receipts; the level is the hero's receipt-provable
/// current level.
/// </summary>
public static class LeaderboardBuilder
{
    public static IReadOnlyList<LeaderboardEntryDto> Build(
        IReadOnlyDictionary<string, (string Name, int Level, string OwnerId)> heroes,
        IEnumerable<ProgressionReceiptDto> receipts)
    {
        var wins = new Dictionary<string, int>();
        var matches = new Dictionary<string, int>();
        foreach (var r in receipts.Where(r => r.Type == "match"))
        {
            matches[r.HeroAId] = matches.GetValueOrDefault(r.HeroAId) + 1;
            matches[r.HeroBId] = matches.GetValueOrDefault(r.HeroBId) + 1;
            if (r.ResultHeroId is { } winner)
                wins[winner] = wins.GetValueOrDefault(winner) + 1;
        }

        return heroes
            .Select(kv => (kv.Key, kv.Value.Name, kv.Value.Level, kv.Value.OwnerId,
                Wins: wins.GetValueOrDefault(kv.Key), Matches: matches.GetValueOrDefault(kv.Key)))
            .OrderByDescending(h => h.Wins)
            .ThenByDescending(h => h.Level)
            .ThenByDescending(h => h.Matches)
            .ThenBy(h => h.Name, StringComparer.Ordinal)
            .Select((h, i) => new LeaderboardEntryDto(i + 1, h.Key, h.Name, h.Level, h.Wins, h.Matches, h.OwnerId))
            .ToList();
    }
}
