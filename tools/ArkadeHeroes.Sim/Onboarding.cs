using System.Text;
using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Core.Progression;
using ArkadeHeroes.Server;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ArkadeHeroes.Sim;

/// <summary>
/// Day one, in order, on the balance a new player actually starts with. Every other measurement in this
/// tool looks at one system in isolation; this one is the ORDERED EXPERIENCE — what a newcomer tried,
/// what the game said back, and what was left in the wallet afterwards.
///
/// The cohort walks one fixed script in LOCKSTEP (everybody does step 1, then everybody does step 2), so
/// the step number at which one player hits a wall is comparable to every other player's, and so a beat
/// that needs an opponent has one. The script is what the UI puts in front of a newcomer: claim a
/// recruit, look around, run the PvE ladder, buy the gear you can afford, try to sell something, take a
/// staked duel, and find out which doors need a roster you do not have yet.
/// </summary>
public static class Onboarding
{
    public static Task<string> RenderAsync(int players, int seed) =>
        new Cohort(Math.Max(2, players), seed).ExecuteAsync();

    private enum Verdict { Did, Refused, Broke }

    private readonly record struct Said(bool Ok, string Detail)
    {
        public static Said Yes(string detail) => new(true, detail);
        public static Said No(string why) => new(false, why);
    }

    private sealed record Step(int Index, string Beat, Verdict Verdict, string Detail, long Balance);

    private readonly record struct Snap(int Heroes, int Items, int Level, long Xp, long Balance);

    private sealed class Newcomer
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required ArkadeHeroesClient Api { get; init; }
        public long Opening { get; set; }
        public long Balance { get; set; }
        public List<HeroDto> Roster { get; set; } = [];
        public List<string> Gear { get; set; } = [];
        public List<Step> Steps { get; } = [];
        public Dictionary<string, long> SpentOn { get; } = [];
        public HashSet<string> Rested { get; } = [];
        public int? FirstWall { get; set; }
        public int? FirstOwned { get; set; }
        public int? FirstXp { get; set; }
        public bool FirstXpFromPeer { get; set; }
        public int? FirstLevel { get; set; }
        public bool FirstLevelFromPeer { get; set; }
        public int? FirstIncome { get; set; }
        public long SpentByLevel2 { get; set; }
        public long Income { get; set; }
        public long PeerXp { get; set; }
        public int ParkedBuyIns { get; set; }

