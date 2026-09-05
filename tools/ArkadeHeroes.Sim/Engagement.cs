using System.Text;

namespace ArkadeHeroes.Sim;

/// <summary>One player as they stood at the end of one round.</summary>
public readonly record struct Snapshot(
    int Round, long Sats, int Heroes, long Xp, int MaxLevel, int Wins, int Losses, int HeroesLost);

/// <summary>
/// The per-round record the end-of-run totals cannot answer: what the MEDIAN player's run felt like,
/// whether the board churned, and who ran out of moves. The aggregate report says the treasury grew and
/// 89% of heroes are level 1; this says which round that became true and for whom.
/// </summary>
public sealed class Engagement
{
    /// A level-1 shop item — the cheapest thing in the game that costs sats. Read from items.json,
    /// not tuned here: below this a player cannot press any button that spends.
    public const long CheapestPaidActionSats = 500;
    public const long GauntletEntryAtLevel1Sats = 770;
    public const long DuelEntryAtLevel1Sats = 1_020;

    private readonly Dictionary<string, List<Snapshot>> _snaps = [];
    private readonly Dictionary<string, Persona> _persona = [];
    private readonly List<(int Round, string Player, string Action, bool Ok)> _did = [];
    private readonly Dictionary<int, Dictionary<string, int>> _boards = [];
    private readonly Dictionary<int, string> _leader = [];
    private readonly List<(int Round, string Kind, string Line)> _events = [];
    private readonly Dictionary<string, int> _brokeAt = [];

    public void Persona(string player, Persona persona) => _persona[player] = persona;

    public void Take(string player, Snapshot s)
    {
        if (!_snaps.TryGetValue(player, out var list)) _snaps[player] = list = [];
        list.Add(s);
    }

    public void Action(string player, int round, string action, bool ok) => _did.Add((round, player, action, ok));

    public void Broke(string player, int round) => _brokeAt.TryAdd(player, round);

    public void Board(int round, IEnumerable<(string HeroId, int Rank, string Name)> entries)
    {
        var map = new Dictionary<string, int>();
        string? top = null;
        foreach (var (heroId, rank, name) in entries)
        {
            map[heroId] = rank;
            if (rank == 1) top = name;
        }
        _boards[round] = map;
        if (top is not null) _leader[round] = top;
    }

    public void Event(int round, string kind, string line) => _events.Add((round, kind, line));

    // ── Derived ─────────────────────────────────────────────────────────────────

    /// A change a player would notice on their own screen and cannot undo by spending: they levelled,
    /// they earned their first XP, or their roster grew or shrank. Balance alone is not progress —
    /// spending down the faucet is what every action does.
    private static bool Material(Snapshot a, Snapshot b) =>
        b.MaxLevel > a.MaxLevel || (a.Xp == 0 && b.Xp > 0) || b.Heroes != a.Heroes;

    private int FirstMaterialRound(string player) => FirstRound(player, Material);

    private int FirstRound(string player, Func<Snapshot, Snapshot, bool> hit)
    {
        var list = _snaps[player];
        for (var i = 1; i < list.Count; i++)
            if (hit(list[i - 1], list[i])) return list[i].Round;
        return int.MaxValue;
    }

    private static string Med(IEnumerable<int> rounds, int total)
    {
        var got = rounds.Where(v => v != int.MaxValue).Select(v => (double)v).OrderBy(v => v).ToList();
        return got.Count == 0 ? $"never (0/{total})" : $"median r{Pct(got, 0.50):F0}, p75 r{Pct(got, 0.75):F0} ({got.Count}/{total})";
    }

    private static double Pct(IReadOnlyList<double> sorted, double q)
    {
        if (sorted.Count == 0) return 0;
        var idx = (int)Math.Round(q * (sorted.Count - 1));
        return sorted[Math.Clamp(idx, 0, sorted.Count - 1)];
    }

    private double PctOf(int round, Func<Snapshot, double> pick, double q)
    {
        var vals = _snaps.Values.Select(l => l.FirstOrDefault(s => s.Round == round))
            .Where(s => s.Round == round || round == 0).Select(pick).OrderBy(v => v).ToList();
        return Pct(vals, q);
    }

