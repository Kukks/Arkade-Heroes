using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Server;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ArkadeHeroes.Sim;

/// <summary>How a simulated player spends their turns. Personas exist so the playerbase is not a
/// uniform blob: a Trader listing heroes gives a Duelist something to buy, and a system nobody's
/// persona reaches shows up in the report as never-exercised.</summary>
public enum Persona { Grinder, Breeder, Duelist, Trader, Whale, Casual }

public sealed class Player
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required Persona Persona { get; init; }
    public required ArkadeHeroesClient Api { get; init; }
    public long StartingSats { get; set; }
    public int HeroesLost { get; set; }
    public int Wins { get; set; }
    public int Losses { get; set; }
    public bool WentBroke { get; set; }
}

public sealed class Simulation(int players, int rounds, int seed, bool verbose)
{
    private readonly Random _rng = new(seed);
    private readonly Tally _tally = new();
    private readonly Engagement _engagement = new();
    private readonly List<Player> _players = [];
    private WebApplicationFactory<Program> _factory = null!;
    private ArkadeHeroesClient _observer = null!;

    public Tally Tally => _tally;
    public Engagement Engagement => _engagement;
    public IReadOnlyList<Player> Players => _players;

    /// The world as it stood at the last round, captured before the host is torn down.
    public EconomyHealthDto Economy { get; private set; } = null!;
    public List<HeroDto> Heroes { get; private set; } = [];
    public List<LeaderboardEntryDto> Board { get; private set; } = [];

