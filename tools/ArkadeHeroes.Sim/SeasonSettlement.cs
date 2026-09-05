using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Core.Progression;
using ArkadeHeroes.Server;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ArkadeHeroes.Sim;

/// <summary>
/// Measures the one treasury outflow no other harness can reach: SEASON SETTLEMENT.
///
/// The pot is <c>SeasonPotBaseSats</c> (house-funded, bought by no inflow) plus a slice of the season's
/// staked-match fees (backed) — the largest unbacked payout in the game, and the one the treasury-solvency
/// run could not reach. Playing harder cannot reach it either: a receipt is stamped <c>UtcNow</c>, and the
/// window holding <c>UtcNow</c> is the CURRENT season at ANY season length, so the sats a run earns are
/// always in the one season that is never due.
///
/// Two doors, kept apart in the report. PART 1 is the shipped HTTP one: every ended season settled for
/// real, all of them empty — which is the check that matters, since a season nobody won must not pay a
/// podium. PART 2 is the same settle evaluated one season later through
/// <c>GameService.SeasonLeaderboardAt</c>, the server's own documented clock seam. Nothing is faked there:
/// real receipts, real config, real chain payout — only the instant moves.
/// </summary>
public static class SeasonSettlement
{
    /// Far past any treasury this workload can build, so the underfunded branch is reached by CONFIG rather
    /// than by starving the run. The balance it is compared against is printed beside it.
    private const long UnaffordablePotSats = 10_000_000;

    public static async Task<string> RenderAsync(int players, int rounds, int seed)
    {
        var main = await new Run(Math.Max(2, players), Math.Max(1, rounds), seed).ExecuteAsync();
        var probe = await new Run(4, 4, seed + 1, UnaffordablePotSats).ExecuteAsync();
        return main.Report() + Environment.NewLine + probe.ProbeReport();
    }

