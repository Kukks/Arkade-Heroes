using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Server;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ArkadeHeroes.Sim;

/// <summary>
/// Plays the treasury rather than the game: every action here is chosen because it makes the treasury PAY,
/// and the paths that only feed it (gauntlet, trials, item purchases) are left out on purpose. Real server,
/// real economics, real sats semantics — the report is a solvency measurement, not a playthrough.
///
/// The number this exists to produce is the MARGIN: the treasury balance minus the sats it is on the hook
/// for at that instant (fully-staked matches not yet fought, cleared buy-ins in an unresolved bracket, a
/// funded bid awaiting delivery). Those obligations sit in the same single balance the daily faucet and the
/// season pot draw on, and nothing on the server computes them — so the margin is the only thing that says
/// whether a payout was covered by its own inflow or by somebody else's.
/// </summary>
public static class TreasurySolvency
{
    public static Task<string> RenderAsync(int players, int rounds, int seed) =>
        new Run(Math.Max(2, players), Math.Max(1, rounds), seed).ExecuteAsync();

    private sealed class Actor
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required ArkadeHeroesClient Api { get; init; }
        public long DailyPaid { get; set; }
    }

    private readonly record struct Sample(int Round, string Label, long Balance, long Obligations)
    {
        public long Margin => Balance - Obligations;
    }

    private readonly record struct Refusal(int Round, string Action, string Message, long Balance, string Aftermath);

    private sealed class Held
    {
        public required string Id { get; init; }
        public required Actor Opener { get; init; }
        public bool Filled { get; set; }
    }

    private sealed class Run(int players, int rounds, int seed)
    {
        /// The shipped GameOptions default. Left at its default deliberately: the question is what the
        /// as-configured server does, and a floor invented by the harness would measure the harness.
        private const long ReserveFloorSats = 0;

        private readonly Random _rng = new(seed);
        private readonly Tally _tally = new();
        private readonly List<Actor> _actors = [];
        private readonly List<Sample> _samples = [];
        private readonly List<Refusal> _refusals = [];
        private readonly List<(int Round, EconomyHealthDto Economy)> _roundEnds = [];
        private readonly Dictionary<string, long> _obligations = [];
        private readonly List<(string MatchId, Actor Challenger, Actor Defender, long Wager)> _parkedDuels = [];

        private WebApplicationFactory<Program> _factory = null!;
        private ArkadeHeroesClient _observer = null!;
        private EconomyHealthDto _final = null!;
        private SeasonLeaderboardDto? _season;
        private long _peakObligations;

        public async Task<string> ExecuteAsync()
        {
            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.UseContentRoot(ServerContentRoot());
                b.UseSetting("Game:DailyRewardEnabled", "true");
                b.UseSetting("Logging:LogLevel:Default", "Error");
            });
            _observer = new ArkadeHeroesClient(_factory.CreateClient());

            await SignUpAsync();
            await SampleAsync(0, "after signup");

            for (var round = 1; round <= rounds; round++)
            {
                await ClaimEveryDailyAsync(round);
                await ParkStakedDuelsAsync(round);
                await BurstOutflowAsync(round);
                await ConcurrentHoldsAsync(round);
                await SettleBacklogAsync(round);
                _roundEnds.Add((round, await _observer.Economy.HealthAsync()));
                await SampleAsync(round, "round end");
            }

            await SettleBacklogAsync(rounds, drainAll: true);
            // Reading the season board is what settles a due season — the API has no other door to it.
            try { _season = await _observer.Leaderboard.SeasonAsync(); _tally.Record("season-read", Outcome.Ok); }
            catch (ArkadeHeroesApiException ex) { _tally.Record("season-read", Outcome.Refused, ex.Message); }
            catch (Exception ex) { _tally.Record("season-read", Outcome.Broken, $"{ex.GetType().Name}: {ex.Message}"); }

            _final = await _observer.Economy.HealthAsync();
            await SampleAsync(rounds, "final");
            await _factory.DisposeAsync();
            return Report();
        }

        // ── Onboarding ──────────────────────────────────────────────────────────────

        private async Task SignUpAsync()
        {
            for (var i = 0; i < players; i++)
            {
                var api = new ArkadeHeroesClient(_factory.CreateClient());
                var name = $"Drain{i:D2}";
                Actor actor;
                try
                {
                    var dto = await api.Players.RegisterAsync(
                        new RegisterPlayerRequest(name, $"solvency-wallet-{seed}-{i:D3}"));
                    if (dto.Token is { } t) api.SetAuthToken(t);
                    actor = new Actor { Id = dto.PlayerId, Name = name, Api = api };
                    _actors.Add(actor);
                    _tally.Record("register", Outcome.Ok);
                }
                catch (Exception ex)
                {
                    _tally.Record("register", ex is ArkadeHeroesApiException ? Outcome.Refused : Outcome.Broken, ex.Message);
                    continue;
                }

                // Three heroes is the squad minimum; the breed adds a fourth and buys the day's breed quest.
                for (var h = 0; h < 3; h++) await Attempt("recruit", 0, () => RecruitAsync(actor));
                await Attempt("breed", 0, () => BreedAsync(actor));
            }

            if (_actors.Count < 2)
                throw new InvalidOperationException("Fewer than two players could sign up; there is no payout to drive.");
        }

        private static async Task RecruitAsync(Actor a)
        {
            var quote = await a.Api.Heroes.RequestStartersAsync();
            if (quote.Fee is { } fee) await Pay(a, fee.InvoiceId);
            await a.Api.Heroes.ClaimStartersAsync();
        }

        private async Task BreedAsync(Actor a)
        {
            var mine = (await a.Api.Heroes.MineAsync())
                .Where(h => !h.IsSterile && (h.BreedCooldownUntil is null || h.BreedCooldownUntil <= DateTimeOffset.UtcNow))
                .ToList();
            if (mine.Count < 2) return;
            var commit = await a.Api.Breeding.CommitAsync(new BreedCommitRequest(mine[0].Id, mine[1].Id));
            if (commit.Invoice is { } inv) await Pay(a, inv.InvoiceId);
            await a.Api.Breeding.RevealAsync(commit.BreedingId, new BreedRevealRequest(Nonce()));
        }

        // ── The four phases ─────────────────────────────────────────────────────────

        /// The only outflow with no matching inflow a player can trigger at will.
        private async Task ClaimEveryDailyAsync(int round)
        {
            foreach (var a in Shuffled())
            {
                await Attempt("daily", round, async () =>
                {
                    var status = await a.Api.Daily.StatusAsync();
                    if (status.ClaimedToday) throw new ArkadeHeroesApiException("already claimed today");
                    var claim = await a.Api.Daily.ClaimAsync();
                    a.DailyPaid += claim.AwardedSats;
                    _tally.Note($"round {round}: {a.Name} claimed {claim.AwardedSats} sats "
                        + $"(base {claim.BaseSats} + quests {claim.QuestBonusSats} at +{claim.StreakBonusPct}%, "
                        + $"quests done: {(claim.CompletedQuestIds.Count == 0 ? "none" : string.Join(",", claim.CompletedQuestIds))})");
                });
                await SampleAsync(round, "daily");
            }
        }

        /// Fully stake duels and walk away. Every one parks 2x its wager inside the treasury as an
        /// obligation nothing on the server accounts for, which is the exposure being measured.
        private async Task ParkStakedDuelsAsync(int round)
        {
            var pairs = Shuffled().Chunk(2).Where(c => c.Length == 2).ToList();
            foreach (var pair in pairs)
            {
                var (challenger, defender) = (pair[0], pair[1]);
                var wager = 2_000L + 1_000L * _rng.Next(4);
                await Attempt("duel-stake", round, async () =>
                {
                    var mine = await FirstHeroAsync(challenger);
                    var theirs = await FirstHeroAsync(defender);
                    if (mine is null || theirs is null) throw new ArkadeHeroesApiException("a side has no hero to field");

                    var open = await challenger.Api.Matches.OpenAsync(
                        new OpenMatchRequest(mine.Id, theirs.Id, wager));
                    await challenger.Api.Dev.StakeEscrowAsync(new { MatchId = open.MatchId });
                    if (open.MatchFeeInvoice is { } f) await Pay(challenger, f.InvoiceId);
                    var accept = await defender.Api.Matches.AcceptAsync(open.MatchId);
                    await defender.Api.Dev.StakeEscrowAsync(new { MatchId = open.MatchId });
                    if (accept.MatchFeeInvoice is { } df) await Pay(defender, df.InvoiceId);

                    // A staked pot is NOT a treasury obligation: both stakes sit in per-party covenant
                    // escrows and settle straight to the winner, so the treasury never owes it and must not
                    // be modelled as if it did. What remains in _obligations is therefore exactly the
                    // custodial surface — brackets, stud fees and bids — which is the point of this mode.
                    _parkedDuels.Add((open.MatchId, challenger, defender, wager));
                });
                await SampleAsync(round, "duel staked");
            }
        }

        private async Task BurstOutflowAsync(int round)
        {
            var order = Shuffled();
            var a = order[0];
            var b = order[1 % order.Count];
            var c = order[2 % order.Count];

            await Attempt("tournament-resolve", round, () => TournamentAsync(a, b, round));
            await SampleAsync(round, "tournament");

            await Attempt("tournament-refund", round, () => CalledOffBracketAsync(c, round));
            await SampleAsync(round, "bracket refund");

            await Attempt("squad", round, () => SquadAsync(a, b));
            await SampleAsync(round, "squad");

            await Attempt("stud", round, () => StudAsync(b, c, round));
            await SampleAsync(round, "stud");

            await Attempt("bid", round, () => BidAsync(c, a, round));
            await SampleAsync(round, "bid");
        }

        /// Holds several brackets open AT ONCE: every other custodial path is open-then-close, so their peak
        /// is the largest SINGLE hold, while the faucet and season pot draw on the same balance all of these
        /// sit in — what must stay covered is everything held simultaneously.
        private async Task ConcurrentHoldsAsync(int round)
        {
            var order = Shuffled();
            var open = new List<Held>();
            for (var i = 3; i + 1 < order.Count && open.Count < 3; i += 2)
            {
                var opener = order[i];
                var joiner = order[i + 1];
                await Attempt("bracket-hold", round, async () =>
                {
                    var openerHero = await FirstHeroAsync(opener);
                    var joinerHero = await FirstHeroAsync(joiner);
                    if (openerHero is null || joinerHero is null)
                        throw new ArkadeHeroesApiException("a side has no hero to enter");
                    var buyIn = 4_000L;

                    var created = await opener.Api.Tournament.OpenAsync(
                        new OpenTournamentRequest(openerHero.Id, buyIn, 2));
                    await Pay(opener, created.BuyIn.InvoiceId);
                    // Owed the moment IT clears, and tracked from creation: recording only once BOTH legs
                    // cleared drops the opener's stake when the joiner's leg fails, and strands the bracket.
                    var held = new Held { Id = created.Tournament.Id, Opener = opener };
                    open.Add(held);
                    Owe($"bracket:{held.Id}", buyIn);

                    var joined = await joiner.Api.Tournament.JoinAsync(
                        held.Id, new JoinTournamentRequest(joinerHero.Id));
                    await Pay(joiner, joined.BuyIn.InvoiceId);
                    Owe($"bracket:{held.Id}", buyIn * 2);
                    held.Filled = true;
                });
            }

            if (open.Count == 0) return;
            await SampleAsync(round, $"{open.Count} brackets held");

            // A hold that neither resolves nor refunds stays owed, because it IS still holding the money.
            foreach (var held in open)
                await Attempt(held.Filled ? "bracket-hold-settle" : "bracket-hold-refund", round, async () =>
                {
                    if (held.Filled)
                        await held.Opener.Api.Tournament.ResolveAsync(held.Id, new FightRequest(Nonce()));
                    else
                        await held.Opener.Api.Tournament.RefundAsync(held.Id);
                    Settle($"bracket:{held.Id}");
                });
        }

        /// Fights what earlier rounds parked, so an obligation actually spans a round boundary rather than
        /// being opened and closed inside one.
        private async Task SettleBacklogAsync(int round, bool drainAll = false)
        {
            var due = drainAll ? _parkedDuels.Count : _parkedDuels.Count / 2;
            for (var i = 0; i < due && _parkedDuels.Count > 0; i++)
            {
                var (matchId, challenger, _, wager) = _parkedDuels[0];
                _parkedDuels.RemoveAt(0);
                try
                {
                    await challenger.Api.Matches.FightAsync(matchId, new FightRequest(Nonce()));
                    _tally.Record("duel-settle", Outcome.Ok);
                }
                catch (ArkadeHeroesApiException ex)
                {
                    _tally.Record("duel-settle", Outcome.Refused, ex.Message);
                    var aftermath = "not probed";
                    try
                    {
                        var m = await _observer.Matches.GetAsync(matchId);
                        aftermath = $"match left '{m.Status}' — the {wager * 2}-sat pot is still owed and the fight is retryable";
                    }
                    catch (Exception probe) { aftermath = $"could not re-read the match: {probe.Message}"; }
                    await NoteIfTreasuryShortAsync(round, "duel-settle", ex.Message, aftermath);
                }
                catch (Exception ex) { _tally.Record("duel-settle", Outcome.Broken, $"{ex.GetType().Name}: {ex.Message}"); }
                await SampleAsync(round, "duel settled");
            }
        }

        // ── Payout-path drivers ─────────────────────────────────────────────────────

        private async Task TournamentAsync(Actor opener, Actor joiner, int round)
        {
            var openerHero = await FirstHeroAsync(opener);
            var joinerHero = await FirstHeroAsync(joiner);
            if (openerHero is null || joinerHero is null) throw new ArkadeHeroesApiException("a side has no hero to enter");
            var buyIn = 3_000L + 1_000L * _rng.Next(3);

            var created = await opener.Api.Tournament.OpenAsync(new OpenTournamentRequest(openerHero.Id, buyIn, 2));
            await Pay(opener, created.BuyIn.InvoiceId);
            Owe($"bracket:{created.Tournament.Id}", buyIn);

            var joined = await joiner.Api.Tournament.JoinAsync(created.Tournament.Id, new JoinTournamentRequest(joinerHero.Id));
            await Pay(joiner, joined.BuyIn.InvoiceId);
            Owe($"bracket:{created.Tournament.Id}", buyIn * 2);
            // The only reader of _obligations is a sample, and every OTHER sample sits outside an Owe→Settle
            // pair — so deleting these four as redundant pins peak obligations back at zero.
            await SampleAsync(round, "bracket held");

            var resolved = await opener.Api.Tournament.ResolveAsync(created.Tournament.Id, new FightRequest(Nonce()));
            Settle($"bracket:{created.Tournament.Id}");
            _tally.Note($"round {round}: bracket paid {string.Join("/", resolved.Prizes)} of a {buyIn * 2}-sat pot "
                + $"(house kept {buyIn * 2 - resolved.Prizes.Sum()})");
        }

        /// The highest outflow-per-inflow action in the game: pay a buy-in, call the bracket off, take it
        /// all back. A 100% pass-through, and the one path a player can use to pull sats out on demand.
        private async Task CalledOffBracketAsync(Actor opener, int round)
        {
            var hero = await FirstHeroAsync(opener);
            if (hero is null) throw new ArkadeHeroesApiException("no hero to enter");
            var buyIn = 5_000L;
            var created = await opener.Api.Tournament.OpenAsync(new OpenTournamentRequest(hero.Id, buyIn, 4));
            await Pay(opener, created.BuyIn.InvoiceId);
            Owe($"bracket:{created.Tournament.Id}", buyIn);
            await SampleAsync(round, "buy-in held");
            var refunded = await opener.Api.Tournament.RefundAsync(created.Tournament.Id);
            Settle($"bracket:{created.Tournament.Id}");
            if (refunded.RefundedSats != buyIn)
                _tally.Note($"round {round}: called-off bracket returned {refunded.RefundedSats} against a {buyIn} buy-in");
        }

        private async Task SquadAsync(Actor challenger, Actor defender)
        {
            var mine = (await challenger.Api.Heroes.MineAsync()).Take(3).ToList();
            var theirs = (await defender.Api.Heroes.MineAsync()).Take(3).ToList();
            if (mine.Count < 3 || theirs.Count < 3) throw new ArkadeHeroesApiException("a side cannot field three heroes");
            var wager = 2_000L;

            var open = await challenger.Api.Squad.OpenAsync(new OpenSquadMatchRequest(
                [.. mine.Select(h => h.Id)], [.. theirs.Select(h => h.Id)], wager));
            await challenger.Api.Dev.StakeEscrowAsync(new { MatchId = open.MatchId });
            if (open.MatchFeeInvoice is { } f) await Pay(challenger, f.InvoiceId);
            var accept = await defender.Api.Squad.AcceptAsync(open.MatchId);
            await defender.Api.Dev.StakeEscrowAsync(new { MatchId = open.MatchId });
            if (accept.MatchFeeInvoice is { } df) await Pay(defender, df.InvoiceId);

            await challenger.Api.Squad.ResolveAsync(open.MatchId, new FightRequest(Nonce()));
        }

        private async Task StudAsync(Actor proposer, Actor owner, int round)
        {
            var mine = await BreedableHeroAsync(proposer);
            var stud = await BreedableHeroAsync(owner);
            if (mine is null || stud is null) throw new ArkadeHeroesApiException("no pair off cooldown to breed");
            var fee = 1_500L;

            var proposal = await proposer.Api.Stud.ProposeAsync(new StudProposeRequest(mine.Id, stud.Id, fee));
            var accepted = await owner.Api.Stud.AcceptAsync(proposal.ProposalId);
            await Pay(proposer, accepted.BreedFeeInvoice.InvoiceId);
            if (accepted.StudFeeInvoice is { } sf)
            {
                await Pay(proposer, sf.InvoiceId);
                Owe($"stud:{proposal.ProposalId}", fee);
                await SampleAsync(round, "stud fee held");
            }
            await proposer.Api.Stud.RevealAsync(proposal.ProposalId, new StudRevealRequest(Nonce()));
            Settle($"stud:{proposal.ProposalId}");
        }

        private async Task BidAsync(Actor bidder, Actor owner, int round)
        {
            var roster = (await owner.Api.Heroes.MineAsync()).ToList();
            if (roster.Count < 2) throw new ArkadeHeroesApiException("owner is down to their last hero");
            var target = roster[^1];
            var amount = 3_000L;

            var bid = await bidder.Api.Bids.PlaceAsync(new PlaceBidRequest(target.Id, amount));
            var accepted = await owner.Api.Bids.AcceptAsync(bid.BidId);
            await Pay(bidder, accepted.Invoice.InvoiceId);
            Owe($"bid:{bid.BidId}", amount);
            await SampleAsync(round, "bid held");
            await owner.Api.Dev.TransferAssetAsync(new { AssetId = target.AssetId, ToPlayerId = bidder.Id });
            await bidder.Api.Bids.SettleAsync(bid.BidId);
            Settle($"bid:{bid.BidId}");
        }

        // ── Bookkeeping ─────────────────────────────────────────────────────────────

        private void Owe(string key, long sats) => _obligations[key] = sats;
        private void Settle(string key) => _obligations.Remove(key);
        private long Outstanding => _obligations.Values.Sum();

        private async Task SampleAsync(int round, string label)
        {
            long balance;
            try { balance = (await _observer.Economy.HealthAsync()).TreasuryBalanceSats; }
            catch (Exception ex) { _tally.Record("economy-read", Outcome.Broken, $"{ex.GetType().Name}: {ex.Message}"); return; }
            var outstanding = Outstanding;
            _peakObligations = Math.Max(_peakObligations, outstanding);
            _samples.Add(new Sample(round, label, balance, outstanding));
        }

        private async Task NoteIfTreasuryShortAsync(int round, string action, string message, string aftermath)
        {
            if (!message.Contains("Treasury cannot cover", StringComparison.OrdinalIgnoreCase)) return;
            long balance = -1;
            try { balance = (await _observer.Economy.HealthAsync()).TreasuryBalanceSats; } catch { /* the report survives a failed read */ }
            _refusals.Add(new Refusal(round, action, message, balance, aftermath));
        }

        private async Task Attempt(string action, int round, Func<Task> body)
        {
            try { await body(); _tally.Record(action, Outcome.Ok); }
            catch (ArkadeHeroesApiException ex)
            {
                _tally.Record(action, Outcome.Refused, ex.Message);
                await NoteIfTreasuryShortAsync(round, action, ex.Message, "flow abandoned mid-sequence");
            }
            catch (Exception ex) { _tally.Record(action, Outcome.Broken, $"{ex.GetType().Name}: {ex.Message}"); }
        }

        private List<Actor> Shuffled() => [.. _actors.OrderBy(_ => _rng.Next())];

        private static async Task<HeroDto?> FirstHeroAsync(Actor a) => (await a.Api.Heroes.MineAsync()).FirstOrDefault();

        private static async Task<HeroDto?> BreedableHeroAsync(Actor a) => (await a.Api.Heroes.MineAsync())
            .FirstOrDefault(h => !h.IsSterile && (h.BreedCooldownUntil is null || h.BreedCooldownUntil <= DateTimeOffset.UtcNow));

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

        /// Outflow tags whose sats are NOT bought by a matching inflow on the same flow. Everything else
        /// here is a pass-through: the treasury took the money in before it paid it out.
        private static readonly string[] Unbacked = ["daily", "season"];

        private string Report()
        {
            var sb = new System.Text.StringBuilder();
            var worst = _samples.MinBy(s => s.Balance);
            var thinnest = _samples.MinBy(s => s.Margin);
            var start = _samples.Count > 0 ? _samples[0].Balance : 0;

            sb.AppendLine($"TREASURY SOLVENCY — {_actors.Count} players, {rounds} rounds, seed {seed}");
            sb.AppendLine($"  workload: every daily claimed, staked duels parked across rounds, brackets resolved and");
            sb.AppendLine($"            called off, squad pots, stud fees, bid proceeds. PvE and the item shop are");
            sb.AppendLine($"            OMITTED — they only feed the treasury.");
            sb.AppendLine($"  wallets:  {Chain.InMemoryChainService.FaucetSats:N0} sats each, treasury starts at 0 (in-memory chain, real economics)");
            sb.AppendLine($"  config:   DailyRewardEnabled=true, TreasuryReserveFloorSats={ReserveFloorSats} (shipped default), rest as shipped");
            sb.AppendLine($"  note:     the action SEQUENCE is seeded and repeatable; fight OUTCOMES are not — the");
            sb.AppendLine($"            server draws its own commit-reveal seed per session.");
            sb.AppendLine();

            sb.AppendLine("TRAJECTORY");
            sb.AppendLine($"  {"round",5} {"balance",13} {"in",12} {"out",12} {"obligations",13} {"margin",13}");
            foreach (var (round, economy) in _roundEnds)
            {
                var sample = _samples.LastOrDefault(s => s.Round == round && s.Label == "round end");
                sb.AppendLine($"  {round,5} {economy.TreasuryBalanceSats,13:N0} {economy.TotalInflowSats,12:N0} "
                    + $"{economy.TotalOutflowSats,12:N0} {sample.Obligations,13:N0} {sample.Margin,13:N0}");
            }
            sb.AppendLine();
            sb.AppendLine($"  start (post-signup)  {start,13:N0} sats");
            sb.AppendLine($"  worst balance        {worst.Balance,13:N0} sats   at round {worst.Round} ({worst.Label})");
            sb.AppendLine($"  end                  {_final.TreasuryBalanceSats,13:N0} sats   "
                + $"({(_final.TreasuryBalanceSats >= start ? "+" : "")}{_final.TreasuryBalanceSats - start:N0} over the run; "
                + "the last round's parked pots are drained AFTER the table above)");
            sb.AppendLine($"  peak obligations     {_peakObligations,13:N0} sats   (staked-not-fought pots, cleared buy-ins, funded bids)");
            sb.AppendLine($"  thinnest margin      {thinnest.Margin,13:N0} sats   at round {thinnest.Round} ({thinnest.Label}), "
                + $"balance {thinnest.Balance:N0} against {thinnest.Obligations:N0} owed");
            sb.AppendLine($"  samples taken        {_samples.Count,13:N0}");
            sb.AppendLine();

            var wentNegative = _samples.Any(s => s.Balance < 0);
            var brokeFloor = _samples.Any(s => s.Balance < ReserveFloorSats);
            var brokeMargin = _samples.Any(s => s.Margin < 0);
            sb.AppendLine("SOLVENCY VERDICT");
            sb.AppendLine($"  balance ever negative?              {(wentNegative ? "YES" : "no")}");
            sb.AppendLine($"  balance ever below the reserve floor ({ReserveFloorSats})? {(brokeFloor ? "YES" : "no")}");
            sb.AppendLine($"  balance ever below OBLIGATIONS?     {(brokeMargin ? "YES — player stakes were spent on someone else" : "no")}");
            sb.AppendLine($"  payouts refused for an empty treasury: {_refusals.Count}");
            foreach (var r in _refusals.Take(10))
            {
                sb.AppendLine($"     round {r.Round} {r.Action}: {r.Message}");
                sb.AppendLine($"        balance at refusal {r.Balance:N0} — {r.Aftermath}");
            }
            sb.AppendLine();

            sb.AppendLine("WHERE THE SATS WENT");
            sb.AppendLine($"  {"tag",-20} {"in",12} {"out",12} {"net",12}   backing");
            var tags = _final.InflowByTag.Keys.Concat(_final.OutflowByTag.Keys).Distinct().OrderBy(t => t);
            foreach (var tag in tags)
            {
                var inflow = _final.InflowByTag.GetValueOrDefault(tag);
                var outflow = _final.OutflowByTag.GetValueOrDefault(tag);
                var backing = outflow == 0 ? ""
                    : Unbacked.Contains(tag) ? "UNBACKED — no inflow buys this"
                    : "pass-through";
                sb.AppendLine($"  {tag,-20} {inflow,12:N0} {outflow,12:N0} {inflow - outflow,12:N0}   {backing}");
            }
            sb.AppendLine($"  {"TOTAL",-20} {_final.TotalInflowSats,12:N0} {_final.TotalOutflowSats,12:N0} "
                + $"{_final.TotalInflowSats - _final.TotalOutflowSats,12:N0}");
            sb.AppendLine();
            var dailyOut = _final.OutflowByTag.GetValueOrDefault("daily");
            var claimants = _actors.Count(a => a.DailyPaid > 0);
            var heroFloor = ArkadeHeroes.Core.Genetics.StarterPolicy.ClaimFeeSats();
            sb.AppendLine("THE UNBACKED EMISSION");
            sb.AppendLine($"  daily paid out       {dailyOut,13:N0} sats to {claimants} wallets, "
                + $"{(claimants == 0 ? 0 : dailyOut / claimants):N0} each, ONCE — the claim is per UTC day and this");
            sb.AppendLine($"                       run is one day, so rounds do not multiply it.");
            sb.AppendLine($"  cost of a claimant   {heroFloor,13:N0} sats — a claim needs a hero, and the cheapest hero is a recruit.");
            sb.AppendLine($"  season accrued       {_final.SeasonAccrualSats,13:N0} sats of staked-match fees (backed); the pot's BASE is house-funded.");
            sb.AppendLine($"  season settled?      {(_season?.LastSettlement is null ? "no — every ENDED season predates this run, so it has no receipts and no winners" : $"season {_season.LastSettlement.SeasonNumber} paid {_season.LastSettlement.PotSats:N0}")}");
            if (_season is not null)
                sb.AppendLine($"  current season       #{_season.SeasonNumber}, pot {_season.PotSats:N0} sats, {_season.Standings.Count} ranked — settles only after it ENDS");
            sb.AppendLine();

            // The books are meant to explain the balance; any gap is a path that moved sats without
            // booking them. Deliberately NOT attributed to a named suspect any more: this harness found
            // the duel/squad stakes unbooked, that was fixed, and a hard-coded subtraction of the pot
            // outflow now reads as a NEGATIVE residual and reports a defect that is no longer there.
            var booked = _final.TotalInflowSats - _final.TotalOutflowSats;
            var unaccounted = _final.TreasuryBalanceSats - booked;
            sb.AppendLine("LEDGER RECONCILIATION");
            sb.AppendLine($"  real treasury balance         {_final.TreasuryBalanceSats,13:N0}");
            sb.AppendLine($"  booked inflow - booked outflow{booked,13:N0}");
            sb.AppendLine($"  unaccounted                   {unaccounted,13:N0}   "
                + $"{(unaccounted == 0 ? "<- the books account for every sat" : "<- a path moved sats without booking them")}");
            sb.AppendLine($"  swallowed ledger writes: {_final.LedgerWriteFailures}   heroes minted {_final.HeroesMinted} / burned {_final.HeroesBurned}");
            sb.AppendLine();

            sb.Append(_tally.Render());
            return sb.ToString();
        }
    }
}