    public async Task RunAsync()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseContentRoot(ServerContentRoot());
            b.UseSetting("Game:DailyRewardEnabled", "true");
            b.UseSetting("Logging:LogLevel:Default", "Error");
        });
        _observer = new ArkadeHeroesClient(_factory.CreateClient());

        await SignUpAsync();
        await SnapshotAsync(0);
        for (var round = 1; round <= rounds; round++)
        {
            foreach (var p in _players.OrderBy(_ => _rng.Next()))
                await TakeTurnAsync(p, round);
            await SnapshotAsync(round);
            if (verbose) Console.WriteLine($"  round {round}/{rounds} done");
        }

        Economy = await _observer.Economy.HealthAsync();
        Heroes = await _observer.Heroes.AllAsync();
        Board = await _observer.Leaderboard.TopAsync();
        await _factory.DisposeAsync();
    }

    // ── Onboarding ──────────────────────────────────────────────────────────────

    private async Task SignUpAsync()
    {
        var personas = Enum.GetValues<Persona>();
        for (var i = 0; i < players; i++)
        {
            var persona = personas[i % personas.Length];
            var api = new ArkadeHeroesClient(_factory.CreateClient());
            var name = $"{persona}{i:D2}";

            Player player;
            try
            {
                var dto = await api.Players.RegisterAsync(
                    new RegisterPlayerRequest(name, $"sim-wallet-{seed}-{i:D3}"));
                if (dto.Token is { } t) api.SetAuthToken(t);
                player = new Player { Id = dto.PlayerId, Name = name, Persona = persona, Api = api };
                player.StartingSats = dto.BalanceSats;
                _players.Add(player);
                _engagement.Persona(name, persona);
                _tally.Record("register", Outcome.Ok);
            }
            catch (Exception ex)
            {
                // Onboarding is the one thing that must not abort the run: the report IS the point,
                // and a player who cannot sign up is itself a finding worth printing.
                _tally.Record("register", ex is ArkadeHeroesApiException ? Outcome.Refused : Outcome.Broken, ex.Message);
                continue;
            }

            // Everyone buys their opening roster; two heroes is the minimum that can breed.
            await Attempt(player, "recruit", round: 0);
            await Attempt(player, "recruit", round: 0);
        }

        if (_players.Count == 0)
            throw new InvalidOperationException("No player could sign up; there is nothing to simulate.");
    }

    // ── The turn ────────────────────────────────────────────────────────────────

    private async Task TakeTurnAsync(Player p, int round)
    {
        var actions = Weights(p.Persona);
        var take = 1 + _rng.Next(3);
        for (var i = 0; i < take; i++)
        {
            var action = Pick(actions);
            await Attempt(p, action, round);
        }
    }

    private static (string Action, int Weight)[] Weights(Persona persona) => persona switch
    {
        Persona.Grinder => [("gauntlet", 34), ("trials", 22), ("duel", 20), ("equip", 8), ("buyitem", 8), ("daily", 8)],
        Persona.Breeder => [("breed", 30), ("stud", 20), ("sellhero", 16), ("gauntlet", 14), ("recruit", 10), ("merge", 10)],
        Persona.Duelist => [("duel", 40), ("deathmatch", 18), ("gauntlet", 16), ("equip", 10), ("buyitem", 8), ("squad", 8)],
        Persona.Trader => [("buyoffer", 26), ("sellhero", 24), ("buyitem", 18), ("bid", 14), ("recruit", 10), ("gauntlet", 8)],
        Persona.Whale => [("tournament", 30), ("squad", 22), ("duel", 18), ("buyitem", 12), ("recruit", 10), ("buyoffer", 8)],
        _ => [("daily", 26), ("gauntlet", 26), ("duel", 18), ("trials", 14), ("buyitem", 8), ("breed", 8)],
    };

    private string Pick((string Action, int Weight)[] weights)
    {
        var total = weights.Sum(w => w.Weight);
        var roll = _rng.Next(total);
        foreach (var (action, weight) in weights)
        {
            if (roll < weight) return action;
            roll -= weight;
        }
        return weights[^1].Action;
    }

    private async Task Attempt(Player p, string action, int round)
    {
        try
        {
            var did = action switch
            {
                "recruit" => await RecruitAsync(p),
                "gauntlet" => await GauntletAsync(p),
                "trials" => await TrialsAsync(p),
                "duel" => await DuelAsync(p, round),
                "deathmatch" => await DeathMatchAsync(p, round),
                "squad" => await SquadAsync(p),
                "tournament" => await TournamentAsync(p, round),
                "breed" => await BreedAsync(p, round),
                "stud" => await StudAsync(p),
                "merge" => await MergeAsync(p),
                "buyitem" => await BuyItemAsync(p),
                "equip" => await EquipAsync(p),
                "sellhero" => await SellHeroAsync(p),
                "buyoffer" => await BuyOfferAsync(p),
                "bid" => await BidAsync(p),
                "daily" => await DailyAsync(p),
                _ => throw new InvalidOperationException($"unknown action {action}"),
            };
            _tally.Record(action, did.Ok ? Outcome.Ok : Outcome.Refused, did.Reason);
            _engagement.Action(p.Name, round, action, did.Ok);
        }
        catch (ArkadeHeroesApiException ex)
        {
            _tally.Record(action, Outcome.Refused, ex.Message);
            _engagement.Action(p.Name, round, action, ok: false);
            if (ex.Message.Contains("balance", StringComparison.OrdinalIgnoreCase))
            {
                p.WentBroke = true;
                _engagement.Broke(p.Name, round);
            }
        }
        catch (Exception ex)
        {
            _tally.Record(action, Outcome.Broken, $"{ex.GetType().Name}: {ex.Message}");
            _engagement.Action(p.Name, round, action, ok: false);
        }
    }

    /// The round-boundary read. Uses no RNG, so an instrumented run replays a bare one exactly.
    private async Task SnapshotAsync(int round)
    {
        var board = await _observer.Leaderboard.TopAsync();
        _engagement.Board(round, board.Select(e => (e.HeroId, e.Rank, e.Name)));

        foreach (var p in _players)
        {
            var me = await p.Api.Players.MeAsync();
            var mine = await p.Api.Heroes.MineAsync();
            _engagement.Take(p.Name, new Snapshot(
                round, me.BalanceSats, mine.Count, mine.Sum(h => h.Xp),
                mine.Count == 0 ? 0 : mine.Max(h => h.Level),
                p.Wins, p.Losses, p.HeroesLost));
        }
    }

    private readonly record struct Did(bool Ok, string? Reason = null)
    {
        public static Did Yes => new(true);
        public static Did No(string why) => new(false, why);
    }

    // ── Actions ─────────────────────────────────────────────────────────────────

    private async Task<Did> RecruitAsync(Player p)
    {
        var quote = await p.Api.Heroes.RequestStartersAsync();
        if (quote.Fee is { } fee) await Pay(p, fee.InvoiceId);
        await p.Api.Heroes.ClaimStartersAsync();
        return Did.Yes;
    }

    private async Task<Did> GauntletAsync(Player p)
    {
        var hero = await PickHeroAsync(p);
        if (hero is null) return Did.No("no hero to send");
        var open = await p.Api.Gauntlet.OpenAsync(hero.Id);
        await Pay(p, open.FeeInvoice.InvoiceId);
        var run = await p.Api.Gauntlet.RunAsync(open.GauntletId, Nonce());
        if (run.WavesCleared == 0) _tally.Record("gauntlet:zero-wave", Outcome.Ok);
        return Did.Yes;
    }

    private async Task<Did> TrialsAsync(Player p)
    {
        var hero = await PickHeroAsync(p);
        if (hero is null) return Did.No("no hero to send");
        var open = await p.Api.Trials.OpenAsync(hero.Id);
        var run = await p.Api.Trials.RunAsync(open.TrialsId, Nonce());
        if (run.WavesCleared == 0) _tally.Record("trials:zero-wave", Outcome.Ok);
        return Did.Yes;
    }

    private async Task<Did> DuelAsync(Player p, int round)
    {
        var mine = await PickHeroAsync(p);
        if (mine is null) return Did.No("no hero to fight with");

        var suggestions = await p.Api.Matches.MatchmakingAsync(mine.Id);
        if (suggestions.Count == 0) return Did.No("matchmaking suggested nobody");
        var pick = suggestions[_rng.Next(Math.Min(4, suggestions.Count))];
        var defender = _players.FirstOrDefault(x => x.Id == pick.OwnerPlayerId);
        if (defender is null) return Did.No("suggested opponent is not a simulated player");

        var wager = 500L * (1 + _rng.Next(4));
        var open = await p.Api.Matches.OpenAsync(
            new OpenMatchRequest(mine.Id, pick.Hero.Id, wager));
        // Staked play is covenant-only: each side funds its OWN escrow and the pot settles from there,
        // so there is no stake invoice to pay and the treasury never holds the money.
        await p.Api.Dev.StakeEscrowAsync(new { MatchId = open.MatchId });
        if (open.MatchFeeInvoice is { } f) await Pay(p, f.InvoiceId);

        var accept = await defender.Api.Matches.AcceptAsync(open.MatchId);
        await defender.Api.Dev.StakeEscrowAsync(new { MatchId = open.MatchId });
        if (accept.MatchFeeInvoice is { } df) await Pay(defender, df.InvoiceId);

        var fight = await p.Api.Matches.FightAsync(open.MatchId, new FightRequest(Nonce()));
        var challengerWon = fight.Result.WinnerId == mine.Id;
        (challengerWon ? p : defender).Wins++;
        (challengerWon ? defender : p).Losses++;
        if (fight.ChallengerXpAward == 0 && fight.DefenderXpAward == 0)
            _tally.Record("duel:zero-xp", Outcome.Ok);
        return Did.Yes;
    }

    private async Task<Did> DeathMatchAsync(Player p, int round)
    {
        var mine = await PickHeroAsync(p);
        if (mine is null) return Did.No("no hero to stake");
        var target = await PickOtherHeroAsync(p);
        if (target is null) return Did.No("nobody else owns a hero");
        var defender = _players.First(x => x.Id == target.OwnerId);

        var open = await p.Api.DeathMatch.OpenAsync(new DeathMatchOpenRequest(mine.Id, target.Id));
        if (open.FeeInvoice is { } f) await Pay(p, f.InvoiceId);
        var accept = await defender.Api.DeathMatch.AcceptAsync(open.DeathMatchId);
        if (accept.FeeInvoice is { } df) await Pay(defender, df.InvoiceId);

        await p.Api.Dev.FundDeathMatchEscrowAsync(new { DeathMatchId = open.DeathMatchId, Role = "challenger" });
        await defender.Api.Dev.FundDeathMatchEscrowAsync(new { DeathMatchId = open.DeathMatchId, Role = "defender" });

        var settled = await p.Api.DeathMatch.SettleAsync(open.DeathMatchId, new DeathMatchSettleRequest(Nonce()));
        var loserOwner = settled.LoserHeroId == mine.Id ? p : defender;
        loserOwner.HeroesLost++;
        var death = $"{p.Name} death-matched {defender.Name}: {loserOwner.Name} lost a hero permanently";
        _tally.Note(death);
        _engagement.Event(round, "permadeath", death);
        return Did.Yes;
    }

    private async Task<Did> SquadAsync(Player p)
    {
        var mine = (await p.Api.Heroes.MineAsync()).Take(3).ToList();
        if (mine.Count < 3) return Did.No("squad needs three heroes");

        Player? other = null;
        List<HeroDto> theirs = [];
        foreach (var candidate in _players.Where(x => x.Id != p.Id).OrderBy(_ => _rng.Next()))
        {
            var roster = (await candidate.Api.Heroes.MineAsync()).Take(3).ToList();
            if (roster.Count < 3) continue;
            (other, theirs) = (candidate, roster);
            break;
        }
        if (other is null) return Did.No("nobody else fields three heroes");

        var open = await p.Api.Squad.OpenAsync(new OpenSquadMatchRequest(
            [.. mine.Select(h => h.Id)], [.. theirs.Select(h => h.Id)], 500));
        await p.Api.Dev.StakeEscrowAsync(new { MatchId = open.MatchId });
        if (open.MatchFeeInvoice is { } f) await Pay(p, f.InvoiceId);
        var accept = await other.Api.Squad.AcceptAsync(open.MatchId);
        await other.Api.Dev.StakeEscrowAsync(new { MatchId = open.MatchId });
        if (accept.MatchFeeInvoice is { } df) await Pay(other, df.InvoiceId);
        await p.Api.Squad.ResolveAsync(open.MatchId, new FightRequest(Nonce()));
        return Did.Yes;
    }

    private async Task<Did> TournamentAsync(Player p, int round)
    {
        var hero = await PickHeroAsync(p);
        if (hero is null) return Did.No("no hero to enter");

        var open = (await p.Api.Tournament.ListAsync()).FirstOrDefault(t => t.Status == "open" && t.Joined < t.Size);
        if (open is null)
        {
            var created = await p.Api.Tournament.OpenAsync(new OpenTournamentRequest(hero.Id, 1_000, 4));
            await Pay(p, created.BuyIn.InvoiceId);
            return Did.Yes;
        }

        if (open.Entrants.Any(e => e.PlayerId == p.Id)) return Did.No("already entered this bracket");
        var joined = await p.Api.Tournament.JoinAsync(open.Id, new JoinTournamentRequest(hero.Id));
        await Pay(p, joined.BuyIn.InvoiceId);

        var now = await p.Api.Tournament.GetAsync(open.Id);
        if (now.Joined < now.Size) return Did.Yes;
        var opener = _players.First(x => x.Id == now.OpenerPlayerId);
        var resolved = await opener.Api.Tournament.ResolveAsync(now.Id, new FightRequest(Nonce()));
        var line = $"tournament {now.Id[..8]} resolved: prizes {string.Join("/", resolved.Prizes)} sats";
        _tally.Note(line);
        _engagement.Event(round, "tournament", line);
        return Did.Yes;
    }

    private async Task<Did> BreedAsync(Player p, int round)
    {
        var mine = (await p.Api.Heroes.MineAsync())
            .Where(h => !h.IsSterile && (h.BreedCooldownUntil is null || h.BreedCooldownUntil <= DateTimeOffset.UtcNow))
            .ToList();
        if (mine.Count < 2) return Did.No("fewer than two breedable heroes");
        var a = mine[_rng.Next(mine.Count)];
        var b = mine.First(h => h.Id != a.Id);

        var commit = await p.Api.Breeding.CommitAsync(new BreedCommitRequest(a.Id, b.Id));
        if (commit.Invoice is { } inv) await Pay(p, inv.InvoiceId);
        var reveal = await p.Api.Breeding.RevealAsync(commit.BreedingId, new BreedRevealRequest(Nonce()));
        if (reveal.Hero.Rarity?.Tier is "Legendary" or "Epic")
        {
            var born = $"{p.Name} bred a {reveal.Hero.Rarity!.Tier}: {reveal.Hero.Name}";
            _tally.Note(born);
            _engagement.Event(round, "rare-birth", born);
        }
        return Did.Yes;
    }

    private async Task<Did> StudAsync(Player p)
    {
        var mine = await PickHeroAsync(p);
        if (mine is null) return Did.No("no hero of my own");
        var stud = await PickOtherHeroAsync(p);
        if (stud is null) return Did.No("no other player's hero to breed with");
        var owner = _players.First(x => x.Id == stud.OwnerId);

        var proposal = await p.Api.Stud.ProposeAsync(new StudProposeRequest(mine.Id, stud.Id, 500));
        var accepted = await owner.Api.Stud.AcceptAsync(proposal.ProposalId);
        await Pay(p, accepted.BreedFeeInvoice.InvoiceId);
        if (accepted.StudFeeInvoice is { } sf) await Pay(p, sf.InvoiceId);
        await p.Api.Stud.RevealAsync(proposal.ProposalId, new StudRevealRequest(Nonce()));
        return Did.Yes;
    }

    private async Task<Did> MergeAsync(Player p)
    {
        var mine = (await p.Api.Heroes.MineAsync()).ToList();
        if (mine.Count < 3) return Did.No("merge burns a hero, so it needs a spare");
        var basis = mine[0];
        var sacrifice = mine[^1];

        var commit = await p.Api.Merge.CommitAsync(new MergeCommitRequest(basis.Id, sacrifice.Id));
        await p.Api.Dev.FundMergeEscrowAsync(new { MergeId = commit.MergeId });
        var reveal = await p.Api.Merge.RevealAsync(commit.MergeId, new MergeRevealRequest(Nonce()));
        if (reveal.Hero.IsSterile) _tally.Note($"{p.Name}'s fusion came out STERILE ({reveal.Hero.Rarity?.Tier})");
        return Did.Yes;
    }

    private async Task<Did> BuyItemAsync(Player p)
    {
        var hero = await PickHeroAsync(p);
        var shop = await p.Api.Items.ShopAsync();
        var affordable = shop.Where(i => hero is null || i.MinLevel <= hero.Level).ToList();
        if (affordable.Count == 0) return Did.No("nothing in the shop is usable at my level");
        var item = affordable[_rng.Next(affordable.Count)];
        var invoice = (await p.Api.Items.BuyAsync(item.Id)).Invoice;
        await Pay(p, invoice.InvoiceId);
        await p.Api.Items.ClaimAsync(new ClaimItemRequest(invoice.InvoiceId));
        return Did.Yes;
    }

    private async Task<Did> EquipAsync(Player p)
    {
        var hero = await PickHeroAsync(p);
        if (hero is null) return Did.No("no hero to equip");
        var owned = (await p.Api.Items.MineAsync()).Keys.ToList();
        if (owned.Count == 0) return Did.No("own no gear");
        await p.Api.Heroes.EquipAsync(hero.Id, new EquipRequest(owned[_rng.Next(owned.Count)]));
        return Did.Yes;
    }

    private async Task<Did> SellHeroAsync(Player p)
    {
        var mine = (await p.Api.Heroes.MineAsync()).ToList();
        if (mine.Count < 3) return Did.No("keeping my last two heroes");
        var hero = mine[^1];
        var offer = await p.Api.Offers.CreateHeroAsync(new CreateHeroOfferRequest(hero.Id, 2_000 + 500 * _rng.Next(8)));
        await p.Api.Dev.FundOfferAsync(new { OfferId = offer.OfferId });
        return Did.Yes;
    }

    private async Task<Did> BuyOfferAsync(Player p)
    {
        var offers = (await p.Api.Offers.ListAsync()).Where(o => o.SellerId != p.Id).ToList();
        if (offers.Count == 0) return Did.No("nothing listed by anyone else");
        var offer = offers[_rng.Next(offers.Count)];
        await p.Api.Dev.FulfillOfferAsync(new { OfferId = offer.OfferId });
        if (offer.Kind == "hero") await p.Api.Offers.ClaimHeroAsync(offer.OfferId);
        return Did.Yes;
    }

    /// The whole bid protocol, since a bid nobody accepts proves nothing: propose → the owner
    /// consents → the bidder pays → the owner delivers the hero → settle.
    private async Task<Did> BidAsync(Player p)
    {
        var target = await PickOtherHeroAsync(p);
        if (target is null) return Did.No("no hero to bid on");
        var owner = _players.First(x => x.Id == target.OwnerId);
        if ((await owner.Api.Heroes.MineAsync()).Count < 2) return Did.No("owner is down to their last hero");

        var bid = await p.Api.Bids.PlaceAsync(new PlaceBidRequest(target.Id, 3_000));
        var accepted = await owner.Api.Bids.AcceptAsync(bid.BidId);
        await Pay(p, accepted.Invoice.InvoiceId);
        await owner.Api.Dev.TransferAssetAsync(new { AssetId = target.AssetId, ToPlayerId = p.Id });
        await p.Api.Bids.SettleAsync(bid.BidId);
        return Did.Yes;
    }

    private async Task<Did> DailyAsync(Player p)
    {
        var status = await p.Api.Daily.StatusAsync();
        if (status.ClaimedToday) return Did.No("already claimed today");
        await p.Api.Daily.ClaimAsync();
        return Did.Yes;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private async Task<HeroDto?> PickHeroAsync(Player p)
    {
        var mine = (await p.Api.Heroes.MineAsync()).ToList();
        return mine.Count == 0 ? null : mine[_rng.Next(mine.Count)];
    }

    private async Task<HeroDto?> PickOtherHeroAsync(Player p)
    {
        var all = (await _observer.Heroes.AllAsync())
            .Where(h => h.OwnerId != p.Id && _players.Any(x => x.Id == h.OwnerId)).ToList();
        return all.Count == 0 ? null : all[_rng.Next(all.Count)];
    }

    private static async Task Pay(Player p, string invoiceId) =>
        await p.Api.Dev.PayInvoiceAsync(new { InvoiceId = invoiceId });

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
}
