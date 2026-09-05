using System.Text;
using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Server;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ArkadeHeroes.Sim;

/// <summary>
/// Whether the marketplace is an ECONOMY or merely working plumbing. Boots the real server the way
/// <see cref="Simulation"/> does and drives a market-only workload: sellers list heroes and gear across a
/// spread of asks, buyers browse and take the best deal they will accept, sellers pull stale listings.
///
/// The only price judgement here is the GAME'S OWN: an item is referenced against its shop price (an
/// identical unit is always on sale new) and a hero against the recruit claim fee. So "nobody would pay
/// that" is the game's own number, never an invented valuation.
/// </summary>
public static class MarketLiquidity
{
    public static async Task<string> RenderAsync(int players, int rounds, int seed)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseContentRoot(ServerContentRoot());
            b.UseSetting("Logging:LogLevel:Default", "Error");
        });
        try
        {
            var market = new Market(factory, seed);
            await market.SetUpAsync(players);
            if (market.Traders.Count < 2)
                return $"MARKET LIQUIDITY — seed {seed}\n"
                       + "  Fewer than two traders could register, so there is no market to measure.\n\n"
                       + market.Tally.Render();
            for (var round = 1; round <= rounds; round++) await market.RoundAsync(round);
            return await market.ReportAsync(rounds, seed);
        }
        finally
        {
            await factory.DisposeAsync();
        }
    }

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

    /// <summary>What a buyer will pay, as a multiple of the game's own price for the same thing.</summary>
    private enum Shopper { Bargain, Collector, Anything }

    private sealed class Trader
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required ArkadeHeroesClient Api { get; init; }
        public required Shopper Shopper { get; init; }
        public long OpeningSats { get; set; }
        public long ClosingSats { get; set; }
        public int Browsed { get; set; }
        public int Bought { get; set; }
        public int Sold { get; set; }
    }

    private sealed class Listing
    {
        public required string OfferId { get; init; }
        public required Trader Seller { get; init; }
        public required string Kind { get; init; }
        public required string What { get; init; }
        public required long AskSats { get; init; }
        public required long FeeSats { get; init; }
        public required long ReferenceSats { get; init; }
        public required int ListedRound { get; init; }
        public required int Sequence { get; init; }
        public int? SoldRound { get; set; }
        public int? PulledRound { get; set; }
        public bool Resting => SoldRound is null && PulledRound is null;
        public double Ratio => ReferenceSats <= 0 ? 0 : (double)AskSats / ReferenceSats;
    }

    private sealed record BlockedAsk(string Kind, string What, long ReferenceSats, long IntendedAsk);

    private sealed class Market(WebApplicationFactory<Program> factory, int seed)
    {
        private static readonly double[] AskBands = [0.4, 0.7, 1.0, 1.4, 2.0, 3.0];

        private readonly Random _rng = new(seed);
        private readonly List<Listing> _listings = [];
        private readonly List<BlockedAsk> _blocked = [];
        private readonly Dictionary<string, long> _shopPrice = [];
        private readonly ArkadeHeroesClient _observer = new(factory.CreateClient());

        private long _listingFee;
        private long _heroReference = 1_000;
        private long _openingWallets;
        private long _openingTreasury;
        private int _browsed, _foundNothing, _couldNotAfford, _allTooDear, _bought;
        // Rounds where the trader had nothing to list, so nothing was ever offered to the server.
        // Counted apart from refusals: folding them in would attribute to the game listings it
        // never saw, and inflate the share of attempts it turned away.
        private int _listSkipped;
        private long _toSellers, _toTreasury;

        public Tally Tally { get; } = new();
        public List<Trader> Traders { get; } = [];

        // ── Setup ───────────────────────────────────────────────────────────────

        public async Task SetUpAsync(int players)
        {
            var info = await _observer.Chain.InfoAsync();
            _listingFee = info.Config?.OfferListingFeeSats ?? 0;
            foreach (var item in await _observer.Items.ShopAsync()) _shopPrice[item.Id] = item.PriceSats;

            for (var i = 0; i < players; i++)
            {
                var api = new ArkadeHeroesClient(factory.CreateClient());
                var name = $"MKT{i:D2}";
                try
                {
                    var dto = await api.Players.RegisterAsync(
                        new RegisterPlayerRequest(name, $"mkt-wallet-{seed}-{i:D3}"));
                    if (dto.Token is { } t) api.SetAuthToken(t);
                    Traders.Add(new Trader
                    {
                        Id = dto.PlayerId, Name = name, Api = api, Shopper = (Shopper)(i % 3),
                        OpeningSats = dto.BalanceSats,
                    });
                    Tally.Record("register", Outcome.Ok);
                }
                catch (Exception ex)
                {
                    Tally.Record("register", ex is ArkadeHeroesApiException ? Outcome.Refused : Outcome.Broken, ex.Message);
                }
            }

            _openingWallets = Traders.Sum(t => t.OpeningSats);
            _openingTreasury = (await _observer.Economy.HealthAsync()).TreasuryBalanceSats;
            foreach (var trader in Traders) await EndowAsync(trader);
        }

        /// Inventory to trade with: three recruits, and one unit of each shop price tier (two of the
        /// cheapest, since the cheapest tier is where the listing fee is closest to the item's own price).
        private async Task EndowAsync(Trader t)
        {
            for (var i = 0; i < 3; i++) await Run(t, "endow:recruit", 0, () => RecruitAsync(t));
            var tiers = _shopPrice.Values.Distinct().OrderBy(p => p).ToList();
            foreach (var price in tiers)
            {
                var choices = _shopPrice.Where(kv => kv.Value == price).Select(kv => kv.Key)
                    .OrderBy(id => id, StringComparer.Ordinal).ToList();
                var units = price == tiers[0] ? 2 : 1;
                for (var i = 0; i < units; i++)
                {
                    var itemId = choices[_rng.Next(choices.Count)];
                    await Run(t, "endow:gear", 0, () => BuyFromShopAsync(t, itemId));
                }
            }
        }

        private async Task RecruitAsync(Trader t)
        {
            var quote = await t.Api.Heroes.RequestStartersAsync();
            if (quote.FeeSats > 0) _heroReference = quote.FeeSats;
            if (quote.Fee is { } fee) await Pay(t, fee.InvoiceId);
            await t.Api.Heroes.ClaimStartersAsync();
        }

        private async Task BuyFromShopAsync(Trader t, string itemId)
        {
            var invoice = (await t.Api.Items.BuyAsync(itemId)).Invoice;
            await Pay(t, invoice.InvoiceId);
            await t.Api.Items.ClaimAsync(new ClaimItemRequest(invoice.InvoiceId));
        }

        // ── The round ───────────────────────────────────────────────────────────

        /// Two actions per trader per round, buy-side and sell-side pressure drawn from the same weights so
        /// neither is baked in — a workload that browsed every round would guarantee demand outran supply
        /// and would report a sell-through rate it had itself created.
        public async Task RoundAsync(int round)
        {
            foreach (var t in Traders.OrderBy(_ => _rng.Next()).ToList())
                for (var i = 0; i < 2; i++)
                {
                    var action = _rng.Next(100) switch
                    {
                        < 40 => "buy", < 60 => "list-item", < 80 => "list-hero", _ => "pull",
                    };
                    await Run(t, action, round, action switch
                    {
                        "buy" => () => BuyAsync(t, round),
                        "list-item" => () => ListItemAsync(t, round),
                        "list-hero" => () => ListHeroAsync(t, round),
                        _ => () => PullAsync(t, round),
                    });
                }
        }

        private async Task Run(Trader t, string action, int round, Func<Task> body)
        {
            try
            {
                await body();
                Tally.Record(action, Outcome.Ok);
            }
            catch (Refusal ex)
            {
                Tally.Record(action, Outcome.Refused, ex.Message);
            }
            catch (ArkadeHeroesApiException ex)
            {
                Tally.Record(action, Outcome.Refused, ex.Message);
                if (ex.Message.Contains("marketplace fee", StringComparison.OrdinalIgnoreCase)) return;
                if (round == 0) Tally.Note($"{t.Name} could not be endowed ({action}): {ex.Message}");
            }
            catch (Exception ex)
            {
                Tally.Record(action, Outcome.Broken, $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        private async Task ListItemAsync(Trader t, int round)
        {
            var owned = await t.Api.Items.MineAsync();
            if (owned.Count == 0) { _listSkipped++; throw new Refusal("hold no gear unit that is free to list"); }
            var itemId = owned[_rng.Next(owned.Count)];
            var reference = _shopPrice.GetValueOrDefault(itemId, 0);
            await ListAsync(t, round, "item", itemId, reference,
                ask => t.Api.Offers.CreateItemAsync(new CreateOfferRequest(itemId, ask)));
        }

        private async Task ListHeroAsync(Trader t, int round)
        {
            var listed = _listings.Where(l => l.Seller.Id == t.Id && l.Kind == "hero" && l.Resting)
                .Select(l => l.What).ToHashSet(StringComparer.Ordinal);
            var mine = (await t.Api.Heroes.MineAsync()).Where(h => !listed.Contains(h.Id)).ToList();
            if (mine.Count == 0) { _listSkipped++; throw new Refusal("own no hero that is not already listed"); }
            var hero = mine[_rng.Next(mine.Count)];
            await ListAsync(t, round, "hero", hero.Id, _heroReference,
                ask => t.Api.Offers.CreateHeroAsync(new CreateHeroOfferRequest(hero.Id, ask)));
        }

        private async Task ListAsync(Trader t, int round, string kind, string what, long reference,
            Func<long, Task<CreateOfferResponse>> create)
        {
            var ask = Math.Max(1, (long)Math.Round(reference * AskBands[_rng.Next(AskBands.Length)]));
            CreateOfferResponse offer;
            try
            {
                offer = await create(ask);
            }
            catch (ArkadeHeroesApiException ex) when (ex.Message.Contains("marketplace fee", StringComparison.OrdinalIgnoreCase))
            {
                _blocked.Add(new BlockedAsk(kind, what, reference, ask));
                throw;
            }
            await t.Api.Dev.FundOfferAsync(new { OfferId = offer.OfferId });
            _listings.Add(new Listing
            {
                OfferId = offer.OfferId, Seller = t, Kind = kind, What = what, AskSats = offer.AskSats,
                FeeSats = offer.ListingFeeSats, ReferenceSats = reference, ListedRound = round,
                Sequence = _listings.Count,
            });
        }

        private async Task BuyAsync(Trader t, int round)
        {
            _browsed++;
            t.Browsed++;
            var offers = (await t.Api.Offers.ListAsync()).Where(o => o.SellerId != t.Id).ToList();
            if (offers.Count == 0) { _foundNothing++; throw new Refusal("nothing listed by anyone else"); }

            var balance = (await t.Api.Players.MeAsync()).BalanceSats;
            var affordable = offers.Where(o => o.AskSats <= balance).ToList();
            if (affordable.Count == 0) { _couldNotAfford++; throw new Refusal("everything listed costs more than I hold"); }

            // Ties break on the order the listing was CREATED, never on the offer id: the server mints ids
            // from a cryptographic RNG, so an id tie-break would make the run unreproducible at a fixed seed.
            var byId = _listings.ToDictionary(l => l.OfferId, l => l.Sequence, StringComparer.Ordinal);
            var cap = t.Shopper switch { Shopper.Bargain => 1.0, Shopper.Collector => 2.0, _ => double.MaxValue };
            var acceptable = affordable
                .Select(o => (Offer: o, Ratio: RatioOf(o)))
                .Where(x => x.Ratio <= cap)
                .OrderBy(x => x.Ratio).ThenBy(x => x.Offer.AskSats)
                .ThenBy(x => byId.GetValueOrDefault(x.Offer.OfferId, int.MaxValue)).ToList();
            if (acceptable.Count == 0)
            {
                _allTooDear++;
                throw new Refusal("every listing asks more than the game itself charges for the same thing");
            }

            var pick = acceptable[0].Offer;
            await t.Api.Dev.FulfillOfferAsync(new { OfferId = pick.OfferId });
            if (pick.Kind == "hero") await t.Api.Offers.ClaimHeroAsync(pick.OfferId);

            _bought++;
            t.Bought++;
            var sale = _listings.FirstOrDefault(l => l.OfferId == pick.OfferId);
            var fee = sale?.FeeSats ?? 0;
            _toSellers += pick.AskSats - fee;
            _toTreasury += fee;
            if (sale is not null)
            {
                sale.SoldRound = round;
                sale.Seller.Sold++;
                if (sale.Ratio >= 3.0)
                    Tally.Note($"{t.Name} paid {pick.AskSats:N0} for {Describe(sale)} — {sale.Ratio:F1}x what the game charges");
            }
        }

        private async Task PullAsync(Trader t, int round)
        {
            var mine = _listings
                .Where(l => l.Seller.Id == t.Id && l.Resting && round - l.ListedRound >= 1)
                .OrderBy(l => l.Sequence).ToList();
            if (mine.Count == 0) throw new Refusal("nothing of mine has rested long enough to be worth pulling");
            var listing = mine[_rng.Next(mine.Count)];
            await t.Api.Dev.ReclaimOfferAsync(new { OfferId = listing.OfferId });
            listing.PulledRound = round;
        }

        private double RatioOf(OfferDto o)
        {
            var reference = o.Kind == "hero" ? _heroReference : _shopPrice.GetValueOrDefault(o.ItemId, 0);
            return reference <= 0 ? double.MaxValue : (double)o.AskSats / reference;
        }

        private static string Describe(Listing l) => l.Kind == "hero" ? "a hero" : l.What;

        private static async Task Pay(Trader t, string invoiceId) =>
            await t.Api.Dev.PayInvoiceAsync(new { InvoiceId = invoiceId });

        private sealed class Refusal(string why) : Exception(why);

        // ── The report ──────────────────────────────────────────────────────────

        public async Task<string> ReportAsync(int rounds, int seed)
        {
            foreach (var t in Traders)
                t.ClosingSats = (await t.Api.Players.MeAsync()).BalanceSats;
            // Health is read BEFORE and AFTER a market read on purpose: an item sale books its fee only when
            // someone next reconciles the offer, so the gap between these two is income the treasury already
            // holds and had not yet written down.
            var atRest = await _observer.Economy.HealthAsync();
            var resting = await _observer.Offers.ListAsync();
            var soldStrip = await _observer.Offers.SoldAsync(24);
            var health = await _observer.Economy.HealthAsync();

            var sb = new StringBuilder();
            sb.AppendLine($"MARKET LIQUIDITY — {Traders.Count} traders, {rounds} rounds, seed {seed}");
            sb.AppendLine($"  marketplace fee: {_listingFee:N0} sats per sale, absorbed by the seller out of the ask");
            sb.AppendLine($"  reference price = the game's own cheapest equivalent: a hero {_heroReference:N0} sats "
                          + "(a fresh recruit); gear, its shop price");
            sb.AppendLine($"  asks are drawn from {string.Join("/", AskBands.Select(b => $"{b:0.0}x"))} of that reference");
            foreach (var group in Traders.GroupBy(t => t.Shopper).OrderBy(g => g.Key))
                sb.Append($"  {group.Key}({group.Count()}) pays up to {Cap(group.Key)}   ");
            sb.AppendLine();

            AppendFeeFloor(sb);
            AppendListings(sb);
            AppendBlocked(sb);
            AppendTimeToSale(sb);
            AppendPriceVsSold(sb);
            AppendMoney(sb, health, atRest);
            AppendBuyerExperience(sb);
            AppendServerView(sb, health, resting, soldStrip);

            sb.AppendLine();
            sb.Append(Tally.Render());
            return sb.ToString();
        }

        private static string Cap(Shopper s) => s switch
        {
            Shopper.Bargain => "1.0x the reference",
            Shopper.Collector => "2.0x the reference",
            _ => "any ask it can afford",
        };

        private void AppendFeeFloor(StringBuilder sb)
        {
            sb.AppendLine();
            sb.AppendLine("THE FLOOR THE FEE PUTS UNDER EVERY LISTING   (game constants only — no demand model)");
            sb.AppendLine($"  {"what",-28} {"game's own price",16} {"cheapest legal ask",19} {"seller nets",12} {"floor",8}");
            var rows = _shopPrice.GroupBy(kv => kv.Value).OrderBy(g => g.Key)
                .Select(g => ($"gear, {g.Count()} items at this tier", g.Key)).ToList();
            rows.Insert(0, ("a hero (a fresh recruit)", _heroReference));
            foreach (var (what, price) in rows)
            {
                var floor = _listingFee + 1;
                sb.AppendLine($"  {what,-28} {price,16:N0} {floor,19:N0} {floor - _listingFee,12:N0} "
                              + $"{(price <= 0 ? "n/a" : $"{(double)floor / price:0.00}x"),8}");
            }
            sb.AppendLine("  A floor above 1.00x means the cheapest listing the game will accept already costs the");
            sb.AppendLine("  buyer more than buying the same thing new from the game.");
        }

        private void AppendListings(StringBuilder sb)
        {
            var attempted = _listings.Count
                            + Tally.Count("list-item", Outcome.Refused) + Tally.Count("list-hero", Outcome.Refused)
                            - _listSkipped;
            var sold = _listings.Count(l => l.SoldRound is not null);
            var pulled = _listings.Count(l => l.PulledRound is not null);
            var stillResting = _listings.Count(l => l.Resting);
            sb.AppendLine();
            sb.AppendLine("LISTINGS");
            sb.AppendLine($"  offered to the game {attempted}   accepted {_listings.Count}   "
                          + $"REFUSED {attempted - _listings.Count} ({Pct(attempted - _listings.Count, attempted)})"
                          + $", of which the fee floor blocked {_blocked.Count}");
            sb.AppendLine($"  (a further {_listSkipped} rounds had nothing to list and were never offered — "
                          + "excluded above, so the refusal rate is the game's and not the harness's)");
            sb.AppendLine($"  of the {_listings.Count} that existed:  SOLD {sold} ({Pct(sold, _listings.Count)})   "
                          + $"pulled by the seller {pulled} ({Pct(pulled, _listings.Count)})   "
                          + $"still resting {stillResting} ({Pct(stillResting, _listings.Count)})");
            foreach (var kind in new[] { "hero", "item" })
            {
                var of = _listings.Where(l => l.Kind == kind).ToList();
                if (of.Count == 0) continue;
                sb.AppendLine($"    {kind,-5} listed {of.Count,4}   sold {of.Count(l => l.SoldRound is not null),4} "
                              + $"({Pct(of.Count(l => l.SoldRound is not null), of.Count)})");
            }
        }

        private void AppendBlocked(StringBuilder sb)
        {
            sb.AppendLine();
            sb.AppendLine("ASKS THE GAME WOULD NOT ACCEPT   (ask <= the marketplace fee, so the covenant cannot be built)");
            if (_blocked.Count == 0) { sb.AppendLine("  none"); return; }
            sb.AppendLine($"  {"what",-22} {"reference",10} {"blocked",8} {"asks refused",-28}");
            foreach (var g in _blocked
                         .GroupBy(b => (Label: b.Kind == "hero" ? "a hero" : b.What, b.ReferenceSats))
                         .OrderBy(g => g.Key.ReferenceSats).ThenBy(g => g.Key.Label, StringComparer.Ordinal))
            {
                var asks = g.Select(b => b.IntendedAsk).Distinct().OrderBy(a => a).Select(a => $"{a:N0}");
                sb.AppendLine($"  {g.Key.Label,-22} {g.Key.ReferenceSats,10:N0} {g.Count(),8} {string.Join(", ", asks),-28}");
            }
        }

        private void AppendTimeToSale(StringBuilder sb)
        {
            var sold = _listings.Where(l => l.SoldRound is not null).ToList();
            sb.AppendLine();
            sb.AppendLine("TIME TO SALE   (rounds a listing rested before a buyer took it)");
            if (sold.Count == 0)
            {
                sb.AppendLine($"  nothing sold — all {_listings.Count} listings ended unsold");
                return;
            }
            foreach (var bucket in new[] { 0, 1, 2, 3 })
            {
                var n = sold.Count(l => l.SoldRound!.Value - l.ListedRound == bucket);
                sb.AppendLine($"  {(bucket == 0 ? "same round" : $"+{bucket} round(s)"),-14} "
                              + $"{new string('#', Math.Min(50, n)),-50} {n,4} ({Pct(n, sold.Count)})");
            }
            var slow = sold.Count(l => l.SoldRound!.Value - l.ListedRound >= 4);
            sb.AppendLine($"  {"+4 or more",-14} {new string('#', Math.Min(50, slow)),-50} {slow,4} ({Pct(slow, sold.Count)})");
            sb.AppendLine($"  median {Median([.. sold.Select(l => (double)(l.SoldRound!.Value - l.ListedRound))]):F1} rounds   "
                          + $"slowest {sold.Max(l => l.SoldRound!.Value - l.ListedRound)}");
        }

        private void AppendPriceVsSold(StringBuilder sb)
        {
            sb.AppendLine();
            sb.AppendLine("ASK vs WHETHER IT SOLD   (ask as a multiple of the game's own price for the same thing)");
            sb.AppendLine($"  {"band",-14} {"listed",7} {"sold",6} {"sold%",7} {"median ask",12} {"median rounds resting",22}");
            foreach (var band in _listings.GroupBy(l => Math.Round(l.Ratio, 1)).OrderBy(g => g.Key))
            {
                var inBand = band.ToList();
                var sold = inBand.Where(l => l.SoldRound is not null).ToList();
                sb.AppendLine($"  {$"{band.Key:0.0}x",-14} {inBand.Count,7} {sold.Count,6} {Pct(sold.Count, inBand.Count),7} "
                              + $"{Median([.. inBand.Select(l => (double)l.AskSats)]),12:N0} "
                              + $"{(sold.Count == 0 ? "-" : $"{Median([.. sold.Select(l => (double)(l.SoldRound!.Value - l.ListedRound))]):F1}"),22}");
            }
            var dear = _listings.Where(l => l.SoldRound is not null && l.Ratio > 1.0).ToList();
            sb.AppendLine($"  sales struck ABOVE the game's own price for the same thing: {dear.Count} of "
                          + $"{_listings.Count(l => l.SoldRound is not null)}");
            var highest = _listings.Where(l => l.SoldRound is not null).OrderByDescending(l => l.AskSats).FirstOrDefault();
            var unsoldFloor = _listings.Where(l => l.Resting || l.PulledRound is not null)
                .OrderBy(l => l.AskSats).FirstOrDefault();
            sb.AppendLine($"  highest ask that DID clear: {(highest is null ? "none" : $"{highest.AskSats:N0} sats ({highest.Ratio:0.00}x)")}"
                          + $"   cheapest ask that did NOT: {(unsoldFloor is null ? "none" : $"{unsoldFloor.AskSats:N0} sats ({unsoldFloor.Ratio:0.00}x)")}");
        }

        private void AppendMoney(StringBuilder sb, EconomyHealthDto health, EconomyHealthDto atRest)
        {
            var turnover = _toSellers + _toTreasury;
            var booked = health.InflowByTag.GetValueOrDefault("listing");
            var bookedAtRest = atRest.InflowByTag.GetValueOrDefault("listing");
            var closingWallets = Traders.Sum(t => t.ClosingSats);
            sb.AppendLine();
            sb.AppendLine("WHERE THE SATS WENT");
            sb.AppendLine($"  buyer -> seller     {_toSellers,12:N0} sats across {_bought} sales");
            sb.AppendLine($"  buyer -> treasury   {_toTreasury,12:N0} sats  ({Pct(_toTreasury, Math.Max(1, turnover))} of turnover)");
            sb.AppendLine($"  turnover            {turnover,12:N0} sats  "
                          + $"({(Traders.Count == 0 ? "n/a" : $"{turnover / (double)Math.Max(1, _openingWallets):P1}")} of the wallets that started the run)");
            sb.AppendLine($"  the server's own books: InflowByTag[\"listing\"] = {booked:N0} — "
                          + (booked == _toTreasury ? "agrees with the fees measured here" : $"DISAGREES with the {_toTreasury:N0} measured here"));
            sb.AppendLine($"  booked before anyone re-read the market: {bookedAtRest:N0} — "
                          + (bookedAtRest == booked
                              ? "nothing was waiting on a reconcile"
                              : $"{booked - bookedAtRest:N0} sats of fees the treasury ALREADY HELD were unwritten "
                                + "until a market read forced the reconcile"));
            sb.AppendLine($"  wallets {_openingWallets:N0} -> {closingWallets:N0} ({closingWallets - _openingWallets:+#,##0;-#,##0;0})   "
                          + $"treasury {_openingTreasury:N0} -> {health.TreasuryBalanceSats:N0} "
                          + $"({health.TreasuryBalanceSats - _openingTreasury:+#,##0;-#,##0;0})   "
                          + $"net {(closingWallets - _openingWallets) + (health.TreasuryBalanceSats - _openingTreasury):+#,##0;-#,##0;0}");
            var traded = Traders.Where(t => t.Bought > 0 || t.Sold > 0).ToList();
            sb.AppendLine($"  traders who moved anything: {traded.Count}/{Traders.Count}   "
                          + $"bought nothing all run: {Traders.Count(t => t.Bought == 0)}   "
                          + $"sold nothing all run: {Traders.Count(t => t.Sold == 0)}");
        }

        private void AppendBuyerExperience(StringBuilder sb)
        {
            sb.AppendLine();
            sb.AppendLine("A BUYER WITH SATS IN HAND, LOOKING TO SPEND");
            sb.AppendLine($"  browsed the market {_browsed} times");
            foreach (var (label, n) in new (string, int)[]
                     {
                         ("nothing listed at all", _foundNothing),
                         ("could not afford anything listed", _couldNotAfford),
                         ("everything above what they would pay", _allTooDear),
                         ("bought something", _bought),
                     })
                sb.AppendLine($"    {label,-38} {n,5} ({Pct(n, _browsed)})");
            foreach (var g in Traders.GroupBy(t => t.Shopper).OrderBy(g => g.Key))
                sb.AppendLine($"    {$"{g.Key} ({g.Count()} traders)",-38} {g.Sum(t => t.Bought),5} purchases "
                              + $"in {g.Sum(t => t.Browsed)} browses ({Pct(g.Sum(t => t.Bought), g.Sum(t => t.Browsed))} of the time)");
            sb.AppendLine($"  supply vs demand: {_listings.Count} listings created against {_browsed} browses "
                          + $"({(_browsed == 0 ? "n/a" : $"{_listings.Count / (double)_browsed:0.00}")} listings per would-be buyer)");
        }

        private void AppendServerView(StringBuilder sb, EconomyHealthDto health,
            List<OfferDto> resting, List<OfferDto> soldStrip)
        {
            var pulledIds = _listings.Where(l => l.PulledRound is not null)
                .Select(l => l.OfferId).ToHashSet(StringComparer.Ordinal);
            var pulledInStrip = soldStrip.Count(o => pulledIds.Contains(o.OfferId));
            sb.AppendLine();
            sb.AppendLine("THE SERVER'S OWN MARKET VIEW");
            sb.AppendLine($"  GET /api/offers            {resting.Count} resting  (harness counted {_listings.Count(l => l.Resting)})");
            sb.AppendLine($"  economy health             active {health.ActiveOfferCount}   closed {health.ClosedOfferCount}   "
                          + $"closed-with-fee-unbooked {health.UnbookedClosedFeeOffers}");
            sb.AppendLine($"  GET /api/offers/sold?24    {soldStrip.Count} rows, of which {pulledInStrip} were PULLED by the seller, not sold");
        }

        private static string Pct(long n, long total) => total == 0 ? "n/a" : $"{100.0 * n / total:F0}%";

        private static double Median(double[] values)
        {
            if (values.Length == 0) return 0;
            Array.Sort(values);
            var mid = values.Length / 2;
            return values.Length % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2.0;
        }
    }
}