        public long Outflow => SpentOn.Values.Sum();
        public int Level => Roster.Count == 0 ? 0 : Roster.Max(h => h.Level);
        public long BankedXp => Roster.Sum(h => h.Level < 1 ? 0 : Leveling.TotalXp(h.Level, h.Xp));
    }

    private sealed class Cohort(int players, int seed)
    {
        private const long Wager = 500;
        private const long BuyIn = 1_000;
        private const int BracketSize = 4;

        private readonly Random _rng = new(seed);
        private readonly Tally _tally = new();
        private readonly List<Newcomer> _cohort = [];
        private readonly HashSet<string> _names = new(StringComparer.Ordinal);
        private readonly List<(int Waves, long Xp, long Fee)> _gauntlets = [];
        private readonly List<int> _trials = [];
        private readonly List<(long Moved, long Cost)> _duels = [];
        private readonly List<(long Paid, long Nets)> _resales = [];
        private int _births, _bornWithATakenName;

        private WebApplicationFactory<Program> _factory = null!;
        private ArkadeHeroesClient _observer = null!;
        private GameConfigDto? _config;
        private List<ItemDto> _shop = [];
        private DailyStatusDto? _openingDaily;
        private EconomyHealthDto _economy = null!;

        public async Task<string> ExecuteAsync()
        {
            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.UseContentRoot(ServerContentRoot());
                b.UseSetting("Game:DailyRewardEnabled", "true");
                b.UseSetting("Logging:LogLevel:Default", "Error");
            });
            _observer = new ArkadeHeroesClient(_factory.CreateClient());
            _config = (await _observer.Chain.InfoAsync()).Config;
            _shop = await _observer.Items.ShopAsync();

            await SignUpAsync();
            if (_cohort.Count < 2)
                return "DAY ONE — fewer than two players could register, so there is no day one to walk.\n\n"
                       + _tally.Render();

            var script = Script();
            for (var i = 0; i < script.Length; i++)
            {
                var (beat, body) = script[i];
                foreach (var p in _cohort.OrderBy(_ => _rng.Next()).ToList())
                    await StepAsync(p, i + 1, beat, body);
            }

            await CountParkedBuyInsAsync();
            _economy = await _observer.Economy.HealthAsync();
            await _factory.DisposeAsync();
            return Report(script.Select(s => s.Beat).ToArray());
        }

        private (string Beat, Func<Newcomer, Task<Said>> Body)[] Script() =>
        [
            ("look:shop", LookShopAsync),
            ("look:market", LookMarketAsync),
            ("look:arena", LookArenaAsync),
            ("look:daily", LookDailyAsync),
            ("daily:before-hero", DailyAsync),
            ("recruit:first", RecruitAsync),
            ("daily:after-hero", DailyAsync),
            ("gauntlet:first", p => GauntletAsync(p, preferUnrested: false)),
            ("gauntlet:retry", p => GauntletAsync(p, preferUnrested: false)),
            ("trials:first", TrialsAsync),
            ("trials:again", TrialsAsync),
            ("buy:cheapest-gear", BuyGearAsync),
            ("list:that-gear", ListGearAsync),
            ("equip:that-gear", EquipGearAsync),
            ("buy:next-tier", BuyNextTierAsync),
            ("equip:next-tier", EquipNextTierAsync),
            ("list:next-tier", ListNextTierAsync),
            ("list:my-hero", ListHeroAsync),
            ("duel:staked", DuelAsync),
            ("squad:3v3", SquadAsync),
            ("breed:one-hero", BreedAsync),
            ("recruit:second", RecruitAsync),
            ("breed:two-heroes", BreedAsync),
            ("gauntlet:second-hero", p => GauntletAsync(p, preferUnrested: true)),
            ("tournament:buy-in", TournamentAsync),
        ];

        // ── Signing up ──────────────────────────────────────────────────────────

        private async Task SignUpAsync()
        {
            for (var i = 0; i < players; i++)
            {
                var api = new ArkadeHeroesClient(_factory.CreateClient());
                var name = $"New{i:D2}";
                try
                {
                    var dto = await api.Players.RegisterAsync(
                        new RegisterPlayerRequest(name, $"day-one-wallet-{seed}-{i:D3}"));
                    if (dto.Token is { } t) api.SetAuthToken(t);
                    _cohort.Add(new Newcomer
                    {
                        Id = dto.PlayerId, Name = name, Api = api,
                        Opening = dto.BalanceSats, Balance = dto.BalanceSats,
                    });
                    _tally.Record("register", Outcome.Ok);
                }
                catch (Exception ex)
                {
                    _tally.Record("register", ex is ArkadeHeroesApiException ? Outcome.Refused : Outcome.Broken, ex.Message);
                }
            }

            if (_cohort.Count > 0)
            {
                try { _openingDaily = await _cohort[0].Api.Daily.StatusAsync(); }
                catch (Exception ex) { _tally.Record("look:daily", Outcome.Broken, ex.Message); }
            }
        }

        // ── One step of the script ──────────────────────────────────────────────

        /// Refreshed twice on purpose. A duel is opened BY one player AGAINST another, so a defender's hero
        /// can gain or lose XP during somebody else's step; the opening read separates what a peer did to
        /// this player from what this player's own action did.
        private async Task StepAsync(Newcomer p, int index, string beat, Func<Newcomer, Task<Said>> body)
        {
            var carried = Snapshot(p);
            await RefreshAsync(p);
            var pre = Snapshot(p);
            p.PeerXp += pre.Xp - carried.Xp;
            if (pre.Xp > 0 && carried.Xp == 0) { p.FirstXp ??= index; p.FirstXpFromPeer = true; }
            if (pre.Level >= 2 && carried.Level < 2)
            {
                if (p.FirstLevel is null) { p.FirstLevel = index; p.FirstLevelFromPeer = true; p.SpentByLevel2 = p.Outflow; }
            }

            Verdict verdict;
            string detail;
            try
            {
                var said = await body(p);
                verdict = said.Ok ? Verdict.Did : Verdict.Refused;
                detail = said.Detail;
                _tally.Record(beat, said.Ok ? Outcome.Ok : Outcome.Refused, said.Ok ? null : said.Detail);
            }
            catch (ArkadeHeroesApiException ex)
            {
                (verdict, detail) = (Verdict.Refused, ex.Message);
                _tally.Record(beat, Outcome.Refused, ex.Message);
            }
            catch (Exception ex)
            {
                (verdict, detail) = (Verdict.Broke, $"{ex.GetType().Name}: {ex.Message}");
                _tally.Record(beat, Outcome.Broken, detail);
            }

            await RefreshAsync(p);
            var post = Snapshot(p);
            if (verdict != Verdict.Did) p.FirstWall ??= index;
            if (post.Heroes > pre.Heroes || post.Items > pre.Items) p.FirstOwned ??= index;
            if (post.Xp > 0 && pre.Xp == 0) p.FirstXp ??= index;
            if (post.Balance > pre.Balance) p.FirstIncome ??= index;
            if (post.Level >= 2 && pre.Level < 2)
            {
                p.FirstLevel ??= index;
                p.SpentByLevel2 = p.Outflow;
            }
            p.Steps.Add(new Step(index, beat, verdict, detail, post.Balance));
        }

        private static Snap Snapshot(Newcomer p) =>
            new(p.Roster.Count, p.Gear.Count, p.Level, p.BankedXp, p.Balance);

        private async Task RefreshAsync(Newcomer p)
        {
            try
            {
                p.Balance = (await p.Api.Players.MeAsync()).BalanceSats;
                p.Roster = await p.Api.Heroes.MineAsync();
                p.Gear = [.. (await p.Api.Items.MineAsync()).Keys];
                foreach (var h in p.Roster) _names.Add(h.Name);
            }
            catch (Exception ex)
            {
                _tally.Record("read:own-state", ex is ArkadeHeroesApiException ? Outcome.Refused : Outcome.Broken, ex.Message);
            }
        }

        // ── The beats ───────────────────────────────────────────────────────────

        private async Task<Said> LookShopAsync(Newcomer p)
        {
            var shop = await p.Api.Items.ShopAsync();
            var level = Math.Max(1, p.Level);
            var usable = shop.Where(i => i.MinLevel <= level).ToList();
            return Said.Yes($"{shop.Count} items on sale; {usable.Count} a level-{level} hero may equip "
                + $"(from {(usable.Count == 0 ? 0 : usable.Min(i => i.PriceSats)):N0} sats), "
                + $"{shop.Count - usable.Count} gated behind level {(shop.Count == usable.Count ? 0 : shop.Where(i => i.MinLevel > level).Min(i => i.MinLevel))}+");
        }

        private async Task<Said> LookMarketAsync(Newcomer p)
        {
            var offers = (await p.Api.Offers.ListAsync()).Where(o => o.SellerId != p.Id).ToList();
            return Said.Yes(offers.Count == 0
                ? "the marketplace is empty — no other player has anything listed"
                : $"{offers.Count} listings from other players, cheapest {offers.Min(o => o.AskSats):N0} sats");
        }

        private async Task<Said> LookArenaAsync(Newcomer p)
        {
            var board = await p.Api.Leaderboard.TopAsync();
            var ranked = board.Count(e => e.Wins > 0);
            return Said.Yes(board.Count == 0
                ? "the leaderboard is empty — nobody has fought anything"
                : $"{board.Count} heroes on the board, {ranked} with a ranked win, "
                  + $"top hero level {board.Max(e => e.Level)}");
        }

        private async Task<Said> LookDailyAsync(Newcomer p)
        {
            var d = await p.Api.Daily.StatusAsync();
            var quests = string.Join(", ", d.Quests.Select(q => $"\"{q.Title}\" +{q.BonusSats}"));
            return Said.Yes($"claimable now {d.ClaimableNowSats:N0} (base {d.BaseSats}, streak {d.Streak}); "
                + $"today's quests: {(quests.Length == 0 ? "none" : quests)}");
        }

        private async Task<Said> DailyAsync(Newcomer p)
        {
            var status = await p.Api.Daily.StatusAsync();
            if (status.ClaimedToday) return Said.No("Daily reward already claimed today.");
            var claim = await p.Api.Daily.ClaimAsync();
            p.Income += claim.AwardedSats;
            return Said.Yes($"+{claim.AwardedSats:N0} sats (base {claim.BaseSats} + quests {claim.QuestBonusSats}); "
                + $"quests completed: {(claim.CompletedQuestIds.Count == 0 ? "none" : string.Join("/", claim.CompletedQuestIds))}");
        }

        private async Task<Said> RecruitAsync(Newcomer p)
        {
            var quote = await p.Api.Heroes.RequestStartersAsync();
            if (quote.Fee is { } fee) await PayAsync(p, "recruit", fee);
            var claimed = await p.Api.Heroes.ClaimStartersAsync();
            return Said.Yes($"paid {quote.FeeSats:N0} for {claimed.Heroes.Count} hero: "
                + string.Join(", ", claimed.Heroes.Select(h => $"{h.Name} (lvl {h.Level}, {h.Rarity?.Tier ?? "?"})")));
        }

        private async Task<Said> GauntletAsync(Newcomer p, bool preferUnrested)
        {
            var hero = preferUnrested
                ? p.Roster.FirstOrDefault(h => !p.Rested.Contains(h.Id)) ?? p.Roster.FirstOrDefault()
                : p.Roster.FirstOrDefault();
            if (hero is null) return Said.No("I own no hero to send.");

            var open = await p.Api.Gauntlet.OpenAsync(hero.Id);
            p.Rested.Add(hero.Id);
            await PayAsync(p, "gauntlet entry", open.FeeInvoice);
            var run = await p.Api.Gauntlet.RunAsync(open.GauntletId, Nonce());
            _gauntlets.Add((run.WavesCleared, run.XpAwarded, open.FeeInvoice.AmountSats));
            return Said.Yes($"{hero.Name} cleared {run.WavesCleared}/{Gauntlet.WaveCount} waves for {run.XpAwarded} xp "
                + $"({open.FeeInvoice.AmountSats:N0} sats in)"
                + (run.NewLevel > hero.Level ? $" — LEVEL {run.NewLevel}" : "")
                + (run.ItemAwarded is { } drop ? $", dropped {drop}" : ""));
        }

        private async Task<Said> TrialsAsync(Newcomer p)
        {
            var hero = p.Roster.FirstOrDefault();
            if (hero is null) return Said.No("I own no hero to send.");
            var open = await p.Api.Trials.OpenAsync(hero.Id);
            var run = await p.Api.Trials.RunAsync(open.TrialsId, Nonce());
            _trials.Add(run.WavesCleared);
            return Said.Yes($"{hero.Name} cleared {run.WavesCleared} waves under \"{run.Affix}\" — "
                + $"title {run.Title ?? "none"}, best {run.BestScore}; free to enter, pays no xp/sats/item");
        }

        private async Task<Said> BuyGearAsync(Newcomer p)
        {
            var level = Math.Max(1, p.Level);
            var item = _shop.Where(i => i.MinLevel <= level).OrderBy(i => i.PriceSats).FirstOrDefault();
            if (item is null) return Said.No("nothing in the shop is equippable at my level.");
            var invoice = (await p.Api.Items.BuyAsync(item.Id)).Invoice;
            await PayAsync(p, "gear", invoice);
            await p.Api.Items.ClaimAsync(new ClaimItemRequest(invoice.InvoiceId));
            return Said.Yes($"bought {item.Name} for {item.PriceSats:N0} sats (level {item.MinLevel}+)");
        }

        /// Listing the gear at exactly what the shop charged for it — the ask a seller who wants their sats
        /// back would pick, and the one the flat marketplace fee is measured against.
        private async Task<Said> ListGearAsync(Newcomer p)
        {
            var item = _shop.Where(i => p.Gear.Contains(i.Id)).OrderBy(i => i.PriceSats).FirstOrDefault();
            if (item is null) return Said.No("I own no gear to list.");
            var offer = await p.Api.Offers.CreateItemAsync(new CreateOfferRequest(item.Id, item.PriceSats));
            return Said.Yes($"listed {item.Name} at {item.PriceSats:N0}; fee {offer.ListingFeeSats:N0} leaves "
                + $"{item.PriceSats - offer.ListingFeeSats:N0} for me");
        }

        private async Task<Said> EquipGearAsync(Newcomer p)
        {
            var hero = p.Roster.FirstOrDefault();
            if (hero is null) return Said.No("I own no hero to equip.");
            var item = _shop.Where(i => p.Gear.Contains(i.Id)).OrderBy(i => i.PriceSats).FirstOrDefault();
            if (item is null) return Said.No("I own no gear to equip.");
            var after = await p.Api.Heroes.EquipAsync(hero.Id, new EquipRequest(item.Id));
            return Said.Yes($"{item.Name} on {hero.Name}: attack {hero.Stats.Attack} -> {after.Hero.Stats.Attack}, "
                + $"hp {hero.Stats.MaxHp} -> {after.Hero.Stats.MaxHp}");
        }

        private async Task<Said> BuyNextTierAsync(Newcomer p)
        {
            var item = NextTier(p);
            if (item is null) return Said.No("the whole shop is already open to me.");
            var invoice = (await p.Api.Items.BuyAsync(item.Id)).Invoice;
            await PayAsync(p, "gear (level-gated)", invoice);
            await p.Api.Items.ClaimAsync(new ClaimItemRequest(invoice.InvoiceId));
            return Said.Yes($"bought {item.Name} for {item.PriceSats:N0} sats — the shop asked nothing about my level");
        }

        private async Task<Said> EquipNextTierAsync(Newcomer p)
        {
            var hero = p.Roster.FirstOrDefault();
            if (hero is null) return Said.No("I own no hero to equip.");
            var item = NextTier(p);
            if (item is null) return Said.No("the whole shop is already open to me.");
            await p.Api.Heroes.EquipAsync(hero.Id, new EquipRequest(item.Id));
            return Said.Yes($"equipped {item.Name} ({item.PriceSats:N0} sats, level {item.MinLevel}+)");
        }

        /// The way out of gear that cannot be worn: list it at what it cost. Above the fee floor, so this is
        /// the paired measurement to the cheap item that is below it.
        private async Task<Said> ListNextTierAsync(Newcomer p)
        {
            var item = NextTier(p);
            if (item is null) return Said.No("nothing level-gated to resell.");
            if (!p.Gear.Contains(item.Id)) return Said.No("I do not hold that item.");
            var offer = await p.Api.Offers.CreateItemAsync(new CreateOfferRequest(item.Id, item.PriceSats));
            await p.Api.Dev.FundOfferAsync(new { OfferId = offer.OfferId });
            _resales.Add((item.PriceSats, item.PriceSats - offer.ListingFeeSats));
            return Said.Yes($"listed {item.Name} at the {item.PriceSats:N0} I paid; the {offer.ListingFeeSats:N0} fee "
                + $"leaves {item.PriceSats - offer.ListingFeeSats:N0} — and only if somebody buys it");
        }

        private ItemDto? NextTier(Newcomer p)
        {
            var level = p.Roster.Count == 0 ? 1 : p.Roster.Max(h => h.Level);
            return _shop.Where(i => i.MinLevel > level).OrderBy(i => i.PriceSats).FirstOrDefault();
        }

        private async Task<Said> ListHeroAsync(Newcomer p)
        {
            var hero = p.Roster.FirstOrDefault();
            if (hero is null) return Said.No("I own no hero to sell.");
            var ask = _config?.StarterClaimFeeSats is > 0 ? _config.StarterClaimFeeSats : BuyIn;
            var offer = await p.Api.Offers.CreateHeroAsync(new CreateHeroOfferRequest(hero.Id, ask));
            return Said.Yes($"listed {hero.Name} at {ask:N0} (what a recruit costs); fee {offer.ListingFeeSats:N0} "
                + $"leaves {ask - offer.ListingFeeSats:N0} for me");
        }

        private async Task<Said> DuelAsync(Newcomer p)
        {
            var mine = p.Roster.FirstOrDefault();
            if (mine is null) return Said.No("I own no hero to fight with.");
            var suggestions = await p.Api.Matches.MatchmakingAsync(mine.Id);
            if (suggestions.Count == 0) return Said.No("Matchmaking suggested nobody.");
            var pick = suggestions[_rng.Next(Math.Min(4, suggestions.Count))];
            var defender = _cohort.FirstOrDefault(x => x.Id == pick.OwnerPlayerId);
            if (defender is null) return Said.No("The suggested opponent is not one of us.");

            var open = await p.Api.Matches.OpenAsync(new OpenMatchRequest(mine.Id, pick.Hero.Id, Wager, "invoice"));
            if (open.StakeInvoice is { } s) await PayAsync(p, "duel stake", s);
            if (open.MatchFeeInvoice is { } f) await PayAsync(p, "duel fee", f);
            var accept = await defender.Api.Matches.AcceptAsync(open.MatchId);
            if (accept.StakeInvoice is { } ds) await PayAsync(defender, "duel stake", ds);
            if (accept.MatchFeeInvoice is { } df) await PayAsync(defender, "duel fee", df);

            var fight = await p.Api.Matches.FightAsync(open.MatchId, new FightRequest(Nonce()));
            var won = fight.Result.WinnerId == mine.Id;
            // The two awards are one conserved swing (defenderDelta = -challengerDelta), so their SUM is
            // always zero — the size of the transfer is either side's magnitude.
            var moved = Math.Abs(fight.ChallengerXpAward);
            _duels.Add((moved, Wager + (open.MatchFeeInvoice?.AmountSats ?? 0)));
            if (won) p.Income += fight.WinnerPayoutSats;
            return Said.Yes($"staked {Wager:N0} + {open.MatchFeeInvoice?.AmountSats ?? 0:N0} fee vs {defender.Name}'s "
                + $"{pick.Hero.Name} (gap {pick.PowerGapPercent}%, {pick.Favor}): {(won ? "WON" : "lost")} in "
                + $"{fight.Result.Turns} turns, xp moved {moved}, payout {fight.WinnerPayoutSats:N0}");
        }

        private async Task<Said> SquadAsync(Newcomer p)
        {
            if (p.Roster.Count == 0) return Said.No("I own no hero to field.");
            var other = _cohort.FirstOrDefault(x => x.Id != p.Id && x.Roster.Count > 0);
            if (other is null) return Said.No("Nobody else fields a hero.");
            var open = await p.Api.Squad.OpenAsync(new OpenSquadMatchRequest(
                [.. p.Roster.Take(3).Select(h => h.Id)], [.. other.Roster.Take(3).Select(h => h.Id)], Wager, "invoice"));
            if (open.StakeInvoice is { } s) await PayAsync(p, "squad stake", s);
            if (open.MatchFeeInvoice is { } f) await PayAsync(p, "squad fee", f);
            var accept = await other.Api.Squad.AcceptAsync(open.MatchId);
            if (accept.StakeInvoice is { } ds) await PayAsync(other, "squad stake", ds);
            if (accept.MatchFeeInvoice is { } df) await PayAsync(other, "squad fee", df);
            var resolved = await p.Api.Squad.ResolveAsync(open.MatchId, new FightRequest(Nonce()));
            if (resolved.Result.ChallengerWon) p.Income += resolved.WinnerPayoutSats;
            return Said.Yes($"3v3 vs {other.Name}: {resolved.Result.ChallengerWins}-{resolved.Result.DefenderWins}");
        }

        private async Task<Said> BreedAsync(Newcomer p)
        {
            if (p.Roster.Count == 0) return Said.No("I own no hero to breed.");
            var a = p.Roster[0];
            var b = p.Roster.Count > 1 ? p.Roster[1] : a;
            var commit = await p.Api.Breeding.CommitAsync(new BreedCommitRequest(a.Id, b.Id));
            if (commit.Invoice is { } inv) await PayAsync(p, "breeding", inv);
            var reveal = await p.Api.Breeding.RevealAsync(commit.BreedingId, new BreedRevealRequest(Nonce()));
            _births++;
            if (p.Roster.Any(h => h.Name == reveal.Hero.Name)) _bornWithATakenName++;
            _names.Add(reveal.Hero.Name);
            return Said.Yes($"{a.Name} x {b.Name} -> {reveal.Hero.Name} "
                + $"(gen {reveal.Hero.Generation}, {reveal.Hero.Rarity?.Tier ?? "?"}, lvl {reveal.Hero.Level})");
        }

        private async Task<Said> TournamentAsync(Newcomer p)
        {
            var hero = p.Roster.FirstOrDefault();
            if (hero is null) return Said.No("I own no hero to enter.");
            var open = (await p.Api.Tournament.ListAsync()).FirstOrDefault(t =>
                t.Status == "open" && t.Joined < t.Size && t.Entrants.All(e => e.PlayerId != p.Id));

            if (open is null)
            {
                var created = await p.Api.Tournament.OpenAsync(new OpenTournamentRequest(hero.Id, BuyIn, BracketSize));
                await PayAsync(p, "tournament buy-in", created.BuyIn);
                return Said.Yes($"opened a {BuyIn:N0}-sat bracket for {BracketSize}; 1/{BracketSize} in — "
                    + "nothing at all happens until three strangers turn up");
            }

            var joined = await p.Api.Tournament.JoinAsync(open.Id, new JoinTournamentRequest(hero.Id));
            await PayAsync(p, "tournament buy-in", joined.BuyIn);
            var now = await p.Api.Tournament.GetAsync(open.Id);
            if (now.Joined < now.Size)
                return Said.Yes($"joined a bracket at {BuyIn:N0} sats ({now.Joined}/{now.Size} in) — "
                    + "nothing happens until it fills");

            var opener = _cohort.FirstOrDefault(x => x.Id == now.OpenerPlayerId);
            if (opener is null) return Said.No("The bracket is full but its opener is not one of us.");
            var resolved = await opener.Api.Tournament.ResolveAsync(now.Id, new FightRequest(Nonce()));
            return Said.Yes($"the bracket filled and ran; prizes {string.Join("/", resolved.Prizes)} sats "
                + $"over {now.Size} paid buy-ins of {BuyIn:N0}");
        }

        private async Task CountParkedBuyInsAsync()
        {
            try
            {
                var brackets = await _observer.Tournament.ListAsync();
                foreach (var p in _cohort)
                    p.ParkedBuyIns = brackets.Count(t =>
                        t.Status != "resolved" && t.Status != "refunded" && t.Entrants.Any(e => e.PlayerId == p.Id));
            }
            catch (Exception ex)
            {
                _tally.Record("read:tournaments", ex is ArkadeHeroesApiException ? Outcome.Refused : Outcome.Broken, ex.Message);
            }
        }

        // ── Helpers ─────────────────────────────────────────────────────────────

        private static async Task PayAsync(Newcomer p, string category, FeeInvoiceDto invoice)
        {
            await p.Api.Dev.PayInvoiceAsync(new { InvoiceId = invoice.InvoiceId });
            p.SpentOn[category] = p.SpentOn.GetValueOrDefault(category) + invoice.AmountSats;
        }

        private string Nonce() => Convert.ToHexString(BitConverter.GetBytes(_rng.NextInt64())).ToLowerInvariant();

        /// Refusals name the hero they were about, so grouping on the raw text would scatter one rule across
        /// the cohort. Collapsing the names (and any number) is what makes the distribution readable.
        private string Key(string message)
        {
            var line = message.Split('\n')[0].Trim();
            foreach (var name in _names.OrderByDescending(n => n.Length))
                line = line.Replace(name, "<hero>", StringComparison.Ordinal);
            foreach (var p in _cohort) line = line.Replace(p.Name, "<player>", StringComparison.Ordinal);
            var sb = new StringBuilder(line.Length);
            var lastDigit = false;
            foreach (var ch in line)
            {
                if (char.IsDigit(ch)) { if (!lastDigit) sb.Append('#'); lastDigit = true; continue; }
                lastDigit = false;
                sb.Append(ch);
            }
            return sb.ToString();
        }

        private static long Median(IEnumerable<long> values)
        {
            var a = values.OrderBy(v => v).ToArray();
            return a.Length == 0 ? 0 : a[a.Length / 2];
        }

        private static string MedianStep(IEnumerable<int?> steps, int of)
        {
            var hit = steps.Where(s => s.HasValue).Select(s => s!.Value).OrderBy(s => s).ToArray();
            if (hit.Length == 0) return $"never, for any of the {of}";
            var span = hit[0] == hit[^1] ? $"step {hit[0]}" : $"step {hit[0]}–{hit[^1]}, median {hit[hit.Length / 2]}";
            return $"{span}   ({hit.Length}/{of} players)";
        }

        // ── The report ──────────────────────────────────────────────────────────

        private string Report(string[] beats)
        {
            var sb = new StringBuilder();
            var n = _cohort.Count;
            var opening = _cohort[0].Opening;

            sb.AppendLine($"DAY ONE — {n} brand-new players, {beats.Length} scripted actions each, seed {seed}");
            sb.AppendLine($"  every wallet opens on {opening:N0} sats (InMemoryChainService.FaucetSats), zero heroes");
            sb.AppendLine($"  the game's own prices: recruit {_config?.StarterClaimFeeSats ?? 0:N0}, "
                + $"gauntlet entry {Gauntlet.Fee(1):N0} at level 1, duel fee {Leveling.MatchFee(1):N0}/side, "
                + $"breed {_config?.BreedingFeeSats ?? 0:N0}, marketplace fee {_config?.OfferListingFeeSats ?? 0:N0}");
            sb.AppendLine($"  cheapest gear {(_shop.Count == 0 ? 0 : _shop.Min(i => i.PriceSats)):N0} (level 1), "
                + $"next tier {(_shop.Any(i => i.MinLevel > 1) ? _shop.Where(i => i.MinLevel > 1).Min(i => i.PriceSats) : 0):N0} "
                + $"(level {(_shop.Any(i => i.MinLevel > 1) ? _shop.Where(i => i.MinLevel > 1).Min(i => i.MinLevel) : 0)}+); "
                + $"level 2 needs {Leveling.XpToNext(1):N0} xp, a full 5-wave clear pays {Gauntlet.WaveXp.Sum()}");
            sb.AppendLine("  daily faucet forced ON for this run; GameOptions.DailyRewardEnabled ships OFF");
            if (_openingDaily is { } od)
                sb.AppendLine($"  wall-clock inputs (not seeded): daily-quest rotation for day {od.DayIndex} — "
                    + string.Join(", ", od.Quests.Select(q => q.Title)));
            sb.AppendLine("  the seed fixes this harness's own choices; every commit-reveal SERVER seed is drawn"
                + " from the OS CSPRNG (CommitReveal.NewSeed), so read the rates, not one run's waves");

            sb.AppendLine();
            sb.AppendLine("THE SCRIPT — what the cohort tried, in order");
            sb.AppendLine($"  {"#",2}  {"beat",-21} {"did",4} {"ref",4} {"brk",4}   what the game said back");
            for (var i = 0; i < beats.Length; i++)
            {
                var index = i + 1;
                var steps = _cohort.Select(p => p.Steps.FirstOrDefault(s => s.Index == index)).OfType<Step>().ToList();
                var did = steps.Count(s => s.Verdict == Verdict.Did);
                var refused = steps.Count(s => s.Verdict == Verdict.Refused);
                var broke = steps.Count(s => s.Verdict == Verdict.Broke);
                var pool = steps.Where(s => s.Verdict != Verdict.Did).ToList();
                if (pool.Count == 0) pool = steps;
                var line = pool.GroupBy(s => Key(s.Detail)).OrderByDescending(g => g.Count())
                    .Select(g => g.Key).FirstOrDefault() ?? "";
                sb.AppendLine($"  {index,2}  {beats[i],-21} {did,4} {refused,4} {broke,4}   {Trim(line, 92)}");
            }

            sb.AppendLine();
            sb.AppendLine("THE FIRST WALL — the first action the game refused, per player");
            var walls = _cohort.Where(p => p.FirstWall is not null)
                .Select(p => p.Steps.First(s => s.Index == p.FirstWall))
                .GroupBy(s => (s.Index, s.Beat, Reason: Key(s.Detail)))
                .OrderBy(g => g.Key.Index).ToList();
            foreach (var g in walls)
                sb.AppendLine($"  step {g.Key.Index,2}  {g.Key.Beat,-21} {g.Count(),3}/{n}  {Trim(g.Key.Reason, 96)}");
            sb.AppendLine($"  players who were never refused anything: {_cohort.Count(p => p.FirstWall is null)}/{n}");

            sb.AppendLine();
            sb.AppendLine("TIME TO FIRST ANYTHING — step numbers on the script above");
            sb.AppendLine($"  first thing they OWNED (hero or gear):  {MedianStep(_cohort.Select(p => p.FirstOwned), n)}");
            sb.AppendLine($"  first xp on any hero at all:            {MedianStep(_cohort.Select(p => p.FirstXp), n)}");
            sb.AppendLine($"  first level-up:                         {MedianStep(_cohort.Select(p => p.FirstLevel), n)}");
            sb.AppendLine($"  first sats coming back IN:              {MedianStep(_cohort.Select(p => p.FirstIncome), n)}");
            sb.AppendLine($"  of those, arrived from a PEER's duel rather than the player's own action: "
                + $"{_cohort.Count(p => p.FirstXpFromPeer)} first-xp, {_cohort.Count(p => p.FirstLevelFromPeer)} first-level");
            sb.AppendLine($"  net xp a peer's staked duel moved onto/off a player: median "
                + $"{Median(_cohort.Select(p => p.PeerXp))}, best {_cohort.Max(p => p.PeerXp)}, worst {_cohort.Min(p => p.PeerXp)}");

            sb.AppendLine();
            sb.AppendLine("THE THREE THINGS THERE ARE TO DO, MEASURED ON THIS COHORT");
            if (_gauntlets.Count > 0)
                sb.AppendLine($"  gauntlet  {_gauntlets.Count,3} runs   "
                    + $"{100.0 * _gauntlets.Count(g => g.Waves == 0) / _gauntlets.Count,4:F0}% cleared nothing, "
                    + $"{100.0 * _gauntlets.Count(g => g.Waves == Gauntlet.WaveCount) / _gauntlets.Count,4:F0}% full clear, "
                    + $"avg {_gauntlets.Average(g => g.Waves):F2}/{Gauntlet.WaveCount} waves, "
                    + $"{_gauntlets.Average(g => g.Xp):F0} xp for {_gauntlets.Average(g => (double)g.Fee):F0} sats");
            if (_trials.Count > 0)
                sb.AppendLine($"  trials    {_trials.Count,3} runs   "
                    + $"{100.0 * _trials.Count(w => w == 0) / _trials.Count,4:F0}% cleared nothing, "
                    + $"avg {_trials.Average():F2} waves — free, and pays no xp, sats or item either way");
            if (_duels.Count > 0)
                sb.AppendLine($"  duel      {_duels.Count,3} staked "
                    + $"{100.0 * _duels.Count(d => d.Moved == 0) / _duels.Count,4:F0}% moved ZERO xp, "
                    + $"avg {_duels.Average(d => (double)d.Moved):F1} xp moved, "
                    + $"{_duels.Average(d => (double)d.Cost):F0} sats a side to enter");

            sb.AppendLine();
            sb.AppendLine($"THE WALLET — median balance after each step, against the {opening:N0} start");
            sb.AppendLine($"  {"#",2}  {"beat",-21} {"balance",9} {"spent",8}   still affords");
            for (var i = 0; i < beats.Length; i++)
            {
                var index = i + 1;
                var balances = _cohort.Select(p => p.Steps.FirstOrDefault(s => s.Index == index))
                    .OfType<Step>().Select(s => s.Balance).ToList();
                if (balances.Count == 0) continue;
                var bal = Median(balances);
                sb.AppendLine($"  {index,2}  {beats[i],-21} {bal,9:N0} {opening - bal,8:N0}   "
                    + $"{bal / Math.Max(1, Gauntlet.Fee(1)),3} gauntlet runs | "
                    + $"{bal / Math.Max(1, _config?.StarterClaimFeeSats ?? 1_000),2} recruits | "
                    + $"{bal / Math.Max(1, Leveling.MatchFee(1) + Wager),2} staked duels");
            }

            sb.AppendLine();
            sb.AppendLine("WHERE THE SATS WENT — median per player");
            var categories = _cohort.SelectMany(p => p.SpentOn.Keys).Distinct().ToList();
            var totalOut = Median(_cohort.Select(p => p.Outflow));
            foreach (var c in categories.OrderByDescending(c => Median(_cohort.Select(p => p.SpentOn.GetValueOrDefault(c)))))
            {
                var med = Median(_cohort.Select(p => p.SpentOn.GetValueOrDefault(c)));
                sb.AppendLine($"  {c,-22} {med,8:N0}   {(totalOut == 0 ? 0 : 100.0 * med / totalOut),5:F1}% of outflow");
            }
            sb.AppendLine($"  {"TOTAL OUT",-22} {totalOut,8:N0}   "
                + $"{(opening == 0 ? 0 : 100.0 * totalOut / opening),5:F1}% of the opening balance");
            sb.AppendLine($"  {"came back IN",-22} {Median(_cohort.Select(p => p.Income)),8:N0}   "
                + $"(daily claims + anything won)");
            sb.AppendLine($"  parked in a bracket that never filled: "
                + $"{_cohort.Count(p => p.ParkedBuyIns > 0)}/{n} players, {BuyIn:N0} sats each");
            if (_resales.Count > 0)
                sb.AppendLine($"  the level-gated gear resold: paid {_resales[0].Paid:N0}, best listable ask nets "
                    + $"{_resales[0].Nets:N0} — a {100.0 * (_resales[0].Paid - _resales[0].Nets) / _resales[0].Paid:F0}% "
                    + "haircut, and only if a buyer turns up");
            if (_births > 0)
                sb.AppendLine($"  bred children named identically to one of their own two parents: "
                    + $"{_bornWithATakenName}/{_births} (HeroNamer.DeriveName has 16x16 = 256 names, "
                    + "drawn from the appearance genes the child inherits)");

            sb.AppendLine();
            sb.AppendLine("REACHING LEVEL 2");
            var levelled = _cohort.Where(p => p.FirstLevel is not null).ToList();
            sb.AppendLine($"  reached level 2: {levelled.Count}/{n}"
                + (levelled.Count == 0 ? "" : $" — median at step {Median(levelled.Select(p => (long)p.FirstLevel!.Value))}, "
                    + $"{Median(levelled.Select(p => p.SpentByLevel2)):N0} sats spent by then"));
            var stuck = _cohort.Where(p => p.FirstLevel is null).ToList();
            if (stuck.Count > 0)
                sb.AppendLine($"  still level 1: {stuck.Count}/{n} — median banked xp "
                    + $"{Median(stuck.Select(p => p.BankedXp))} of the {Leveling.XpToNext(1)} level 2 costs");
            sb.AppendLine($"  heroes owned at the end: median {Median(_cohort.Select(p => (long)p.Roster.Count))}, "
                + $"highest level anyone reached: {_cohort.Max(p => p.Level)}");

            sb.AppendLine();
            sb.AppendLine("LOCKED OUT ON DAY ONE — the gate, and the line the game gives");
            foreach (var (beat, gate) in Gates())
            {
                var refusals = _cohort.SelectMany(p => p.Steps).Where(s => s.Beat == beat && s.Verdict != Verdict.Did).ToList();
                if (refusals.Count == 0) continue;
                var said = refusals.GroupBy(s => Key(s.Detail)).OrderByDescending(g => g.Count()).First();
                sb.AppendLine($"  {beat,-21} {refusals.Count,3}/{n}  {gate}");
                sb.AppendLine($"  {"",-21} {"",3}     \"{Trim(said.Key, 100)}\"");
            }

            sb.AppendLine();
            sb.AppendLine("WHAT A NEWCOMER HEARS — every refusal in the cohort, by frequency");
            foreach (var g in _cohort.SelectMany(p => p.Steps).Where(s => s.Verdict != Verdict.Did)
                         .GroupBy(s => Key(s.Detail)).OrderByDescending(g => g.Count()))
                sb.AppendLine($"  {g.Count(),4}x  {Trim(g.Key, 108)}");
            var totalSteps = _cohort.Sum(p => p.Steps.Count);
            var refusedSteps = _cohort.SelectMany(p => p.Steps).Count(s => s.Verdict != Verdict.Did);
            sb.AppendLine($"  {refusedSteps} of {totalSteps} attempted actions were refused "
                + $"({(totalSteps == 0 ? 0 : 100.0 * refusedSteps / totalSteps):F0}% of everything a newcomer clicks)");

            sb.AppendLine();
            sb.AppendLine("TWO DAYS ONE, VERBATIM");
            foreach (var p in _cohort.Take(2))
            {
                sb.AppendLine();
                sb.AppendLine($"  {p.Name} — opened on {p.Opening:N0} sats, closed on {p.Balance:N0}, "
                    + $"{p.Roster.Count} hero(es), level {p.Level}, {p.BankedXp} xp banked");
                foreach (var s in p.Steps)
                    sb.AppendLine($"   {s.Index,2} {s.Beat,-21} {(s.Verdict == Verdict.Did ? "ok " : s.Verdict == Verdict.Refused ? "NO " : "!! ")}"
                        + $"{s.Balance,7:N0}  {Trim(s.Detail, 104)}");
            }

            sb.AppendLine();
            sb.AppendLine("WHERE THE COHORT'S SATS ENDED UP");
            sb.AppendLine($"  treasury {_economy.TreasuryBalanceSats:N0}   in {_economy.TotalInflowSats:N0}   "
                + $"out {_economy.TotalOutflowSats:N0}");
            foreach (var (tag, amount) in _economy.InflowByTag.OrderByDescending(kv => kv.Value).Take(8))
                sb.AppendLine($"    in   {tag,-22} {amount,10:N0}");
            foreach (var (tag, amount) in _economy.OutflowByTag.OrderByDescending(kv => kv.Value).Take(8))
                sb.AppendLine($"    out  {tag,-22} {amount,10:N0}");

            sb.AppendLine();
            sb.Append(_tally.Render());
            return sb.ToString();
        }

        /// The gate each locked door is locked BY, stated from the rule rather than from the message — the
        /// message says no, the gate says what it would take.
        private (string Beat, string Gate)[] Gates() =>
        [
            ("daily:before-hero", $"needs a claimed starter first — {_config?.StarterClaimFeeSats ?? 0:N0} sats"),
            ("gauntlet:retry", "per-hero cooldown (GameOptions.GauntletCooldown, 30s default, ~10min in prod); "
                + "HeroDto carries no gauntlet cooldown, so no client can show it"),
            ("list:that-gear", $"ask must EXCEED the flat {_config?.OfferListingFeeSats ?? 0:N0}-sat marketplace fee, "
                + $"and the only gear a level-1 hero may equip costs {(_shop.Count == 0 ? 0 : _shop.Min(i => i.PriceSats)):N0}"),
            ("list:my-hero", $"same floor: a recruit costs {_config?.StarterClaimFeeSats ?? 0:N0} and cannot be listed for it"),
            ("equip:next-tier", "gear tiers are level-gated at EQUIP (GameService.EquipAsync), not at purchase "
                + "(CreateItemInvoiceAsync checks nothing) — the shop sold it and the hero cannot wear it"),
            ("squad:3v3", $"three heroes per side, i.e. {3 * (_config?.StarterClaimFeeSats ?? 0):N0} sats of recruits "
                + "before the door opens at all"),
            ("breed:one-hero", $"two distinct parents, i.e. a second recruit at {_config?.StarterClaimFeeSats ?? 0:N0}"),
            ("duel:staked", "needs a matchmaking suggestion, i.e. somebody else already holding a hero"),
        ];

        private static string Trim(string s, int max) =>
            s.Length <= max ? s : s[..(max - 1)] + "…";

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
    }
}
