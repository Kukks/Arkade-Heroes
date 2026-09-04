using System.Text;

namespace ArkadeHeroes.Sim;

public enum Outcome { Ok, Refused, Broken }

/// <summary>
/// What the simulated playerbase actually managed to do. Every attempted action lands here with
/// its outcome, so the report can separate three very different things: it worked, the game said
/// no for a reason a player would understand, and it fell over.
/// </summary>
public sealed class Tally
{
    private readonly Dictionary<string, int[]> _byAction = [];
    private readonly Dictionary<string, Dictionary<string, int>> _reasons = [];
    private readonly List<string> _timeline = [];

    public void Record(string action, Outcome outcome, string? reason = null)
    {
        if (!_byAction.TryGetValue(action, out var counts))
            _byAction[action] = counts = new int[3];
        counts[(int)outcome]++;

        if (outcome == Outcome.Ok || reason is null) return;
        if (!_reasons.TryGetValue(action, out var bucket))
            _reasons[action] = bucket = [];
        var key = Normalise(reason);
        bucket[key] = bucket.GetValueOrDefault(key) + 1;
    }

    public void Note(string line) => _timeline.Add(line);

    public int Count(string action, Outcome outcome) => _byAction.TryGetValue(action, out var c) ? c[(int)outcome] : 0;
    public IEnumerable<string> Actions => _byAction.Keys.OrderBy(a => a);

    /// Collapses the varying parts of a message so 200 near-identical refusals group into one line.
    private static string Normalise(string reason)
    {
        var line = reason.Split('\n')[0].Trim();
        if (line.Length > 160) line = line[..160];
        var sb = new StringBuilder(line.Length);
        var lastWasDigit = false;
        foreach (var ch in line)
        {
            if (char.IsDigit(ch) || (lastWasDigit && char.IsLetter(ch) && char.IsLower(ch) && ch is >= 'a' and <= 'f'))
            {
                if (!lastWasDigit) sb.Append('#');
                lastWasDigit = true;
                continue;
            }
            lastWasDigit = false;
            sb.Append(ch);
        }
        return sb.ToString();
    }

    public string Render()
    {
        var sb = new StringBuilder();
        sb.AppendLine("ACTION OUTCOMES");
        sb.AppendLine($"  {"action",-18} {"ok",6} {"refused",8} {"broken",7}   note");
        foreach (var (action, c) in _byAction.OrderByDescending(kv => kv.Value.Sum()))
        {
            var note = c[(int)Outcome.Ok] == 0 ? "NEVER SUCCEEDED"
                : c[(int)Outcome.Broken] > 0 ? "has crashes"
                : "";
            sb.AppendLine($"  {action,-18} {c[0],6} {c[1],8} {c[2],7}   {note}");
        }

        sb.AppendLine();
        sb.AppendLine("WHY ACTIONS DID NOT HAPPEN (top reasons per action)");
        foreach (var (action, bucket) in _reasons.OrderBy(kv => kv.Key))
        {
            sb.AppendLine($"  {action}:");
            foreach (var (reason, n) in bucket.OrderByDescending(kv => kv.Value).Take(4))
                sb.AppendLine($"     {n,5}x  {reason}");
        }

        if (_timeline.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("NOTABLE MOMENTS");
            foreach (var line in _timeline.Take(40)) sb.AppendLine($"  {line}");
            if (_timeline.Count > 40) sb.AppendLine($"  ... and {_timeline.Count - 40} more");
        }
        return sb.ToString();
    }
}
