namespace ArkadeHeroes.Shared;

/// <summary>One ranked hero on the leaderboard, computed purely from public receipts.</summary>
public record LeaderboardEntryDto(
    int Rank, string HeroId, string Name, int Level, int Wins, int Matches, string OwnerId);

/// <summary>The current season's ranked ladder — its number, when it ends (so the client can show a
/// "resets in X" countdown), and the standings (staked-match wins within the season window).</summary>
public record SeasonLeaderboardDto(
    int SeasonNumber, long EndsAtUnix, long PotSats,
    IReadOnlyList<LeaderboardEntryDto> Standings, SeasonSettlementDto? LastSettlement);

/// <summary>A settled season's payout snapshot — for the "last season's champions" surface.</summary>
public record SeasonSettlementDto(int SeasonNumber, long PotSats, IReadOnlyList<SeasonWinnerDto> Winners);
public record SeasonWinnerDto(int Rank, string Name, long AwardSats);

/// <summary>One ranked hero on the endless-Trials ladder, computed purely from signed trials receipts.</summary>
public record TrialsBoardEntryDto(int Rank, string HeroId, string Name, int Level, int BestScore, string? Title);

/// <summary>
/// The endless-Trials ladder, computed entirely from signed "trials" receipts — each one attests its own
/// run's waves-survived (in <c>XpAwardB</c>), so anyone holding the receipt chain recomputes the same
/// ranking and the server's board carries no trust of its own (same doctrine as the match leaderboard).
/// A hero ranks by its BEST run, so a later weak run can never cost it standing.
/// </summary>
public static class TrialsBoardBuilder
{
    public static IReadOnlyList<TrialsBoardEntryDto> Build(
        IReadOnlyDictionary<string, (string Name, int Level)> heroes,
        IEnumerable<ProgressionReceiptDto> receipts)
    {
        var best = new Dictionary<string, int>();
        foreach (var r in receipts.Where(r => r.Type == "trials"))
        {
            var score = (int)r.XpAwardB;   // the run's waves survived, as attested by the signed receipt
            if (score > best.GetValueOrDefault(r.HeroAId, -1)) best[r.HeroAId] = score;
        }

        return best
            .Where(kv => heroes.ContainsKey(kv.Key))   // a hero that has since been burned/merged drops off
            .Select(kv => (HeroId: kv.Key, heroes[kv.Key].Name, heroes[kv.Key].Level, Best: kv.Value))
            .OrderByDescending(h => h.Best)
            .ThenByDescending(h => h.Level)
            .ThenBy(h => h.Name, StringComparer.Ordinal)
            .ThenBy(h => h.HeroId, StringComparer.Ordinal)   // unique id last → a TOTAL order, so any recompute agrees
            .Select((h, i) => new TrialsBoardEntryDto(
                i + 1, h.HeroId, h.Name, h.Level, h.Best,
                ArkadeHeroes.Core.Progression.Trials.TitleFor(h.Best)))
            .ToList();
    }
}

/// <summary>
/// The leaderboard is computed entirely from signed progression receipts —
/// anyone holding the receipt chain can recompute it, so the server's ranking
/// carries no trust of its own (the receipts are the source of truth). Matches and
/// wins come from "match" receipts; the level is the hero's receipt-provable
/// current level.
///
/// A win only counts if the fight MOVED XP — see the note at the tally below. Rank
/// is paid in real sats at season settlement, so a fight that put nothing at stake
/// must not buy standing.
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
            // A win only ranks if the fight actually moved XP. The ladder already refuses to pay for
            // beating someone far below you (Leveling.XpTransfer clamps to 0), so counting such a fight
            // toward rank let the board be farmed for the price of the match fees — and rank is paid in
            // real sats at season settlement. XpAwardA is the clamp's own recorded output, so "zero" is
            // the ruleset's verdict that this fight was worth nothing, not a judgement made here. The
            // match still counts: it happened, and dropping it would hide the farming instead of
            // devaluing it.
            if (r.ResultHeroId is { } winner && r.XpAwardA != 0)
                wins[winner] = wins.GetValueOrDefault(winner) + 1;
        }

        return heroes
            .Select(kv => (kv.Key, kv.Value.Name, kv.Value.Level, kv.Value.OwnerId,
                Wins: wins.GetValueOrDefault(kv.Key), Matches: matches.GetValueOrDefault(kv.Key)))
            .OrderByDescending(h => h.Wins)
            .ThenByDescending(h => h.Level)
            .ThenByDescending(h => h.Matches)
            .ThenBy(h => h.Name, StringComparer.Ordinal)
            .ThenBy(h => h.Key, StringComparer.Ordinal)   // unique id last → a TOTAL order, so any recompute agrees
            .Select((h, i) => new LeaderboardEntryDto(i + 1, h.Key, h.Name, h.Level, h.Wins, h.Matches, h.OwnerId))
            .ToList();
    }
}
