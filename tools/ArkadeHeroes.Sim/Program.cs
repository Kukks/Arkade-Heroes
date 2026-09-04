using System.Text;
using ArkadeHeroes.Shared;

namespace ArkadeHeroes.Sim;

/// <summary>
/// Entry point, deliberately NOT top-level statements: those synthesise a global <c>Program</c>
/// class that shadows the server's, and <c>WebApplicationFactory&lt;Program&gt;</c> would then boot
/// this tool as the app under test and fork copies of itself.
/// </summary>
internal static class SimMain
{
    private static async Task<int> Main(string[] args)
    {
        var players = Arg(args, "--players", 12);
        var rounds = Arg(args, "--rounds", 10);
        var seed = Arg(args, "--seed", 1);
        var verbose = args.Contains("--verbose");

        if (args.Contains("--rarity"))
        {
            Console.WriteLine(RarityCurve.Render(Arg(args, "--population", 2000), Arg(args, "--generations", 8), seed));
            return 0;
        }

        if (args.Contains("--xp"))
        {
            Console.WriteLine(XpFaucet.Render(Arg(args, "--samples", 2000), seed));
            return 0;
        }

        if (args.Contains("--afford"))
        {
            Console.WriteLine(Affordability.Render(Arg(args, "--samples", 2000), seed, Arg(args, "--budget", 100_000)));
            return 0;
        }

        Console.WriteLine($"Arkade Heroes playthrough — {players} players, {rounds} rounds, seed {seed}");
        Console.WriteLine();

        var sim = new Simulation(players, rounds, seed, verbose);
        var startedAt = DateTimeOffset.UtcNow;
        await sim.RunAsync();

        Console.WriteLine(sim.Tally.Render());
        Console.WriteLine(RenderWorld(sim, sim.Economy, sim.Heroes, sim.Board));
        Console.WriteLine($"(elapsed {(DateTimeOffset.UtcNow - startedAt).TotalSeconds:F1}s)");
        return 0;
    }

    private static int Arg(string[] args, string name, int fallback)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var v) ? v : fallback;
    }

    private static string RenderWorld(
    Simulation sim, EconomyHealthDto economy, List<HeroDto> heroes, List<LeaderboardEntryDto> board)
{
    var sb = new StringBuilder();

    sb.AppendLine("PROGRESSION");
    var levels = heroes.GroupBy(h => h.Level).OrderBy(g => g.Key).ToList();
    foreach (var g in levels)
        sb.AppendLine($"  level {g.Key,-3} {new string('#', Math.Min(60, g.Count())),-60} {g.Count()}");
    var stuck = heroes.Count(h => h.Level == 1 && h.Xp == 0);
    sb.AppendLine($"  heroes alive: {heroes.Count}   still at level 1 with zero XP: {stuck} ({Pct(stuck, heroes.Count)})");
    sb.AppendLine($"  XP concentration (gini): {Gini([.. heroes.Select(h => (double)h.Xp)]):F2}   " +
                  $"top hero: {heroes.OrderByDescending(h => h.Xp).FirstOrDefault()?.Xp ?? 0} xp");

    sb.AppendLine();
    sb.AppendLine("RARITY OF WHAT EXISTS");
    foreach (var g in heroes.GroupBy(h => h.Rarity?.Tier ?? "?").OrderByDescending(g => g.Count()))
        sb.AppendLine($"  {g.Key,-12} {g.Count(),4}  ({Pct(g.Count(), heroes.Count)})");
    sb.AppendLine($"  sterile: {heroes.Count(h => h.IsSterile)}   generation>0: {heroes.Count(h => h.Generation > 0)}");

    sb.AppendLine();
    sb.AppendLine("PLAYERS");
    sb.AppendLine($"  {"player",-12} {"persona",-9} {"W-L",-8} {"lost heroes",11}");
    foreach (var p in sim.Players)
        sb.AppendLine($"  {p.Name,-12} {p.Persona,-9} {$"{p.Wins}-{p.Losses}",-8} {p.HeroesLost,11}");
    var never = sim.Players.Count(p => p.Wins == 0 && p.Losses == 0);
    sb.AppendLine($"  players who never fought a staked match: {never}/{sim.Players.Count}");
    sb.AppendLine($"  players who hit an insufficient-balance wall: {sim.Players.Count(p => p.WentBroke)}");

    sb.AppendLine();
    sb.AppendLine("ECONOMY");
    sb.AppendLine($"  treasury {economy.TreasuryBalanceSats:N0} sats   " +
                  $"in {economy.TotalInflowSats:N0}   out {economy.TotalOutflowSats:N0}");
    foreach (var (tag, amount) in economy.InflowByTag.OrderByDescending(kv => kv.Value).Take(8))
        sb.AppendLine($"    in   {tag,-24} {amount,10:N0}");
    foreach (var (tag, amount) in economy.OutflowByTag.OrderByDescending(kv => kv.Value).Take(8))
        sb.AppendLine($"    out  {tag,-24} {amount,10:N0}");

    sb.AppendLine();
    sb.AppendLine("LEADERBOARD (top 5)");
    foreach (var e in board.Take(5))
        sb.AppendLine($"  {e.Rank,2}. {e.Name,-22} lvl {e.Level,-3} {e.Wins}W/{e.Matches}M");
    sb.AppendLine($"  heroes with a ranked win: {board.Count(e => e.Wins > 0)}/{board.Count}");

    return sb.ToString();
    }

    private static string Pct(int n, int total) => total == 0 ? "n/a" : $"{100.0 * n / total:F0}%";

    private static double Gini(double[] values)
    {
        if (values.Length == 0) return 0;
        Array.Sort(values);
        var sum = values.Sum();
        if (sum <= 0) return 0;
        double weighted = 0;
        for (var i = 0; i < values.Length; i++) weighted += (i + 1) * values[i];
        return (2 * weighted) / (values.Length * sum) - (values.Length + 1.0) / values.Length;
    }
}
