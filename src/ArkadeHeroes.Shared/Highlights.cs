namespace ArkadeHeroes.Shared;

/// <summary>The bits of a hero that make a fight worth watching.</summary>
public readonly record struct HighlightHero(string Name, int Level, bool Prized);

/// <summary>One notable fight, with the single reason that earned it the spot. <see cref="Score"/> is the
/// combined notability so the feed has a stable order; <see cref="Reason"/> is the loudest signal alone,
/// because "upset — 12 levels down" reads better than a list of four things.</summary>
public record HighlightDto(
    string MatchId, string Reason, string WinnerName, string LoserName, long WagerSats, int Score);

/// <summary>
/// The spectator feed: which resolved fights were actually worth watching. Pure over public match data plus
/// a hero lookup, so — like the leaderboards — anyone holding the same inputs recomputes the same feed and
/// the server's pick carries no trust of its own.
///
/// A fight with no signal at all is NOT a highlight. Padding the feed with even, unstaked scraps would make
/// it worthless as a "come watch this" surface, so those are dropped rather than ranked last.
/// </summary>
public static class HighlightsBuilder
{
    public static IReadOnlyList<HighlightDto> Build(
        IEnumerable<MatchDto> matches,
        IReadOnlyDictionary<string, HighlightHero> heroes,
        int take = 5)
    {
        var scored = new List<HighlightDto>();

        foreach (var m in matches)
        {
            if (m.Result is not { } r) continue;                                   // unresolved — nothing to watch
            if (!heroes.TryGetValue(r.WinnerId, out var winner)) continue;         // burned/merged since
            if (!heroes.TryGetValue(r.LoserId, out var loser)) continue;

            var signals = new List<(int Score, string Text)>();

            // Punching up is the most interesting thing a fight can do, and it scales with the gap.
            var levelGap = loser.Level - winner.Level;
            if (levelGap > 0)
                signals.Add((levelGap * 10, $"upset — {levelGap} level{(levelGap == 1 ? "" : "s")} down"));

            if (m.WagerSats > 0)
                signals.Add(((int)(m.WagerSats / 500), $"{m.WagerSats:N0} sat on the line"));

            if (winner.Prized || loser.Prized)
                signals.Add((15, "a prized hero in the ring"));

            // How it ended: untouched, or barely standing. Both are worth a replay; a normal win isn't.
            if (r.WinnerMaxHp > 0 && r.WinnerRemainingHp >= r.WinnerMaxHp)
                signals.Add((12, "flawless — untouched"));
            else if (r.WinnerMaxHp > 0 && r.WinnerRemainingHp * 10 <= r.WinnerMaxHp)
                signals.Add((12, "survived on fumes"));

            if (signals.Count == 0) continue;

            var headline = signals.OrderByDescending(s => s.Score).ThenBy(s => s.Text, StringComparer.Ordinal).First();
            scored.Add(new HighlightDto(
                m.MatchId, headline.Text, winner.Name, loser.Name, m.WagerSats, signals.Sum(s => s.Score)));
        }

        return scored
            .OrderByDescending(h => h.Score)
            .ThenBy(h => h.MatchId, StringComparer.Ordinal)   // stable order for equal notability
            .Take(Math.Max(0, take))
            .ToList();
    }
}