    private sealed class Actor
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required ArkadeHeroesClient Api { get; init; }
        public long StartBalance { get; set; }
        public long EndBalance { get; set; }
    }

    private readonly record struct Step(
        int K, DateTimeOffset At, int CurrentSeason, long Before, long After,
        SeasonSettlementDto? Paid, string Marker, string? Error);

    private sealed class Run(int players, int rounds, int seed, long potBaseOverride = 0)
    {
        /// One day is the shortest window Season.Current allows (it clamps lengthDays to >= 1). Two reasons:
        /// it puts 247 ended seasons behind the run for PART 1 to sweep, and it makes the season the run's
        /// wins land in end one day later, which is the instant PART 2 evaluates at.
        private const int SeasonDays = 1;
        private const int Horizon = 5;

        private readonly string _adminToken = $"sim-season-admin-{seed}";
        private readonly Random _rng = new(seed);
        private readonly Tally _tally = new();
        private readonly List<Actor> _actors = [];
        private readonly List<Step> _steps = [];

        private WebApplicationFactory<Program> _factory = null!;
        private ArkadeHeroesClient _observer = null!;
        private GameService _game = null!;
        private DateTimeOffset _start;
        private AdminOverviewDto? _preSweep;
        private SeasonLeaderboardDto? _liveBoard;
        private EconomyHealthDto? _final;
        private long _balBeforeSweep, _balAfterSweep;
        private double _sweepMs;
        private string _sweepDetail = "not reached";
        private int _duels, _qualifyingWins, _zeroXpDuels;

        public long PotBase => potBaseOverride > 0 ? potBaseOverride : _game.Config.SeasonPotBaseSats;
        private int SeasonAtStart => Season.Current(_start, SeasonDays).Number;

        public async Task<Run> ExecuteAsync()
        {
            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.UseContentRoot(ServerContentRoot());
                b.UseSetting("Game:SeasonLengthDays", SeasonDays.ToString());
                b.UseSetting("Game:AdminToken", _adminToken);
                b.UseSetting("Game:DailyRewardEnabled", "true");
                b.UseSetting("Logging:LogLevel:Default", "Error");
                if (potBaseOverride > 0) b.UseSetting("Game:SeasonPotBaseSats", potBaseOverride.ToString());
            });
            _observer = new ArkadeHeroesClient(_factory.CreateClient());
            _game = _factory.Services.GetRequiredService<GameService>();
            _start = DateTimeOffset.UtcNow;

            await SignUpAsync();
            for (var round = 1; round <= rounds; round++) await DuelRoundAsync();

            // Nothing above reads /api/leaderboard/season, so no settle has run yet and this snapshot is
            // the pre-settle state. AdminOverview projects the season WITHOUT settling — the only such read.
            await Attempt("admin-overview", async () => _preSweep = await _observer.Admin.OverviewAsync(_adminToken));
            _balBeforeSweep = await TreasuryAsync();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            await Attempt("settle-sweep", async () =>
                _sweepDetail = (await _observer.Admin.SettleSeasonsAsync(_adminToken)).Detail);
            _sweepMs = clock.Elapsed.TotalMilliseconds;
            _balAfterSweep = await TreasuryAsync();
            await Attempt("season-board", async () => _liveBoard = await _observer.Leaderboard.SeasonAsync());

            await WalkSeasonsAsync();

            foreach (var a in _actors)
                await Attempt("balance", async () => a.EndBalance = (await a.Api.Players.MeAsync()).BalanceSats);
            await Attempt("economy", async () => _final = await _observer.Economy.HealthAsync());
            await _factory.DisposeAsync();
            return this;
        }

        // ── Workload: bank XP, then stake it ────────────────────────────────────────

        private async Task SignUpAsync()
        {
            for (var i = 0; i < players; i++)
            {
                var api = new ArkadeHeroesClient(_factory.CreateClient());
                var name = $"Season{i:D2}";
                Actor actor;
                try
                {
                    var dto = await api.Players.RegisterAsync(
                        new RegisterPlayerRequest(name, $"season-wallet-{seed}-{i:D3}"));
                    if (dto.Token is { } t) api.SetAuthToken(t);
                    actor = new Actor { Id = dto.PlayerId, Name = name, Api = api, StartBalance = dto.BalanceSats };
                    _actors.Add(actor);
                    _tally.Record("register", Outcome.Ok);
                }
                catch (Exception ex)
                {
                    _tally.Record("register", ex is ArkadeHeroesApiException ? Outcome.Refused : Outcome.Broken, ex.Message);
                    continue;
                }
                await Attempt("recruit", () => RecruitAsync(actor));
                await Attempt("recruit", () => RecruitAsync(actor));
                await BankXpAsync(actor);
            }

            if (_actors.Count < 2)
                throw new InvalidOperationException("Fewer than two players could sign up; no staked match can be fought.");
        }

        private static async Task RecruitAsync(Actor a)
        {
            var quote = await a.Api.Heroes.RequestStartersAsync();
            if (quote.Fee is { } fee) await Pay(a, fee.InvoiceId);
            await a.Api.Heroes.ClaimStartersAsync();
        }

        /// A win only RANKS if the fight moved XP (LeaderboardBuilder drops zero-XP wins), and XP only moves
        /// if the loser banked some first. The gauntlet is the only mint, so it is the price of a podium.
        /// Once per hero: the run puts a hero on cooldown, and this whole simulation takes about a second.
        private async Task BankXpAsync(Actor a)
        {
            foreach (var hero in await a.Api.Heroes.MineAsync())
                await Attempt("gauntlet", async () =>
                {
                    var open = await a.Api.Gauntlet.OpenAsync(hero.Id);
                    await Pay(a, open.FeeInvoice.InvoiceId);
                    await a.Api.Gauntlet.RunAsync(open.GauntletId, Nonce());
                });
        }

        private async Task DuelRoundAsync()
        {
            foreach (var pair in Shuffled().Chunk(2).Where(c => c.Length == 2))
            {
                var (challenger, defender) = (pair[0], pair[1]);
                await Attempt("duel", async () =>
                {
                    var mine = await RichestAsync(challenger);
                    var theirs = await RichestAsync(defender);
                    if (mine is null || theirs is null) throw new ArkadeHeroesApiException("a side has no hero to field");

                    var open = await challenger.Api.Matches.OpenAsync(
                        new OpenMatchRequest(mine.Id, theirs.Id, 500, "invoice"));
                    if (open.StakeInvoice is { } s) await Pay(challenger, s.InvoiceId);
                    if (open.MatchFeeInvoice is { } f) await Pay(challenger, f.InvoiceId);
                    var accept = await defender.Api.Matches.AcceptAsync(open.MatchId);
                    if (accept.StakeInvoice is { } ds) await Pay(defender, ds.InvoiceId);
                    if (accept.MatchFeeInvoice is { } df) await Pay(defender, df.InvoiceId);

                    var fight = await challenger.Api.Matches.FightAsync(open.MatchId, new FightRequest(Nonce()));
                    _duels++;
                    if (fight.ChallengerXpAward == 0 && fight.DefenderXpAward == 0) _zeroXpDuels++;
                    else _qualifyingWins++;
                });
            }
        }

        // ── The settle walk ─────────────────────────────────────────────────────────

        private async Task WalkSeasonsAsync()
        {
            var seen = _liveBoard?.LastSettlement;
            for (var k = 1; k <= Horizon; k++)
            {
                var at = _start.AddDays(k * (double)SeasonDays);
                var before = await TreasuryAsync();
                SeasonSettlementDto? paid = null;
                string? error = null;
                try
                {
                    var board = await _game.SeasonLeaderboardAt(at, CancellationToken.None);
                    if (board.LastSettlement is { } ls && ls.SeasonNumber != seen?.SeasonNumber) { paid = ls; seen = ls; }
                    _tally.Record("settle-at", Outcome.Ok);
                }
                catch (ArkadeHeroesApiException ex) { error = ex.Message; _tally.Record("settle-at", Outcome.Refused, ex.Message); }
                catch (Exception ex) { error = $"{ex.GetType().Name}: {ex.Message}"; _tally.Record("settle-at", Outcome.Broken, error); }

                var after = await TreasuryAsync();
                // The admin action's own prose names the settled marker, which is the only HTTP read of it.
                var marker = "not read";
                await Attempt("marker-read", async () =>
                    marker = (await _observer.Admin.SettleSeasonsAsync(_adminToken)).Detail);
                _steps.Add(new Step(k, at, Season.Current(at, SeasonDays).Number, before, after, paid, marker, error));
            }
        }

        // ── Plumbing ────────────────────────────────────────────────────────────────

        private async Task<long> TreasuryAsync()
        {
            try { return (await _observer.Economy.HealthAsync()).TreasuryBalanceSats; }
            catch (Exception ex) { _tally.Record("economy-read", Outcome.Broken, $"{ex.GetType().Name}: {ex.Message}"); return -1; }
        }

        private async Task Attempt(string action, Func<Task> body)
        {
            try { await body(); _tally.Record(action, Outcome.Ok); }
            catch (ArkadeHeroesApiException ex) { _tally.Record(action, Outcome.Refused, ex.Message); }
            catch (Exception ex) { _tally.Record(action, Outcome.Broken, $"{ex.GetType().Name}: {ex.Message}"); }
        }

        private List<Actor> Shuffled() => [.. _actors.OrderBy(_ => _rng.Next())];

        /// The hero holding the most XP — the one whose loss can actually pay a rankable win.
        private static async Task<HeroDto?> RichestAsync(Actor a) => (await a.Api.Heroes.MineAsync())
            .OrderByDescending(h => h.Level).ThenByDescending(h => h.Xp).FirstOrDefault();

        private static Task Pay(Actor a, string invoiceId) => a.Api.Dev.PayInvoiceAsync(new { InvoiceId = invoiceId });

        private string Nonce() => Convert.ToHexString(BitConverter.GetBytes(_rng.NextInt64())).ToLowerInvariant();

        /// The factory guesses {solutionDir}/{assemblyName}, which misses this repo's src/ layout.
        private static string ServerContentRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                var candidate = Path.Combine(dir.FullName, "src", "ArkadeHeroes.Server");
                if (Directory.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException("Could not locate src/ArkadeHeroes.Server above the sim binary.");
        }

        // ── Report ──────────────────────────────────────────────────────────────────

        private long AccrualOf(SeasonSettlementDto s) => s.PotSats - PotBase;

        public string ProbeReport()
        {
            var sb = new System.Text.StringBuilder();
            var reached = _steps.FirstOrDefault(s => s.Paid is not null);
            var ranked = _liveBoard?.Standings.Count(e => e.Wins > 0) ?? 0;
            sb.AppendLine("PART 4 — A POT THE TREASURY CANNOT COVER (config probe)");
            sb.AppendLine($"  Same workload, {players} players / {rounds} rounds, seed {seed}, with ONE setting moved:");
            sb.AppendLine($"  Game:SeasonPotBaseSats = {potBaseOverride:N0} against a treasury of {_balAfterSweep:N0} sats.");
            sb.AppendLine($"  This is the only lever that reaches GameService.SeasonLeaderboardAt's underfunded branch");
            sb.AppendLine($"  (GameService.cs:2849) without waiting for a real drain.");
            sb.AppendLine($"  heroes with a qualifying win in the played season: {ranked}");
            sb.AppendLine($"  empty-season sweep still ran: {_sweepDetail}");
            foreach (var s in _steps)
                sb.AppendLine($"  +{s.K}d  season {s.CurrentSeason,4} live   treasury {s.Before,12:N0} -> {s.After,12:N0}   "
                    + $"{(s.Paid is null ? "NOTHING PAID" : $"paid {s.Paid.PotSats:N0}")}   |  {s.Marker}");
            var blocked = _steps.All(s => s.Paid is null) && _steps.All(s => s.Marker.Contains($"last settled {SeasonAtStart - 1}"));
            sb.AppendLine($"  verdict: {(ranked == 0
                ? "NOT REACHED — this workload produced no rankable win, so every due season took the empty "
                  + "branch and the pot was retained before the balance was ever checked."
                : blocked
                ? $"the season with winners was NOT settled and the marker FROZE at {SeasonAtStart - 1} — one unaffordable "
                  + "season blocks every later one, because the loop breaks rather than continuing."
                : "the season with winners was not paid; the marker still moved — see the rows above.")}");
            return sb.ToString();
        }

        public string Report()
        {
            var sb = new System.Text.StringBuilder();
            var cfg = _game.Config;
            var endedBefore = SeasonAtStart - 1;
            var settled = _steps.Where(s => s.Paid is not null).ToList();

            sb.AppendLine($"SEASON SETTLEMENT — {_actors.Count} players, {rounds} rounds, seed {seed}");
            sb.AppendLine($"  measuring: the season pot — SeasonPotBaseSats ({cfg.SeasonPotBaseSats:N0}, HOUSE-FUNDED, no inflow");
            sb.AppendLine($"             buys it) + {cfg.SeasonFeeAccrualPct}% of each staked match's fees (backed). The largest single");
            sb.AppendLine($"             unbacked outflow in the game, and the one the solvency run could not reach.");
            sb.AppendLine($"  config:    Game:SeasonLengthDays={SeasonDays} (Season.Current clamps to >= 1 day, so this is the shortest");
            sb.AppendLine($"             legal window), Game:AdminToken set, DailyRewardEnabled=true, rest as shipped.");
            sb.AppendLine($"  clock:     run started {_start:u}; season {SeasonAtStart} was live, {endedBefore} seasons had already ENDED.");
            sb.AppendLine($"  note:      the action sequence is seeded; fight OUTCOMES are not — the server draws its own");
            sb.AppendLine($"             commit-reveal seed per session.");
            sb.AppendLine();

            sb.AppendLine("WHAT THE PLAYERS DID");
            sb.AppendLine($"  staked duels fought           {_duels,10:N0}");
            sb.AppendLine($"  of which moved XP (rankable)  {_qualifyingWins,10:N0}   zero-XP (win confers no rank): {_zeroXpDuels}");
            sb.AppendLine($"  season fee accrued (all-time) {_final?.SeasonAccrualSats ?? -1,10:N0} sats");
            sb.AppendLine($"  gross paid to the treasury    {_balBeforeSweep,10:N0} sats (it starts at 0, so this IS the cohort's spend)");
            sb.AppendLine($"  net cost after season prizes  {_actors.Sum(a => a.StartBalance - a.EndBalance),10:N0} sats across {_actors.Count} players");
            sb.AppendLine();

            sb.AppendLine("PART 1 — THE SHIPPED DOOR: EVERY ENDED SEASON, SETTLED FOR REAL (HTTP only)");
            sb.AppendLine($"  ended-but-unsettled seasons at boot   {endedBefore,12:N0}");
            sb.AppendLine($"  house-funded base they could have paid {endedBefore * cfg.SeasonPotBaseSats,11:N0} sats "
                + $"({endedBefore} x {cfg.SeasonPotBaseSats:N0})");
            sb.AppendLine($"  pre-sweep season snapshot (pure read)  season #{_preSweep?.Season.SeasonNumber}, pot "
                + $"{_preSweep?.Season.PotSats ?? -1:N0}, {_preSweep?.Season.Standings.Count ?? -1} ranked, "
                + $"last settlement: {(_preSweep?.Season.LastSettlement is null ? "none" : "present")}");
            sb.AppendLine($"  treasury before the sweep             {_balBeforeSweep,12:N0} sats");
            sb.AppendLine($"  treasury after the sweep              {_balAfterSweep,12:N0} sats   "
                + $"(delta {_balAfterSweep - _balBeforeSweep:N0})");
            sb.AppendLine($"  operator's own account:               {_sweepDetail}");
            sb.AppendLine($"  the sweep took                        {_sweepMs,12:F0} ms   "
                + $"({endedBefore} windows, each re-scanning every receipt — GameService.cs:2815)");
            var noWinnerPaid = _balAfterSweep == _balBeforeSweep && _liveBoard?.LastSettlement is null;
            sb.AppendLine($"  a season with NO WINNER paid a podium? {(noWinnerPaid ? "no — 0 sats moved across all "
                + $"{endedBefore} of them" : "YES — SOMETHING PAID; see the delta above")}");
            sb.AppendLine();

            sb.AppendLine("PART 2 — A SEASON THAT WAS ACTUALLY PLAYED");
            sb.AppendLine($"  Evaluated through GameService.SeasonLeaderboardAt (GameService.cs:2786), the server's own");
            sb.AppendLine($"  documented clock seam. Nothing is faked: real receipts, real config, real chain payout —");
            sb.AppendLine($"  only the instant moves. Needed because a receipt is stamped UtcNow and the window holding");
            sb.AppendLine($"  UtcNow is the CURRENT season at any length, so a same-clock run can never settle its own.");
            sb.AppendLine();
            var live = _liveBoard;
            sb.AppendLine($"  live season #{live?.SeasonNumber}, pot {live?.PotSats ?? -1:N0} sats, "
                + $"{live?.Standings.Count ?? 0} ranked, {live?.Standings.Count(e => e.Wins > 0) ?? 0} with a qualifying win");
            foreach (var e in (live?.Standings ?? []).Take(6))
                sb.AppendLine($"     {e.Rank,2}. {e.Name,-24} lvl {e.Level,-3} {e.Wins}W/{e.Matches}M");
            sb.AppendLine();

            if (settled.Count == 0)
            {
                sb.AppendLine("  NO SETTLEMENT WAS REACHED. Every step below left the pot retained — see PART 3.");
            }
            foreach (var s in settled)
            {
                var p = s.Paid!;
                var accrual = AccrualOf(p);
                var paidOut = p.Winners.Sum(w => w.AwardSats);
                var unbacked = Math.Max(0, paidOut - accrual);
                sb.AppendLine($"  SETTLED season {p.SeasonNumber} at +{s.K}d ({s.At:u})");
                sb.AppendLine($"     pot                {p.PotSats,12:N0} sats = house base {cfg.SeasonPotBaseSats:N0} "
                    + $"+ accrued fee share {accrual:N0}");
                sb.AppendLine($"     {"winner",-24} {"award",10}  {"% of pot",9}  had a win?");
                foreach (var w in p.Winners)
                {
                    var wins = live?.Standings.FirstOrDefault(e => e.Name == w.Name)?.Wins;
                    sb.AppendLine($"     {w.Name,-24} {w.AwardSats,10:N0}  {100.0 * w.AwardSats / Math.Max(1, p.PotSats),8:F1}%  "
                        + $"{(wins is null ? "NOT ON THE BOARD" : wins > 0 ? $"yes ({wins}W)" : "NO — PAID WITHOUT A WIN")}");
                }
                sb.AppendLine($"     paid out           {paidOut,12:N0} sats   retained (unclaimed weight + rounding): {p.PotSats - paidOut:N0}");
                sb.AppendLine($"     treasury           {s.Before,12:N0} -> {s.After:N0}   "
                    + $"(moved {s.Before - s.After:N0}; {(s.Before - s.After == paidOut ? "MATCHES the awards" : "DOES NOT match the awards")})");
                sb.AppendLine($"     covered?           {(s.Before >= p.PotSats ? $"yes — balance {s.Before:N0} >= pot {p.PotSats:N0}"
                    : $"NO — balance {s.Before:N0} < pot {p.PotSats:N0}")}");
                sb.AppendLine($"     UNBACKED portion   {unbacked,12:N0} sats   (paid out minus the fee share that bought it)");
            }
            sb.AppendLine();

            sb.AppendLine("PART 3 — CONSECUTIVE SEASONS: DOES THE BASE COMPOUND?");
            sb.AppendLine($"  {"step",5} {"live season",12} {"treasury before",16} {"treasury after",15} {"paid",10}   outcome");
            foreach (var s in _steps)
            {
                var outcome = s.Error is not null ? $"BROKEN: {s.Error}"
                    : s.Paid is not null ? $"settled season {s.Paid.SeasonNumber} to {s.Paid.Winners.Count} winner(s)"
                    : "no winner in the due season — POT RETAINED, marker advanced";
                sb.AppendLine($"  +{s.K}d {s.CurrentSeason,12} {s.Before,16:N0} {s.After,15:N0} "
                    + $"{(s.Paid is null ? 0 : s.Paid.Winners.Sum(w => w.AwardSats)),10:N0}   {outcome}");
            }
            sb.AppendLine($"  last marker read: {_steps.LastOrDefault().Marker}");
            sb.AppendLine();

            sb.AppendLine("THE ARITHMETIC OF THE HOUSE-FUNDED BASE");
            var perDuel = _duels == 0 ? 0 : (_final?.SeasonAccrualSats ?? 0) / _duels;
            var b = cfg.SeasonPotBaseSats;
            sb.AppendLine($"  pot            = base {b:N0} + accrual A          (GameService.cs:2847)");
            sb.AppendLine($"  paid           = pot x W, W = 60/90/100% for 1/2/3 winners  (SeasonPrize.cs:9,16)");
            sb.AppendLine($"  unbacked       = paid - A = (base + A)*W - A");
            sb.AppendLine($"    3 winners (W=1):   unbacked = base = {b:N0} sats, EXACTLY, at any accrual — a full");
            sb.AppendLine($"                       podium pays out the whole pot, so the fee share cancels and the house");
            sb.AppendLine($"                       is left funding the base every single settled season.");
            sb.AppendLine($"    2 winners (W=.9):  break-even needs A >= 9 x base = {9 * b:N0} sats of season fees");
            sb.AppendLine($"    1 winner  (W=.6):  break-even needs A >= 1.5 x base = {3 * b / 2:N0} sats of season fees");
            sb.AppendLine($"  measured accrual per staked duel: {perDuel:N0} sats "
                + $"({cfg.SeasonFeeAccrualPct}% of 2 x MatchFee; at level 1 that is {cfg.SeasonFeeAccrualPct * 2 * (cfg.MatchFeeBaseSats + cfg.MatchFeePerLevel) / 100:N0})");
            if (perDuel > 0)
            {
                sb.AppendLine($"  duels per season to break even at 1 winner:  {3 * b / 2 / perDuel:N0}");
                sb.AppendLine($"  duels per season to break even at 2 winners: {9 * b / perDuel:N0}");
                sb.AppendLine($"  duels per season to break even at 3 winners: never — the shortfall is the base itself.");
            }
            sb.AppendLine($"  at the shipped SeasonLengthDays=14 that is {365 / 14} seasons a year, so a ladder that keeps a");
            sb.AppendLine($"  full podium costs the treasury {365 / 14 * b:N0} sats a year that no inflow buys.");
            sb.AppendLine();
            sb.AppendLine($"  WHAT PAID FOR IT: this run's whole workload netted the treasury {_balBeforeSweep:N0} sats before the");
            sb.AppendLine($"  settlement — every fee source, not just the {cfg.SeasonFeeAccrualPct}% match slice. Against a {b:N0} base that is");
            sb.AppendLine($"  {(_balBeforeSweep >= b ? $"self-funding by {_balBeforeSweep - b:N0}" : $"SHORT by {b - _balBeforeSweep:N0}")}, "
                + $"from {_actors.Count} players x {rounds} rounds. So the base is a floor on how much a");
            sb.AppendLine($"  season must earn, not a runaway drain: it compounds only while a season keeps producing");
            sb.AppendLine($"  three ranked winners AND earning less than {b:N0} sats of net fees.");
            sb.AppendLine($"  ReserveSeasonPot is {cfg.ReserveSeasonPot} (shipped default), so the daily faucet is NOT held back");
            sb.AppendLine($"  from the sats the next settlement owes (GameService.cs:2974).");
            sb.AppendLine();

            sb.Append(_tally.Render());
            return sb.ToString();
        }
    }
}
