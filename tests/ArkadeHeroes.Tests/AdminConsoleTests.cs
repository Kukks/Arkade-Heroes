using System.Net;
using ArkadeHeroes.Chain;
using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Heroes;
using ArkadeHeroes.Server;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ArkadeHeroes.Tests;

/// <summary>
/// The operator console: one authenticated surface over what the server already knows, plus three
/// management actions that already existed.
///
/// The gate is what these tests are mostly about. This server holds real bitcoin, so the admin surface is
/// the highest-value target in it, and the failure that matters is not "the token check is wrong" — it is
/// "there is no token check, because nobody configured one". Hence the first two tests: with
/// <c>Game:AdminToken</c> unset the routes must not EXIST, and with it set every route must refuse a
/// missing or wrong token.
/// </summary>
public class AdminConsoleTests
{
    private const string Token = "s3cret-operator-token";

    /// <summary>A factory with the console switched on. Nothing else about the server changes.</summary>
    static WebApplicationFactory<Program> Enabled() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("Game:AdminToken", Token))
            // The overview asserts an exact treasury total; a paid starter claim would add to it.
            .WithFreeStarters();

    /// <summary>A raw request to an admin route, optionally carrying a token — the SDK always sends one,
    /// and "sends none at all" is exactly the case that has to be covered.</summary>
    static async Task<HttpResponseMessage> RawAsync(
        WebApplicationFactory<Program> factory, HttpMethod method, string path, string? token)
    {
        using var request = new HttpRequestMessage(method, path);
        if (token is not null) request.Headers.Add(AdminApiContract.TokenHeader, token);
        return await factory.CreateClient().SendAsync(request);
    }

    // ── The gate ───────────────────────────────────────────────────────────

    /// <summary>
    /// UNSET MEANS OFF, NOT OPEN. The default server configures no admin token, and in that state the admin
    /// group is never mapped — so every route under it 404s because it genuinely does not exist, rather
    /// than answering with an unguarded 200. This is the single most important property of the whole
    /// feature: a deployment that forgets to configure the console must get no console, not a public one.
    /// </summary>
    [Fact]
    public async Task AdminSurface_DoesNotExist_WhenNoTokenIsConfigured()
    {
        using var factory = new WebApplicationFactory<Program>();   // the default — no Game:AdminToken

        foreach (var (method, path) in new (HttpMethod, string)[]
                 {
                     (HttpMethod.Get, "/api/admin/overview"),
                     (HttpMethod.Post, "/api/admin/actions/reconcile-matches"),
                     (HttpMethod.Post, "/api/admin/actions/settle-seasons"),
                     (HttpMethod.Post, "/api/admin/tournaments/tourney-1/refund"),
                 })
        {
            // No token supplied, and — the part that matters — no token that COULD be supplied.
            var anonymous = await RawAsync(factory, method, path, token: null);
            Assert.Equal(HttpStatusCode.NotFound, anonymous.StatusCode);

            // And a caller who guesses a token gets the same nothing: there is no route to authenticate to.
            var guessed = await RawAsync(factory, method, path, token: "any-guess-at-all");
            Assert.Equal(HttpStatusCode.NotFound, guessed.StatusCode);
        }
    }

    /// <summary>Whitespace is an accident, not a password. A token of blanks must leave the surface OFF —
    /// treating it as configured is the one reading that fails open.</summary>
    [Fact]
    public async Task AdminSurface_StaysOff_WhenTheConfiguredTokenIsOnlyWhitespace()
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseSetting("Game:AdminToken", "   "));

        var blank = await RawAsync(factory, HttpMethod.Get, "/api/admin/overview", "   ");
        Assert.Equal(HttpStatusCode.NotFound, blank.StatusCode);
    }

    /// <summary>Every route in the group is gated, not just the read — the check is a group-wide filter, so
    /// a route added later cannot be born unauthenticated. Both a MISSING and a WRONG token are refused.</summary>
    [Theory]
    [InlineData("GET", "/api/admin/overview")]
    [InlineData("POST", "/api/admin/actions/reconcile-matches")]
    [InlineData("POST", "/api/admin/actions/settle-seasons")]
    [InlineData("POST", "/api/admin/tournaments/tourney-1/refund")]
    public async Task EveryAdminRoute_Refuses_AMissingOrWrongToken(string verb, string path)
    {
        using var factory = Enabled();
        var method = new HttpMethod(verb);

        var missing = await RawAsync(factory, method, path, token: null);
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);

        var wrong = await RawAsync(factory, method, path, token: "not-the-token");
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        // A near-miss is still a miss: no prefix, casing or length leniency anywhere.
        var nearMiss = await RawAsync(factory, method, path, token: Token[..^1]);
        Assert.Equal(HttpStatusCode.Unauthorized, nearMiss.StatusCode);

        var wrongCase = await RawAsync(factory, method, path, token: Token.ToUpperInvariant());
        Assert.Equal(HttpStatusCode.Unauthorized, wrongCase.StatusCode);
    }

    /// <summary>The secret belongs in a header. A query string lands in browser history, proxy logs and
    /// Referer headers — so passing it that way must NOT authenticate.</summary>
    [Fact]
    public async Task AdminRead_IsNotAuthenticated_ByAnUrlQueryString()
    {
        using var factory = Enabled();
        var viaUrl = await factory.CreateClient().GetAsync($"/api/admin/overview?token={Token}");
        Assert.Equal(HttpStatusCode.Unauthorized, viaUrl.StatusCode);
    }

    /// <summary>
    /// The browser's CORS preflight must get through the gate. A custom request header makes every admin
    /// call in dev — where the WASM frontend is a separate origin from the API — non-simple, so the browser
    /// sends an OPTIONS preflight FIRST. That preflight carries no token (the spec forbids it), so if the
    /// gate ever answered it the console would simply never load and the only symptom would be a CORS error
    /// with no server-side trace. The preflight is not an admin request and must not be treated as one.
    /// </summary>
    [Fact]
    public async Task AdminRoute_AnswersTheBrowsersPreflight_ForTheTokenHeader()
    {
        using var factory = Enabled();
        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/admin/overview");
        preflight.Headers.Add("Origin", "http://localhost:5132");   // the dev frontend's own origin
        preflight.Headers.Add("Access-Control-Request-Method", "GET");
        preflight.Headers.Add("Access-Control-Request-Headers", AdminApiContract.TokenHeader);

        var response = await factory.CreateClient().SendAsync(preflight);

        Assert.True(response.IsSuccessStatusCode,
            $"the preflight was refused with {(int)response.StatusCode} — the console cannot load cross-origin");
        var allowed = response.Headers.TryGetValues("Access-Control-Allow-Headers", out var values)
            ? string.Join(",", values)
            : "";
        Assert.True(
            allowed.Contains('*') || allowed.Contains(AdminApiContract.TokenHeader, StringComparison.OrdinalIgnoreCase),
            $"the preflight did not allow {AdminApiContract.TokenHeader}; it answered '{allowed}'");
    }

    /// <summary>A player's own bearer token is not an operator credential, and the two must not be
    /// interchangeable — a signed-in player is still refused the console.</summary>
    [Fact]
    public async Task ASignedInPlayer_IsNotAnOperator()
    {
        using var factory = Enabled();
        var (_, player) = await factory.RegisterAsync("Admin-NotMe");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/overview");
        request.Headers.Add("Authorization", $"Bearer {player.Token}");
        var response = await factory.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>The constant-time compare, at the unit level: the hash-then-FixedTimeEquals shape has to
    /// still accept the right token and reject everything else, including an unset secret.</summary>
    [Fact]
    public void AdminGate_MatchesOnlyTheConfiguredToken()
    {
        Assert.True(AdminGate.Matches(Token, Token));
        Assert.False(AdminGate.Matches(Token, null));
        Assert.False(AdminGate.Matches(Token, ""));
        Assert.False(AdminGate.Matches(Token, Token + "x"));

        // An unset secret matches NOTHING — not an empty guess, not a whitespace one.
        Assert.False(AdminGate.Matches(null, ""));
        Assert.False(AdminGate.Matches("", ""));
        Assert.False(AdminGate.Matches("   ", "   "));
        Assert.False(AdminGate.IsEnabled(null));
        Assert.False(AdminGate.IsEnabled("   "));
        Assert.True(AdminGate.IsEnabled(Token));
    }

    // ── The analytics read ─────────────────────────────────────────────────

    /// <summary>The read composes what the server already knows: the economy picture, the population, the
    /// supply cut by generation and rarity, the market, the flow backlogs and the season.</summary>
    [Fact]
    public async Task Overview_ComposesTheServersOwnState()
    {
        using var factory = Enabled();
        var chain = (InMemoryChainService)factory.Services.GetRequiredService<IChainService>();
        chain.FundTreasury(50_000);

        var (player, playerDto) = await factory.RegisterAsync("Admin-Overview");
        await player.ClaimStartersAsync();   // two gen-0 heroes

        var store = factory.Services.GetRequiredService<GameStore>();
        store.Heroes["bred-1"] = new Hero
        {
            Id = "bred-1", OwnerId = playerDto.PlayerId, Name = "Child", Level = 3,
            Genome = new Genome(new byte[32]), Generation = 2,
        };

        var admin = new ArkadeHeroesClient(factory.CreateClient());
        var overview = await admin.Admin.OverviewAsync(Token);

        // The economy picture rides along whole, tripwires included.
        Assert.Equal(50_000, overview.Economy.TreasuryBalanceSats);
        Assert.Equal(3, overview.Economy.HeroSupply);

        // Population: one registered player, who owns heroes.
        Assert.Equal(1, overview.Players.Registered);
        Assert.Equal(1, overview.Players.WithHeroes);

        // Supply by generation — two gen-0 starters and the gen-2 hero above.
        Assert.Equal(2, overview.HeroesByGeneration.Single(b => b.Key == "0").Count);
        Assert.Equal(1, overview.HeroesByGeneration.Single(b => b.Key == "2").Count);
        // Every hero lands in exactly one rarity bucket, so the tiers sum back to the supply.
        Assert.Equal(overview.Economy.HeroSupply, overview.HeroesByRarity.Sum(b => b.Count));

        // Flows are all present and empty on a fresh server.
        Assert.All(overview.Flows, f => Assert.Equal(0, f.Total));
        Assert.Contains(overview.Flows, f => f.Flow == "tournament");
        Assert.Contains(overview.Flows, f => f.Flow == "death-match");

        Assert.True(overview.Season.SeasonNumber > 0);
        Assert.True(overview.GeneratedAtUnix > 0);
    }

    /// <summary>Market state is composed from the offer book the server already holds, and pending offers
    /// count toward neither resting nor cleared.</summary>
    [Fact]
    public async Task Overview_ReportsMarketState_FromTheOfferBook()
    {
        using var factory = Enabled();
        await factory.RegisterAsync("Admin-Market");
        var store = factory.Services.GetRequiredService<GameStore>();

        static OfferListing Offer(string id, string status, long ask) => new()
        {
            Id = id, SellerId = "s", ItemId = "rusty-blade", AskSats = ask,
            OfferAddress = $"addr-{id}", ItemAssetId = $"asset-{id}", OfferValueSats = ask,
            RefundAfterUnixSeconds = 0, Status = status,
        };
        store.Offers["a1"] = Offer("a1", "active", 1_000);
        store.Offers["a2"] = Offer("a2", "active", 2_500);
        store.Offers["c1"] = Offer("c1", "closed", 900);
        store.Offers["p1"] = Offer("p1", "pending", 700);

        var admin = new ArkadeHeroesClient(factory.CreateClient());
        var market = (await admin.Admin.OverviewAsync(Token)).Market;

        Assert.Equal(2, market.ActiveOffers);
        Assert.Equal(1, market.ClosedOffers);
        Assert.Equal(1, market.PendingOffers);
        Assert.Equal(3_500, market.RestingAskSats);   // the two resting asks, not the closed or pending one
    }

    /// <summary>
    /// The read is OBSERVATION. Opening the console must not move a sat, and the one place that could is the
    /// season: the player-facing board settles due seasons as a side effect of being read. The admin read
    /// projects the same window WITHOUT that settle, so the treasury balance is untouched by the read and
    /// the settled marker does not advance.
    /// </summary>
    [Fact]
    public async Task Overview_NeverSettlesOrPays()
    {
        using var factory = Enabled();
        var chain = (InMemoryChainService)factory.Services.GetRequiredService<IChainService>();
        chain.FundTreasury(100_000);
        var store = factory.Services.GetRequiredService<GameStore>();

        var (player, _) = await factory.RegisterAsync("Admin-NoSettle");
        await player.ClaimStartersAsync();

        // Put a season back in the DUE state: DueSeasons runs (lastSettled, currentSeason), so a marker two
        // behind the live season leaves exactly one ended-but-unsettled season for a board read to pick up.
        var live = (await player.Leaderboard.SeasonAsync()).SeasonNumber;
        Assert.True(live >= 3, "the season epoch is in the past, so prior seasons exist to settle");
        store.LastSettledSeason = live - 2;

        var balanceBefore = await chain.TreasuryBalanceAsync(CancellationToken.None);
        var outflowBefore = store.TreasuryOutflowByTag.GetValueOrDefault("season");

        var admin = new ArkadeHeroesClient(factory.CreateClient());
        var overview = await admin.Admin.OverviewAsync(Token);

        // The marker is the settle's own footprint: it did not move, so no settle ran.
        Assert.Equal(live - 2, store.LastSettledSeason);
        Assert.Equal(balanceBefore, await chain.TreasuryBalanceAsync(CancellationToken.None));
        Assert.Equal(outflowBefore, store.TreasuryOutflowByTag.GetValueOrDefault("season"));
        Assert.Equal(live, overview.Season.SeasonNumber);  // but it still reports the live season

        // And the contrast that proves the read is doing something DIFFERENT, not that nothing was due:
        // the player-facing board settles the very season the admin read left alone.
        await player.Leaderboard.SeasonAsync();
        Assert.Equal(live - 1, store.LastSettledSeason);
    }

    /// <summary>The read must not reconcile offers either: reconciling BOOKS listing income into the
    /// treasury ledger, and an analytics read that writes to the money ledger is not an analytics read.
    /// A sold offer therefore still reads by its last-observed status, and nothing is booked.</summary>
    [Fact]
    public async Task Overview_NeverBooksTreasuryInflow()
    {
        using var factory = Enabled();
        var (seller, _) = await factory.RegisterAsync("Admin-NoBook-S");
        var (buyer, _) = await factory.RegisterAsync("Admin-NoBook-B");
        await seller.BuyItemAsync("rusty-blade");
        var offer = await seller.Offers.CreateItemAsync(new CreateOfferRequest("rusty-blade", 5_000));
        await seller.Dev.FundOfferAsync(new { OfferId = offer.OfferId });
        await buyer.Dev.FulfillOfferAsync(new { OfferId = offer.OfferId });   // sold, but not yet observed

        var store = factory.Services.GetRequiredService<GameStore>();
        var bookedBefore = store.TreasuryInflowByTag.GetValueOrDefault("listing");

        var admin = new ArkadeHeroesClient(factory.CreateClient());
        await admin.Admin.OverviewAsync(Token);
        await admin.Admin.OverviewAsync(Token);   // and again — still no booking

        Assert.Equal(bookedBefore, store.TreasuryInflowByTag.GetValueOrDefault("listing"));
    }

    // ── The management actions ─────────────────────────────────────────────

    /// <summary>The strand refund, driven from the console: a bracket that can never resolve pays its
    /// cleared buy-ins back. The refund itself is the same already-tested service call the player-facing
    /// endpoint makes — what is under test here is that the operator route reaches it.</summary>
    [Fact]
    public async Task RefundTournament_RefundsAStrandedBracket()
    {
        using var factory = Enabled();
        var chain = (InMemoryChainService)factory.Services.GetRequiredService<IChainService>();
        chain.FundTreasury(100_000);

        var (alice, _) = await factory.RegisterAsync("Admin-Refund-A");
        var (bob, _) = await factory.RegisterAsync("Admin-Refund-B");
        var aliceHero = (await alice.ClaimStartersAsync())[0].Id;
        var bobHero = (await bob.ClaimStartersAsync())[0].Id;

        var open = await alice.Tournament.OpenAsync(new OpenTournamentRequest(aliceHero, 2_000, 2));
        await alice.Dev.PayInvoiceAsync(new { InvoiceId = open.BuyIn.InvoiceId });
        var join = await bob.Tournament.JoinAsync(open.Tournament.Id, new JoinTournamentRequest(bobHero));
        await bob.Dev.PayInvoiceAsync(new { InvoiceId = join.BuyIn.InvoiceId });

        // Strand it exactly as a restart does: a FULL bracket whose fill-time snapshots are gone.
        var store = factory.Services.GetRequiredService<GameStore>();
        var session = store.Tournaments[open.Tournament.Id];
        Assert.Equal("full", session.Status);
        session.EntrantSnapshots = null;

        var admin = new ArkadeHeroesClient(factory.CreateClient());
        var refund = await admin.Admin.RefundTournamentAsync(Token, session.Id);

        Assert.Equal(2, refund.EntrantsRefunded);
        Assert.Equal(4_000, refund.RefundedSats);
        Assert.Equal("refunded", refund.Tournament.Status);
    }

    /// <summary>The refund is not a way to unwind a LIVE pot — the service's own gate refuses a bracket that
    /// can still be played, and the operator route does not get to bypass it.</summary>
    [Fact]
    public async Task RefundTournament_RefusesAResolvableBracket()
    {
        using var factory = Enabled();
        var chain = (InMemoryChainService)factory.Services.GetRequiredService<IChainService>();
        chain.FundTreasury(100_000);

        var (alice, _) = await factory.RegisterAsync("Admin-Live-A");
        var (bob, _) = await factory.RegisterAsync("Admin-Live-B");
        var aliceHero = (await alice.ClaimStartersAsync())[0].Id;
        var bobHero = (await bob.ClaimStartersAsync())[0].Id;

        var open = await alice.Tournament.OpenAsync(new OpenTournamentRequest(aliceHero, 2_000, 2));
        await alice.Dev.PayInvoiceAsync(new { InvoiceId = open.BuyIn.InvoiceId });
        var join = await bob.Tournament.JoinAsync(open.Tournament.Id, new JoinTournamentRequest(bobHero));
        await bob.Dev.PayInvoiceAsync(new { InvoiceId = join.BuyIn.InvoiceId });

        var admin = new ArkadeHeroesClient(factory.CreateClient());
        var refused = await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => admin.Admin.RefundTournamentAsync(Token, open.Tournament.Id));
        Assert.Contains("can still be resolved", refused.Message);

        // And the overview surfaces the fact the gate reads, so the operator can see WHY.
        var listed = (await admin.Admin.OverviewAsync(Token)).Tournaments
            .Single(t => t.Id == open.Tournament.Id);
        Assert.True(listed.HasEntrantSnapshots);
        Assert.Equal("full", listed.Status);
    }

    /// <summary>Expiring abandoned covenant matches moves NO money — it flips a status so each player's own
    /// wallet can reclaim its stake — and running it twice changes nothing the second time.</summary>
    [Fact]
    public async Task ReconcileMatches_ExpiresAnAbandonedMatch_AndIsIdempotent()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Game:AdminToken", Token);
            b.UseSetting("Game:WagerEscrowRefundAfter", "00:00:00");   // the window is already past
        });
        var chain = (InMemoryChainService)factory.Services.GetRequiredService<IChainService>();
        chain.FundTreasury(100_000);

        var (alice, _) = await factory.RegisterAsync("Admin-Recon-A");
        var (bob, _) = await factory.RegisterAsync("Admin-Recon-B");
        var aliceHero = (await alice.ClaimStartersAsync())[0].Id;
        var bobHero = (await bob.ClaimStartersAsync())[0].Id;

        // Opened in covenant mode and never staked: past the refund window, that is abandoned.
        await alice.Matches.OpenAsync(new OpenMatchRequest(aliceHero, bobHero, 1_000, "covenant"));

        var store = factory.Services.GetRequiredService<GameStore>();
        var balanceBefore = await chain.TreasuryBalanceAsync(CancellationToken.None);

        var admin = new ArkadeHeroesClient(factory.CreateClient());
        var first = await admin.Admin.ReconcileMatchesAsync(Token);

        Assert.Equal("reconcile-matches", first.Action);
        Assert.Equal(1, store.Matches.Values.Count(m => m.Status == "expired"));
        Assert.Equal(balanceBefore, await chain.TreasuryBalanceAsync(CancellationToken.None));   // no money moved

        var second = await admin.Admin.ReconcileMatchesAsync(Token);
        Assert.Contains("1 of 1 now expired", second.Detail);
        Assert.Equal(1, store.Matches.Values.Count(m => m.Status == "expired"));
    }

    /// <summary>Forcing the season settle is the same idempotent call a public season-board read already
    /// makes: it clears what is DUE, advances the settled marker, and a second run finds nothing left.</summary>
    [Fact]
    public async Task SettleSeasons_ClearsWhatIsDue_AndIsIdempotent()
    {
        using var factory = Enabled();
        var chain = (InMemoryChainService)factory.Services.GetRequiredService<IChainService>();
        chain.FundTreasury(500_000);
        var store = factory.Services.GetRequiredService<GameStore>();

        var (player, _) = await factory.RegisterAsync("Admin-Settle");
        var live = (await player.Leaderboard.SeasonAsync()).SeasonNumber;
        Assert.True(live >= 3, "the season epoch is in the past, so prior seasons exist to settle");
        store.LastSettledSeason = live - 2;   // season live-1 has ended and is unsettled

        var admin = new ArkadeHeroesClient(factory.CreateClient());
        var first = await admin.Admin.SettleSeasonsAsync(Token);

        Assert.Equal("settle-seasons", first.Action);
        Assert.Equal(live - 1, store.LastSettledSeason);   // the marker caught up
        Assert.Contains("marker advanced", first.Detail);

        var second = await admin.Admin.SettleSeasonsAsync(Token);
        Assert.Contains("Nothing was due", second.Detail);
        Assert.Equal(live - 1, store.LastSettledSeason);
    }
}