    public string Render(int rounds)
    {
        var sb = new StringBuilder();
        var names = _snaps.Keys.OrderBy(n => n, StringComparer.Ordinal).ToList();

        sb.AppendLine("WEALTH AND PROGRESS BY ROUND (population percentiles)");
        sb.AppendLine($"  {"round",5} {"sats p25",10} {"sats med",10} {"sats p75",10} {"xp med",8} {"xp p75",8} " +
                      $"{"xp total",9} {"xp top10%",10} {"heroes med",11} {"lvl>1",6}");
        for (var r = 0; r <= rounds; r++)
        {
            if (!_snaps.Values.Any(l => l.Any(s => s.Round == r))) continue;
            var lvlUp = _snaps.Values.Count(l => l.Any(s => s.Round == r && s.MaxLevel > 1));
            var xps = _snaps.Values.Select(l => l.FirstOrDefault(s => s.Round == r).Xp).OrderByDescending(x => x).ToList();
            var total = xps.Sum();
            var top = xps.Take(Math.Max(1, xps.Count / 10)).Sum();
            sb.AppendLine($"  {r,5} {PctOf(r, s => s.Sats, 0.25),10:N0} {PctOf(r, s => s.Sats, 0.50),10:N0} " +
                          $"{PctOf(r, s => s.Sats, 0.75),10:N0} {PctOf(r, s => s.Xp, 0.50),8:N0} " +
                          $"{PctOf(r, s => s.Xp, 0.75),8:N0} {total,9:N0} " +
                          $"{(total == 0 ? 0 : 100.0 * top / total),9:F0}% {PctOf(r, s => s.Heroes, 0.50),11:N0} {lvlUp,6}");
        }

        sb.AppendLine();
        sb.AppendLine("TIME TO FIRST MATERIAL CHANGE (level up, first XP, or roster change)");
        var firsts = names.Select(FirstMaterialRound).OrderBy(v => v).ToList();
        var never = firsts.Count(v => v == int.MaxValue);
        var got = firsts.Where(v => v != int.MaxValue).Select(v => (double)v).ToList();
        sb.AppendLine($"  players who ever had one: {got.Count}/{names.Count}   never: {never}");
        if (got.Count > 0)
            sb.AppendLine($"  round of first: p25 {Pct(got, 0.25):F0}   median {Pct(got, 0.50):F0}   " +
                          $"p75 {Pct(got, 0.75):F0}   worst {got[^1]:F0}");
        var quiet = names.Where(n => _snaps[n][^1].MaxLevel <= 1 && _snaps[n][^1].Xp == 0).ToList();
        sb.AppendLine($"  finished the run at level 1 with zero XP on every hero: {quiet.Count}/{names.Count}");
        sb.AppendLine($"  first level-up:      {Med(names.Select(n => FirstRound(n, (a, b) => b.MaxLevel > a.MaxLevel)), names.Count)}");
        sb.AppendLine($"  first XP ever:       {Med(names.Select(n => FirstRound(n, (a, b) => a.Xp == 0 && b.Xp > 0)), names.Count)}");
        sb.AppendLine($"  first roster change: {Med(names.Select(n => FirstRound(n, (a, b) => b.Heroes != a.Heroes)), names.Count)}");
        sb.AppendLine($"  first ranked win:    {Med(names.Select(n => FirstRound(n, (a, b) => b.Wins > a.Wins)), names.Count)}");

        sb.AppendLine();
        sb.AppendLine("THE MEDIAN AND BOTTOM-QUARTILE PLAYER (ranked by final sats)");
        var byEnd = names.OrderBy(n => _snaps[n][^1].Sats).ToList();
        foreach (var (label, name) in new[]
        {
            ("bottom", byEnd[0]),
            ("p25", byEnd[byEnd.Count / 4]),
            ("median", byEnd[byEnd.Count / 2]),
            ("p75", byEnd[3 * byEnd.Count / 4]),
            ("top", byEnd[^1]),
        })
        {
            var l = _snaps[name][^1];
            var first = FirstMaterialRound(name);
            var acts = _did.Where(d => d.Player == name && d.Ok).ToList();
            var top = acts.GroupBy(a => a.Action).OrderByDescending(g => g.Count()).FirstOrDefault();
            sb.AppendLine($"  {label,-7} {name,-12} {_persona.GetValueOrDefault(name),-9} " +
                          $"{l.Sats,8:N0} sats  {l.Heroes,2} heroes  lvl {l.MaxLevel}  {l.Xp,4} xp  " +
                          $"{l.Wins}W-{l.Losses}L  first-material r{(first == int.MaxValue ? "never" : first.ToString())}  " +
                          $"{acts.Count} ok actions, mostly {top?.Key ?? "-"} x{top?.Count() ?? 0}");
        }

        sb.AppendLine();
        sb.AppendLine("ACTIVITY MIX (actions that SUCCEEDED)");
        var ok = _did.Where(d => d.Ok).ToList();
        sb.AppendLine($"  {"action",-14} {"ok",6} {"share",7} {"attempts",9} {"success",8} {"players",8}");
        foreach (var g in ok.GroupBy(d => d.Action).OrderByDescending(g => g.Count()))
        {
            var tried = _did.Count(d => d.Action == g.Key);
            sb.AppendLine($"  {g.Key,-14} {g.Count(),6} {100.0 * g.Count() / ok.Count,6:F1}% {tried,9} " +
                          $"{100.0 * g.Count() / tried,7:F0}% {g.Select(d => d.Player).Distinct().Count(),8}");
        }
        var never2 = _did.Where(d => !d.Ok).Select(d => d.Action).Distinct()
            .Except(ok.Select(d => d.Action).Distinct()).OrderBy(a => a).ToList();
        if (never2.Count > 0) sb.AppendLine($"  NEVER SUCCEEDED for anyone: {string.Join(", ", never2)}");
        var share = ok.GroupBy(d => d.Action).Select(g => (double)g.Count() / ok.Count).ToList();
        sb.AppendLine($"  distinct successful action kinds: {share.Count}   " +
                      $"largest single share: {share.Max() * 100:F1}%   " +
                      $"concentration (HHI): {share.Sum(s => s * s):F3}");

        sb.AppendLine();
        sb.AppendLine("MONEY-COSTING ACTIVITY BY ROUND (the ones the treasury sees)");
        string[] paid = ["gauntlet", "duel", "deathmatch", "squad", "tournament", "breed", "stud", "buyitem", "recruit", "bid", "buyoffer"];
        sb.AppendLine($"  {"round",5} {"paid ok",8} {"per player",11} {"free ok",8}");
        for (var r = 1; r <= rounds; r++)
        {
            var p = ok.Count(d => d.Round == r && paid.Contains(d.Action));
            var f = ok.Count(d => d.Round == r && !paid.Contains(d.Action));
            sb.AppendLine($"  {r,5} {p,8} {(double)p / names.Count,11:F2} {f,8}");
        }

        sb.AppendLine();
        sb.AppendLine("DEAD ENDS (end-of-run balance against the cheapest thing that costs sats)");
        sb.AppendLine($"  cheapest paid action {CheapestPaidActionSats:N0}   gauntlet at lvl 1 {GauntletEntryAtLevel1Sats:N0}   " +
                      $"duel at lvl 1 {DuelEntryAtLevel1Sats:N0}");
        var broke = byEnd.Where(n => _snaps[n][^1].Sats < CheapestPaidActionSats).ToList();
        var nearBroke = byEnd.Where(n => _snaps[n][^1].Sats < DuelEntryAtLevel1Sats).ToList();
        sb.AppendLine($"  below the cheapest paid action: {broke.Count}/{names.Count}   " +
                      $"below a duel entry: {nearBroke.Count}/{names.Count}   " +
                      $"hit an insufficient-balance refusal at some point: {_brokeAt.Count}");
        foreach (var n in nearBroke.Take(12))
        {
            var last = _did.LastOrDefault(d => d.Player == n && d.Ok);
            var l = _snaps[n][^1];
            sb.AppendLine($"    {n,-12} {l.Sats,8:N0} sats  {l.Heroes,2} heroes  " +
                          $"last success: {last.Action ?? "none"} (round {last.Round})  " +
                          $"first refused-for-balance: round {(_brokeAt.TryGetValue(n, out var br) ? br.ToString() : "-")}");
        }

        // A roster wipe locks out every hero-gated action at once, and the starter grant is once per
        // account — so unlike running low on sats, only the market can undo it.
        sb.AppendLine("  ROSTER WIPES (0 heroes — no gauntlet, trials, duel, breed, squad or tournament)");
        var wiped = names.Where(n => _snaps[n].Any(s => s.Heroes == 0)).ToList();
        var stillWiped = wiped.Where(n => _snaps[n][^1].Heroes == 0).ToList();
        sb.AppendLine($"    players who hit zero heroes at some point: {wiped.Count}/{names.Count}   " +
                      $"still at zero at the end: {stillWiped.Count}");
        foreach (var n in wiped.Take(10))
        {
            var at = _snaps[n].First(s => s.Heroes == 0).Round;
            var after = _did.Where(d => d.Player == n && d.Round > at && d.Ok).Select(d => d.Action).Distinct().ToList();
            var lastBefore = _did.LastOrDefault(d => d.Player == n && d.Round <= at && d.Ok);
            sb.AppendLine($"    {n,-12} wiped at round {at,3}  last action before: {lastBefore.Action ?? "none"}  " +
                          $"recovered: {(_snaps[n][^1].Heroes > 0 ? "yes" : "NO")}  " +
                          $"could still do: {(after.Count == 0 ? "nothing" : string.Join(",", after))}");
        }

        sb.AppendLine();
        sb.AppendLine("LEADERBOARD CHURN (top 10)");
        sb.AppendLine($"  {"round",5} {"carried over",13} {"rank-1 hero",26} {"mean |rank move|",17}");
        var seenLeaders = new HashSet<string>();
        for (var r = 1; r <= rounds; r++)
        {
            if (!_boards.TryGetValue(r, out var now) || !_boards.TryGetValue(r - 1, out var was)) continue;
            var topNow = now.Where(kv => kv.Value <= 10).Select(kv => kv.Key).ToHashSet();
            var topWas = was.Where(kv => kv.Value <= 10).Select(kv => kv.Key).ToHashSet();
            var carried = topNow.Count == 0 ? 0 : topNow.Intersect(topWas).Count();
            var moves = topNow.Where(was.ContainsKey).Select(h => Math.Abs(now[h] - was[h])).ToList();
            var leader = _leader.GetValueOrDefault(r, "-");
            seenLeaders.Add(leader);
            sb.AppendLine($"  {r,5} {$"{carried}/{topNow.Count}",13} {leader,26} " +
                          $"{(moves.Count == 0 ? 0 : moves.Average()),17:F1}");
        }
        var finalLeader = _leader.GetValueOrDefault(rounds, "-");
        var firstHeld = Enumerable.Range(1, rounds).FirstOrDefault(r => _leader.GetValueOrDefault(r) == finalLeader);
        sb.AppendLine($"  distinct heroes that held rank 1: {seenLeaders.Count}   " +
                      $"final rank-1 ({finalLeader}) first held it at round {firstHeld} of {rounds}");
        var ranked = _boards.TryGetValue(rounds, out var fin) ? fin.Count : 0;
        sb.AppendLine($"  heroes on the board at the end: {ranked}");

        sb.AppendLine();
        sb.AppendLine("EVENT DENSITY BY ROUND");
        sb.AppendLine($"  {"round",5} {"material",9} {"levelups",9} {"roster+-",9} {"permadeath",11} {"rare birth",11} {"tourney",8}");
        var density = new List<(int Round, int Material)>();
        for (var r = 1; r <= rounds; r++)
        {
            var mat = 0; var lvl = 0; var roster = 0;
            foreach (var n in names)
            {
                var l = _snaps[n];
                var a = l.FirstOrDefault(s => s.Round == r - 1);
                var b = l.FirstOrDefault(s => s.Round == r);
                if (b.Round != r) continue;
                if (Material(a, b)) mat++;
                if (b.MaxLevel > a.MaxLevel) lvl++;
                if (b.Heroes != a.Heroes) roster++;
            }
            density.Add((r, mat));
            sb.AppendLine($"  {r,5} {mat,9} {lvl,9} {roster,9} " +
                          $"{_events.Count(e => e.Round == r && e.Kind == "permadeath"),11} " +
                          $"{_events.Count(e => e.Round == r && e.Kind == "rare-birth"),11} " +
                          $"{_events.Count(e => e.Round == r && e.Kind == "tournament"),8}");
        }
        if (density.Count >= 3)
        {
            var window = Math.Max(3, rounds / 5);
            var runs = Enumerable.Range(0, density.Count - window + 1)
                .Select(i => (Start: density[i].Round, End: density[i + window - 1].Round,
                              Sum: density.Skip(i).Take(window).Sum(d => d.Material)))
                .OrderBy(x => x.Sum).ToList();
            var dullest = runs[0];
            var liveliest = runs[^1];
            sb.AppendLine($"  quietest {window}-round stretch: rounds {dullest.Start}-{dullest.End} " +
                          $"({dullest.Sum} material events, {(double)dullest.Sum / (window * names.Count):F3} per player-round)");
            sb.AppendLine($"  busiest  {window}-round stretch: rounds {liveliest.Start}-{liveliest.End} " +
                          $"({liveliest.Sum} material events, {(double)liveliest.Sum / (window * names.Count):F3} per player-round)");
        }

        return sb.ToString();
    }
}
