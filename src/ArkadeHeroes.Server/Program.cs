using ArkadeHeroes.Chain;
using ArkadeHeroes.Chain.NArk;
using ArkadeHeroes.Core.Equipment;
using ArkadeHeroes.Server;
using ArkadeHeroes.Server.Persistence;
using ArkadeHeroes.Shared;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<GameOptions>(builder.Configuration.GetSection(GameOptions.SectionName));
builder.Services.AddSingleton<GameStore>();
builder.Services.AddSingleton<ReceiptSigner>();
builder.Services.AddSingleton<GameService>();

// CORS for the Blazor WASM frontend. In dev the WASM runs on its own origin
// (http://localhost:5132) and calls this API cross-origin; allow any localhost
// port over http/https. Auth is a bearer header (no cookies), so no credentials
// are needed. Serving the WASM from this host (same-origin) needs none of this.
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.SetIsOriginAllowed(origin =>
            Uri.TryCreate(origin, UriKind.Absolute, out var u) && u.Host is "localhost" or "127.0.0.1")
        .AllowAnyHeader()
        .AllowAnyMethod()));

// Chain mode: "InMemory" (default) or "NArk" (regtest denigiri stack).
var chainMode = builder.Configuration.GetValue<string>("Chain:Mode") ?? "InMemory";
if (chainMode.Equals("NArk", StringComparison.OrdinalIgnoreCase))
{
    var nArkOptions = new ArkadeHeroes.Chain.NArk.NArkChainOptions();
    builder.Configuration.GetSection(ArkadeHeroes.Chain.NArk.NArkChainOptions.SectionName).Bind(nArkOptions);
    builder.Services.AddNArkChain(nArkOptions);
}
else
{
    builder.Services.AddSingleton<IChainService, InMemoryChainService>();
}

// Durability seam (OPT-IN). With no Game:StateDbPath configured the null implementation is registered and
// the server behaves exactly as it always has — all state in memory, gone on restart. With a path set, the
// money-bearing state (a paid-but-unclaimed item purchase) and the hero roster survive a bounce.
var stateDbPath = builder.Configuration["Game:StateDbPath"];
if (!string.IsNullOrWhiteSpace(stateDbPath))
{
    builder.Services.AddDbContextFactory<GameStateDbContext>(o => o.UseSqlite($"Data Source={stateDbPath}"));
    builder.Services.AddSingleton<IGameStatePersistence, SqliteGameStatePersistence>();
    // The append-only audit log — every state-changing action, in the SAME database file, so it shares one
    // migration pipeline and one backup with the state it describes. A SINGLETON because its write-failure
    // counter has to accumulate across the process, which is the only way a log that has gone deaf surfaces
    // as a number rather than as a warning nobody greps.
    builder.Services.AddSingleton<IAuditLog, SqliteAuditLog>();
    // Hero-progression flush: identity events save inline, grinding rides this loop. Registered as a
    // resolvable singleton too so a test can force a deterministic flush instead of racing the timer.
    builder.Services.AddSingleton<HeroFlushService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<HeroFlushService>());
}
else
{
    builder.Services.AddSingleton<IGameStatePersistence, NullGameStatePersistence>();
    // No database to append to: the audit log follows the same opt-in seam as the rest of durability
    // (Game:StateDbPath), because a log that lives only in this process's memory records nothing a restart
    // could ever be asked about.
    builder.Services.AddSingleton<IAuditLog, NullAuditLog>();
}

var app = builder.Build();

// Re-validate the authored content against the economy THIS server actually runs, before it serves a
// single request.
//
// ContentPackLoader already validated the pack against GameConfig.Default when it loaded, but the treasury
// rule — entry must cost more than the best item a dungeon can drop — is priced off the MATCH FEE, and an
// operator can retune that downward through Game:* configuration. Content that is a safe sats sink under
// the default economy can be a bitcoin faucet under theirs, and sats are real money the treasury cannot
// print. So the rule is asked again here, against the live config, and a failure stops the process rather
// than quietly running a leaking dungeon.
{
    var liveConfig = app.Services
        .GetRequiredService<Microsoft.Extensions.Options.IOptions<GameOptions>>().Value.ToGameConfig();
    var contentErrors = ArkadeHeroes.Core.Content.ContentValidation.Validate(
        ArkadeHeroes.Core.Content.ContentPack.Default, liveConfig);
    if (contentErrors.Count > 0)
        throw new ArkadeHeroes.Core.Content.ContentValidationException(contentErrors);
}

// Rehydrate before serving: a purchase paid before the restart must be claimable after it.
if (!string.IsNullOrWhiteSpace(stateDbPath))
{
    // Apply migrations, not EnsureCreated: this creates the DB + every table on a fresh path AND evolves an
    // already-created durable DB (new tables/columns) — EnsureCreated is create-once and silently skips
    // schema changes on an existing file, so a shipped entity (e.g. Heroes) would be missing in production.
    await app.Services.GetRequiredService<IDbContextFactory<GameStateDbContext>>()
        .CreateDbContext().Database.MigrateAsync();
    var rehydrated = app.Services.GetRequiredService<GameStore>();
    await app.Services.GetRequiredService<IGameStatePersistence>().LoadIntoAsync(rehydrated);
    app.Logger.LogInformation(
        "State durability ENABLED at {StateDbPath}: rehydrated {Heroes} heroes, {Players} players, {Offers} offers.",
        stateDbPath, rehydrated.Heroes.Count, rehydrated.Players.Count, rehydrated.Offers.Count);
}
else
{
    // Said at boot, loudly, because the alternative is finding out from a player. Durability is opt-in, and
    // an operator who never sets the key gets a server that looks entirely healthy — it boots, it serves, it
    // passes the healthcheck — right up until the first restart takes the whole roster with it. The heroes
    // are on-chain and survive; it is the game's only record of WHOSE they are that does not, which makes
    // them permanently invisible rather than merely misplaced. Same shape as the admin-token notice below:
    // name the key, say what turns it on.
    app.Logger.LogWarning(
        "State durability DISABLED: no Game:StateDbPath configured, so heroes, offers and stud proposals "
        + "live only in this process's memory and a restart destroys them — the on-chain assets survive but "
        + "the game can no longer see who owns them. Set Game__StateDbPath to a file on a PERSISTENT volume "
        + "(the container defaults it to /data/game.db) to keep them.");
}

app.UseCors();

// ── Serve the Blazor WASM bundle from this host ───────────────────────────────
// The published frontend is copied into wwwroot at image build time, so ONE container is
// the entire deployment. That also makes the browser's origin equal to the API's, which is
// why the localhost-only CORS policy above is never consulted rather than needing to be
// widened for a public domain — a same-origin request does not carry an Origin header at
// all. Running the API alone (no wwwroot, e.g. every test host) is unaffected: with no
// files to serve these are no-ops, not errors.
app.UseBlazorFrameworkFiles();
// Cache policy is load-bearing here, not tidying. index.html asks for the runtime as
// `blazor.webassembly#[.{fingerprint}].js`, so a new build gives the framework a NEW url and the
// browser fetches it. Our own assets — css/app.css, js/hero-render.js — keep the SAME url forever.
// With no Cache-Control at all the browser is free to reuse them heuristically, which is exactly
// what it did: a deploy shipped new markup against a MONTH-old stylesheet, and the page rendered
// as unstyled text. `no-cache` still lets the browser store the file; it just has to revalidate,
// so the steady state is a 304 and the deploy state is the new bytes.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        // Our own assets keep the same url across every deploy, so the browser must be told to
        // revalidate them. `no-cache` still permits storing the file — it just forbids reusing it
        // without asking — so the steady state is a 304 and a deploy is picked up immediately.
        //
        // Nothing here needs to exclude _framework. UseBlazorFrameworkFiles serves those itself and
        // never reaches this callback (verified: a probe header set unconditionally here is absent
        // from a /_framework response). It sets its own no-cache, which is correct — blazor.boot.json
        // has a fixed url and must be revalidated to find a new build at all.
        ctx.Context.Response.Headers.CacheControl = "no-cache";
    },
});

// Deep links (/heroes/abc, /market, …) are client-side routes with no file behind them, so
// the browser must still receive index.html and let the WASM router resolve them.
//
// This is a MIDDLEWARE that rewrites an existing 404, NOT MapFallbackToFile. Both routing
// variants were tried and both changed the API's behaviour, because a catch-all endpoint
// participates in matching for every request:
//   • plain MapFallbackToFile turned a POST to an unregistered admin route from 404 into
//     405 — the path now matched a GET-only endpoint (AdminConsoleTests.AdminSurface_
//     DoesNotExist_WhenNoTokenIsConfigured), and 405 also tells a prober the path is real;
//   • an added MapFallback("/api/{**rest}") turned a bodyless POST to /api/players/me/terms
//     from 400 into 404 (TermsAcceptanceTests.AnAcceptanceWithNoVersionAtAll…).
// Acting only on a response that is ALREADY 404 adds no route, so endpoint matching — and
// therefore every status code the API returns — is exactly what it was before.
//
// Deliberately narrow: GET/HEAD only (a POST to a missing page is not a deep link), never
// under /api (an API 404 must stay a JSON-shaped 404, not a page that parses as garbage),
// and only when a bundle is actually present, so an API-only host is untouched.
var indexHtml = Path.Combine(app.Environment.WebRootPath ?? string.Empty, "index.html");
if (File.Exists(indexHtml))
{
    app.Use(async (context, next) =>
    {
        await next();
        if (context.Response.StatusCode == StatusCodes.Status404NotFound
            && !context.Response.HasStarted
            && !context.Request.Path.StartsWithSegments("/api")
            && (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)))
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.SendFileAsync(indexHtml);
        }
    });
}

// GameRuleException → 400; anything else → 500, both with readable JSON.
app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (GameRuleException ex)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new ErrorResponse(ex.Message));
    }
    catch (Exception ex) when (!context.Response.HasStarted)
    {
        app.Logger.LogError(ex, "Unhandled error on {Path}", context.Request.Path);
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsJsonAsync(new ErrorResponse($"{ex.GetType().Name}: {ex.Message}"));
    }
});

var api = app.MapGroup("/api");

// ── Players ────────────────────────────────────────────────────────────────

api.MapPost("/players", async (RegisterPlayerRequest request, GameService game, CancellationToken ct) =>
{
    var (player, address, balance) = await game.RegisterPlayerAsync(
        request.Name, request.ArkadeAddress, request.LoginPubKeyHex, request.NonceHex, request.SignatureHex, ct,
        request.AcceptedTermsVersion);
    return Results.Ok(new PlayerDto(player.Id, player.Name, address, balance,
        player.StarterClaimed, player.Token, player.TermsAcceptedVersion, player.TermsAcceptedAtUtc));
});

// "Sign in with your wallet" — resume an existing player after a restore: fetch a
// single-use challenge, sign its digest with the wallet's login key, prove it here.
api.MapGet("/players/login-challenge", (GameService game) =>
    Results.Ok(new LoginChallengeResponse(game.IssueLoginChallenge())));

api.MapPost("/players/login", async (LoginRequest request, GameService game, IChainService chain, IAuditLog audit, CancellationToken ct) =>
{
    var player = game.Login(request.LoginPubKeyHex, request.NonceHex, request.SignatureHex);
    // Logged HERE rather than inside GameService.Login because that method is synchronous by design (it
    // touches no I/O) and making it async to fit the log would ripple a signature change through the
    // service for no gain. A REFUSED login is not recorded: Login throws, so this line is never reached —
    // the log holds successful sign-ins, and a failed one is an authentication concern the request log
    // already carries.
    //
    // No signature, nonce, token OR pubkey is recorded. The key is on the player row already, and the
    // actor id resolves to it; writing it into a table the database refuses to update or delete would put
    // a wallet identifier permanently beyond correction for no fact the actor id does not already give.
    await audit.RecordAsync(new AuditEntry(AuditEventType.PlayerLoggedIn, player.Id, [player.Id],
        new { resumed = true }));
    var address = await chain.GetPlayerAddressAsync(player.Id, ct);
    var balance = await chain.GetAddressBalanceSatsAsync(player.Id, ct);
    return Results.Ok(new PlayerDto(player.Id, player.Name, address, balance, player.StarterClaimed, player.Token,
        player.TermsAcceptedVersion, player.TermsAcceptedAtUtc));
});

api.MapGet("/players/me", async (HttpContext http, GameService game, IChainService chain, CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    var address = await chain.GetPlayerAddressAsync(player.Id, ct);
    var balance = await chain.GetAddressBalanceSatsAsync(player.Id, ct);
    return Results.Ok(new PlayerDto(player.Id, player.Name, address, balance, player.StarterClaimed, null,
        player.TermsAcceptedVersion, player.TermsAcceptedAtUtc));
});

// ── Terms of Use ───────────────────────────────────────────────────────────
// The acceptance is the player's deliberate act, recorded HERE — not in their browser, which is one cache
// clear from gone and is their own machine anyway. What is stored is the VERSION they accepted plus a UTC
// timestamp, so a later change to docs/TERMS.md (a bump of Terms.CurrentVersion) re-asks the question.

api.MapGet("/players/me/terms", (HttpContext http, GameService game) =>
    Results.Ok(game.TermsFor(game.Authenticate(BearerToken(http)))));

// A nullable body so an EMPTY post is a readable 400 from our own rules rather than a binding failure —
// "missing version" must be refused as loudly as a malformed one, never recorded as a zero.
api.MapPost("/players/me/terms", async (AcceptTermsRequest? request, HttpContext http, GameService game, CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    if (request is null) throw new GameRuleException("A Terms of Use version is required to record an acceptance.");
    await game.AcceptTermsAsync(player, request.Version, ct);
    return Results.Ok(game.TermsFor(player));
});

// This player's covenant escrows that may still hold their assets with no path forward — what the
// recovery UI lists. DISCOVERY ONLY: the reclaim itself is a covenant spend from the player's own
// wallet against the public escrow params, so it never needs this endpoint's agreement.
api.MapGet("/players/me/reclaimable", async (HttpContext http, GameService game, CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    return Results.Ok(await game.ListReclaimableAsync(player, ct));
});

// Public profile: players are addresses, and addresses are public — this is
// what a sender needs to transfer a hero to another player wallet-to-wallet.
// A player's derived accomplishments + unlocked badges (from their roster + tournament wins).
// The season pass: a season-long goal scored from the player's own receipts. Titles only — never sats.
api.MapGet("/players/me/season-pass", (HttpContext http, GameService game) =>
    Results.Ok(game.SeasonPassFor(game.Authenticate(BearerToken(http)))));

api.MapGet("/players/me/achievements", (HttpContext http, GameService game) =>
    Results.Ok(game.PlayerAchievements(game.Authenticate(BearerToken(http)))));

// Economy control plane: treasury-health telemetry (public, read-only) — balance, outflow by category, season accrual.
api.MapGet("/economy/health", async (GameService game, CancellationToken ct) =>
    Results.Ok(await game.EconomyHealthAsync(ct)));

// A player's public trophy case — the same standing /players/me/* shows them, addressable by anyone, so a
// name on the leaderboard leads somewhere. Public and unauthenticated: every field is bragging material.
api.MapGet("/players/{playerId}/profile", (string playerId, GameStore store, GameService game) =>
    store.Players.TryGetValue(playerId, out var player)
        ? Results.Ok(game.ProfileFor(player))
        : Results.NotFound());

api.MapGet("/players/{playerId}", async (string playerId, GameStore store, IChainService chain, CancellationToken ct) =>
{
    if (!store.Players.TryGetValue(playerId, out var player)) return Results.NotFound();
    var address = await chain.GetPlayerAddressAsync(player.Id, ct);
    return Results.Ok(new PlayerDto(player.Id, player.Name, address, 0, player.StarterClaimed));
});

// ── Heroes ─────────────────────────────────────────────────────────────────

// Starter heroes are bought, not given — this bills them. Pay the returned invoice from your own
// wallet, then POST /heroes/starter to mint.
api.MapPost("/heroes/starter/quote", async (HttpContext http, GameService game, CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    var fee = await game.RequestStartersAsync(player, ct);
    return Results.Ok(new StarterQuoteResponse(
        game.StarterClaimFeeSats, GameService.StarterHeroCount, fee?.ToDto()));
});

api.MapPost("/heroes/starter", async (HttpContext http, GameService game, CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    var heroes = await game.ClaimStartersAsync(player, ct);
    return Results.Ok(new StarterResponse(heroes.Select(h => h.ToDto()).ToList()));
});

// Rarity-ordered so paging composes globally. skip/take are optional — omit take for the
// full set (Home's featured strip + Market's portrait lookup rely on that); capped at 200 when set.
api.MapGet("/heroes", (string? owner, int? skip, int? take, GameStore store) =>
{
    var heroes = ArkadeHeroes.Core.Progression.HeroRanking
        .ByRarity(store.Heroes.Values.Where(h => owner is null || h.OwnerId == owner))
        .Skip(skip ?? 0)
        .Take(take is int t ? Math.Min(t, 200) : int.MaxValue)
        .Select(h => h.ToDto())
        .ToList();
    return Results.Ok(heroes);
});

api.MapGet("/heroes/mine", (HttpContext http, int? skip, int? take, GameService game, GameStore store) =>
{
    var player = game.Authenticate(BearerToken(http));
    return Results.Ok(ArkadeHeroes.Core.Progression.HeroRanking
        .ByRarity(store.Heroes.Values.Where(h => h.OwnerId == player.Id))
        .Skip(skip ?? 0)
        .Take(take is int t ? Math.Min(t, 200) : int.MaxValue)
        .Select(h => h.ToDto())
        .ToList());
});

api.MapGet("/heroes/{heroId}", (string heroId, GameService game) =>
    Results.Ok(game.GetHero(heroId).ToDto()));

// A DESTROYED hero's headstone. Heroes are hard-deleted when they die — a death-match loser, a fusion's
// inputs, both sides of an absorb — so there is no hero row for /heroes/{id} to return and no HeroDto to
// shape. This is the shape a dead hero has instead, and it is why the page can say "destroyed" rather than
// "couldn't load this hero". Public, like the timeline: a hero's fate is part of its lineage's provenance.
// 404 when the id is simply unknown — that is a different fact from "this one died".
api.MapGet("/heroes/{heroId}/tombstone", (string heroId, GameStore store) =>
    store.HeroTombstones.TryGetValue(heroId, out var stone)
        ? Results.Ok(ToTombstoneDto(stone))
        : Results.NotFound());

// Everything that ever happened to one hero, newest first — how it was born, what it fought, what it was
// traded for, what it was fused from, and how it died. Public: a hero's provenance is what a buyer is
// really appraising, so it should not need an account to read. Served for a DESTROYED hero too, off its
// headstone — that is the page a player lands on after losing a death-match, and it is the one history
// that must not 404. Only an id nothing has ever heard of does.
api.MapGet("/heroes/{heroId}/timeline", (string heroId, GameService game) =>
{
    try { return Results.Ok(game.HeroTimeline(heroId)); }
    catch (GameRuleException) { return Results.NotFound(); }
});

// ── Breeding (commit → reveal) ─────────────────────────────────────────────

api.MapPost("/breeding/commit", async (BreedCommitRequest request, HttpContext http, GameService game,
    CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    var (session, invoice) = await game.CommitBreedingAsync(player, request.ParentAId, request.ParentBId, request.Mode, ct);
    return Results.Ok(new BreedCommitResponse(session.Id, session.CommitmentHex, invoice?.ToDto(),
        session.EscrowAddress, session.EscrowAddress is null ? 0 : session.FeeSats));
});

// Public breed-escrow parameters: everything a player needs to rebuild the
// escrow contract locally and reclaim an abandoned deposit. 404 for
// invoice-mode or unknown breedings.
api.MapGet("/breedings/{breedingId}/escrow", async (string breedingId, IChainService chain, CancellationToken ct) =>
    await chain.GetBreedEscrowParamsAsync(breedingId, ct) is { } p ? Results.Ok(p) : Results.NotFound());

api.MapPost("/breeding/{breedingId}/reveal", async (string breedingId, BreedRevealRequest request, HttpContext http, GameService game, CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    var (child, serverSeedHex, entropyHex, receipt) = await game.RevealBreedingAsync(player, breedingId, request.Nonce, ct);
    return Results.Ok(new BreedRevealResponse(child.ToDto(), serverSeedHex, entropyHex, "paid-at-commit", receipt));
});

// ── Stud service (propose → the stud owner consents → pay → reveal) ────────

api.MapPost("/stud/propose", async (StudProposeRequest request, HttpContext http, GameService game, CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    var proposal = await game.ProposeStudAsync(player, request.MyHeroId, request.StudHeroId, request.StudFeeSats, ct);
    return Results.Ok(new StudProposeResponse(proposal.Id, proposal.CommitmentHex,
        proposal.StudHeroId, proposal.StudOwnerPlayerId, proposal.StudFeeSats));
});

api.MapPost("/stud/{id}/accept", async (string id, HttpContext http, GameService game, CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    var (proposal, breedFee, studFee) = await game.AcceptStudAsync(player, id, ct);
    return Results.Ok(new StudAcceptResponse(proposal.Id, breedFee.ToDto(), studFee?.ToDto(), proposal.StudFeeSats));
});

// What an accepted proposal bills, re-readable by either party. The PROPOSER pays, but the accept response
// is handed to the STUD OWNER — so without this the side that owes the sats can't find out what they are.
api.MapGet("/stud/{id}/invoices", async (string id, HttpContext http, GameService game, CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    var (proposal, breedFee, studFee) = await game.GetStudInvoicesAsync(player, id, ct);
    return Results.Ok(new StudAcceptResponse(proposal.Id, breedFee.ToDto(), studFee?.ToDto(), proposal.StudFeeSats));
});

api.MapPost("/stud/{id}/decline", async (string id, HttpContext http, GameService game, CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    var proposal = await game.DeclineStudAsync(player, id, ct);
    return Results.Ok(ToStudDto(proposal));
});

api.MapPost("/stud/{id}/reveal", async (string id, StudRevealRequest request, HttpContext http, GameService game, CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    var (child, serverSeedHex, entropyHex, studFeePaid, receipt) = await game.RevealStudAsync(player, id, request.Nonce, ct);
    return Results.Ok(new StudRevealResponse(child.ToDto(), serverSeedHex, entropyHex, studFeePaid, receipt));
});

// Stud discovery: the proposals a browser needs to SEE an incoming request for its own hero — the rest of
// the stud API is by-id. Public like /deathmatch; the client filters to its own heroes. No fee invoice or
// seed is exposed here, only the offer and its state.
api.MapGet("/stud", (GameStore store) =>
    Results.Ok(store.StudProposals.Values
        .OrderByDescending(s => s.CreatedAt)
        .Take(50)
        .Select(ToStudDto)
        .ToList()));

// ── Bids: buy a hero that is NOT for sale (propose → the owner consents → deliver → settle) ──
//
// Shaped like /stud above, and for the same reason: the counterparty owns the thing. Nothing is billed
// until the owner accepts, so an ignored or refused bid costs the bidder nothing at all.

api.MapPost("/bids", async (PlaceBidRequest request, HttpContext http, GameService game, CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    return Results.Ok(ToBidDto(await game.ProposeBidAsync(player, request.HeroId, request.BidSats, ct)));
});

// THE CONSENT. The only place the invoice is created — before this the bidder owes nothing.
api.MapPost("/bids/{id}/accept", async (string id, HttpContext http, GameService game, CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    var (bid, invoice) = await game.AcceptBidAsync(player, id, ct);
    return Results.Ok(new BidInvoiceResponse(ToBidDto(bid), invoice.ToDto(), false, bid.BidSats - bid.FeeSats));
});

// What an accepted bid bills, re-readable by either party — and whether it is FUNDED. The BIDDER pays, but
// the accept response is handed to the OWNER, so without this the side that owes the money can't find out
// what it is, and the side about to send a hero can't find out whether the money arrived.
api.MapGet("/bids/{id}/invoice", async (string id, HttpContext http, GameService game, CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    var (bid, invoice, funded) = await game.GetBidInvoiceAsync(player, id, ct);
    return Results.Ok(new BidInvoiceResponse(ToBidDto(bid), invoice.ToDto(), funded, bid.BidSats - bid.FeeSats));
});

api.MapPost("/bids/{id}/decline", async (string id, HttpContext http, GameService game, CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    return Results.Ok(ToBidDto(await game.DeclineBidAsync(player, id, ct)));
});

api.MapPost("/bids/{id}/withdraw", async (string id, HttpContext http, GameService game, CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    return Results.Ok(ToBidDto(await game.WithdrawBidAsync(player, id, ct)));
});

// The close: the owner is paid and the hero's record follows the asset. Either party may call it — both
// want it run, and a one-sided trigger would let whoever holds it hold the other side hostage.
api.MapPost("/bids/{id}/settle", async (string id, HttpContext http, GameService game, CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    return Results.Ok((await game.SettleBidAsync(player, id, ct)).ToDto());
});

// The bidder's exit from an accepted bid the owner never honoured. Either party, and only past the window.
api.MapPost("/bids/{id}/refund", async (string id, HttpContext http, GameService game, CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    var (bid, refunded) = await game.RefundBidAsync(player, id, ct);
    return Results.Ok(new BidRefundResponse(ToBidDto(bid), refunded));
});

// Bid discovery: how an owner ever learns someone wants their hero, and how a bidder tracks their own.
// Public like /stud and /deathmatch; the client filters. No invoice is exposed here, only the offers.
api.MapGet("/bids", (GameService game) => Results.Ok(game.ListBids().Select(ToBidDto).ToList()));

// ── Merge / fusion (commit → deposit base+sacrifice+fee → reveal) ───────────

api.MapPost("/merge/commit", async (MergeCommitRequest request, HttpContext http, GameService game, CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    var (session, escrow) = await game.CommitMergeAsync(player, request.BaseId, request.SacrificeId, request.Mode, ct);
    return Results.Ok(new MergeCommitResponse(session.Id, session.CommitmentHex, escrow, session.FeeSats));
});

api.MapPost("/merge/{mergeId}/reveal", async (string mergeId, MergeRevealRequest request, HttpContext http, GameService game, CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    var (fused, serverSeedHex, entropyHex, receipt) = await game.RevealMergeAsync(player, mergeId, request.Nonce, ct);
    return Results.Ok(new MergeRevealResponse(fused.ToDto(), serverSeedHex, entropyHex, receipt));
});

// Public merge-escrow parameters: everything a player needs to rebuild the merge
// covenant locally and reclaim an abandoned deposit. 404 for unknown merges.
api.MapGet("/merges/{mergeId}/escrow", async (string mergeId, IChainService chain, CancellationToken ct) =>
    await chain.GetMergeEscrowParamsAsync(mergeId, ct) is { } p ? Results.Ok(p) : Results.NotFound());

// ── PvE gauntlet (F1): open (commit + fee) → pay → run (5 ghost waves) ──────

api.MapPost("/gauntlet/open", async (GauntletOpenRequest request, HttpContext http, GameService game, CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    var (session, invoice) = await game.OpenGauntletAsync(player, request.HeroId, ct);
    return Results.Ok(new GauntletOpenResponse(session.Id, session.CommitmentHex, invoice.ToDto()));
});

api.MapPost("/gauntlet/{id}/run", async (string id, GauntletRunRequest request, HttpContext http, GameService game, CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    var (run, xp, snapshot, item, itemAssetId, seed, entropy, receipt) = await game.RunGauntletAsync(player, id, request.Nonce, ct);
    // Surface each wave's ghost snapshot + fight log so the browser can replay the wave in the arena.
    // The ghost is a pure function of the run entropy + the PRE-run hero level (snapshot.Level), so this
    // reconstructs exactly what Gauntlet.Resolve fought — no soft-foe substitution possible.
    var entropyBytes = Convert.FromHexString(entropy);
    var waves = run.Waves.Select(w => new GauntletWaveDto(
        w.Wave, w.GhostLevel, w.Won,
        ArkadeHeroes.Core.Progression.Gauntlet.GhostFor(entropyBytes, w.Wave, snapshot.Level).ToDto(),
        w.Result.ToDto())).ToList();
    return Results.Ok(new GauntletRunResponse(run.WavesCleared, waves, xp, receipt.LevelA, item, itemAssetId, snapshot, seed, entropy, receipt, game.ConfigVersion, game.ContentVersion));
});

// ── Endless PvE Trials (cold-start solo leaderboard): open (commit, FREE) → run (endless ghost ladder) ──

// The spectator feed: which resolved fights were worth watching (upsets, stakes, prized heroes, flawless
// or near-death finishes). Public, and recomputable by anyone holding the same match + hero data.
api.MapGet("/highlights", (GameStore store) =>
{
    var heroes = store.Heroes.Values.ToDictionary(h => h.Id, h => new HighlightHero(
        h.Name, h.Level,
        ArkadeHeroes.Core.Progression.Rarity.Of(h.Genome).Tier.ToString() == "Legendary"
            || ArkadeHeroes.Core.Progression.FancySets.TitleFor(h.Genome) is not null));
    var resolved = store.Matches.Values.Where(m => m.Result is not null).Select(ToMatchDto);
    return Results.Ok(HighlightsBuilder.Build(resolved, heroes));
});

// The Fancy discovery race — who FIRST bred a hero expressing each named set. Every catalog title is
// returned, claimed or not, so the board doubles as "what's still undiscovered".
api.MapGet("/fancies/discoveries", (GameStore store) =>
{
    var board = ArkadeHeroes.Core.Progression.FancySets.AllTitles.Select(title =>
    {
        store.FancyDiscoveries.TryGetValue(title, out var d);
        return new FancyDiscoveryDto(
            title, d?.HeroId, d?.HeroName, d?.OwnerId, d?.UnixSeconds,
            store.FancyFindCount.GetValueOrDefault(title));
    }).ToList();
    return Results.Ok(board);
});

// The endless-Trials ladder — every hero's BEST run, recomputed from the signed "trials" receipts (each
// attests its own waves-survived), so the board carries no trust of its own. Same doctrine as /leaderboard.
api.MapGet("/trials/board", (GameStore store) =>
{
    var heroes = store.Heroes.Values.ToDictionary(h => h.Id, h => (h.Name, h.Level));
    var receipts = store.ReceiptsByHero.Values.SelectMany(list => list).DistinctBy(r => r.Id);
    return Results.Ok(TrialsBoardBuilder.Build(heroes, receipts));
});

api.MapPost("/trials/open", async (TrialsOpenRequest request, HttpContext http, GameService game) =>
{
    var player = game.Authenticate(BearerToken(http));
    var session = await game.OpenTrialsAsync(player, request.HeroId);
    return Results.Ok(new TrialsOpenResponse(session.Id, session.CommitmentHex, session.Affix.ToString(),
        ArkadeHeroes.Core.Progression.Trials.AffixDescription(session.Affix)));
});

api.MapPost("/trials/{id}/run", async (string id, TrialsRunRequest request, HttpContext http, GameService game) =>
{
    var player = game.Authenticate(BearerToken(http));
    var (run, snapshot, title, best, affix, seed, entropy, receipt) = await game.RunTrialsAsync(player, id, request.Nonce);
    // Surface each wave's ghost snapshot + fight log so the browser can replay the wave in the arena. The
    // ghost is a pure function of the run entropy + the run's pinned affix, so this reconstructs exactly
    // what Trials.Resolve fought — no soft-foe substitution possible.
    var entropyBytes = Convert.FromHexString(entropy);
    var waves = run.Waves.Select(w => new TrialsWaveDto(
        w.Wave, w.GhostLevel, w.Won,
        ArkadeHeroes.Core.Progression.Trials.GhostFor(entropyBytes, w.Wave, affix).ToDto(),
        w.Result.ToDto())).ToList();
    return Results.Ok(new TrialsRunResponse(
        run.WavesCleared, waves, title, best, affix.ToString(), snapshot, seed, entropy, receipt, game.ConfigVersion, game.ContentVersion));
});

// ── Death-match (open → both stake a hero → settle; loser's hero burns) ─────

api.MapPost("/deathmatch/open", async (DeathMatchOpenRequest request, HttpContext http, GameService game, CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    var (session, escrow, favor, challengerGear, defenderGear, feeInvoice) = await game.OpenDeathMatchAsync(player, request.ChallengerHeroId, request.DefenderHeroId, request.Absorb, ct);
    return Results.Ok(new DeathMatchOpenResponse(session.Id, session.CommitmentHex, escrow, favor, challengerGear, defenderGear, feeInvoice.ToDto()));
});

api.MapPost("/deathmatch/{id}/accept", async (string id, HttpContext http, GameService game, CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    var (_, escrow, defender, defenderGear, feeInvoice) = await game.AcceptDeathMatchAsync(player, id, ct);
    return Results.Ok(new DeathMatchAcceptResponse(escrow, defender.ToDto(), defenderGear, feeInvoice.ToDto()));
});

api.MapPost("/deathmatch/{id}/settle", async (string id, DeathMatchSettleRequest request, HttpContext http, GameService game, CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    var (result, winner, loser, challSnap, defSnap, seed, entropy, receipt, minted, absorbed, newGenome, newHero) = await game.SettleDeathMatchAsync(player, id, request.Nonce, ct);
    return Results.Ok(new DeathMatchSettleResponse(result, winner, loser, challSnap, defSnap, seed, entropy, receipt, minted, absorbed, newGenome, newHero, game.ConfigVersion, game.ContentVersion));
});

api.MapGet("/deathmatch/{id}/escrow", async (string id, IChainService chain, CancellationToken ct) =>
    await chain.GetDeathMatchEscrowParamsAsync(id, ct) is { } p ? Results.Ok(p) : Results.NotFound());

// Public spectator replay of a SETTLED death-match (mirrors /matches/{id}/replay) — watch + verify a
// permakill trustlessly from the revealed seed. 404 until settled. No auth — a shareable link.
api.MapGet("/deathmatch/{id}/replay", (string id, GameStore store) =>
    store.DeathMatches.TryGetValue(id, out var s)
        && s.Result is not null && s.ChallengerSnapshot is not null && s.DefenderSnapshot is not null
        ? Results.Ok(new MatchReplayDto(
            s.ChallengerSnapshot, s.DefenderSnapshot, s.Result.ToDto(), s.Result.WinnerId,
            s.CommitmentHex, Convert.ToHexString(s.ServerSeed).ToLowerInvariant(),
            s.EntropyHex ?? "", s.Nonce ?? "", s.ConfigVersion ?? "", s.ContentVersion ?? ""))
        : Results.NotFound());

// Death-match discovery: the sessions a browser needs to SEE an incoming challenge — no list
// endpoint existed (the console passes the death-match id out-of-band). Public like /matches; the
// client filters to its own heroes. Status is derived from the session's accepted/completed flags.
api.MapGet("/deathmatch", (GameStore store) =>
    Results.Ok(store.DeathMatches.Values
        .OrderByDescending(d => d.CreatedAt)
        .Take(50)
        .Select(d => new DeathMatchDto(
            d.Id, d.ChallengerHeroId, d.DefenderHeroId,
            d.Completed ? "resolved" : d.Accepted ? "accepted" : "open",
            d.Absorb, d.WinnerHeroId))
        .ToList()));

// ── Matches (open → fight) ─────────────────────────────────────────────────

api.MapPost("/matches/open", async (OpenMatchRequest request, HttpContext http, GameService game, CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    var (session, invoice, feeInvoice) = await game.OpenMatchAsync(player, request.ChallengerHeroId, request.DefenderHeroId,
        request.WagerSats, request.Mode, ct);
    // The challenger stakes into THEIR escrow address.
    return Results.Ok(new OpenMatchResponse(session.Id, session.CommitmentHex, session.WagerSats, session.Status,
        invoice?.ToDto(), session.EscrowChallengerAddress, session.EscrowChallengerAddress is null ? 0 : session.WagerSats,
        feeInvoice?.ToDto()));
});

api.MapPost("/matches/{matchId}/accept", async (string matchId, HttpContext http, GameService game, CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    var (session, invoice, feeInvoice) = await game.AcceptMatchAsync(player, matchId, ct);
    // The defender stakes into THEIR escrow address.
    return Results.Ok(new AcceptMatchResponse(ToMatchDto(session), invoice?.ToDto(),
        session.EscrowDefenderAddress, session.EscrowDefenderAddress is null ? 0 : session.WagerSats,
        feeInvoice?.ToDto()));
});

api.MapPost("/matches/{matchId}/fight", async (string matchId, FightRequest request, HttpContext http, GameService game, CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    var (session, result, serverSeedHex, entropyHex, challengerXp, defenderXp,
            challengerSnapshot, defenderSnapshot, winnerPayout, receipt) =
        await game.FightAsync(player, matchId, request.Nonce, ct);
    var challenger = game.GetHero(session.ChallengerHeroId);
    var defender = game.GetHero(session.DefenderHeroId);
    return Results.Ok(new FightResponse(result.ToDto(), serverSeedHex, entropyHex,
        challengerXp, defenderXp, challenger.ToDto(), defender.ToDto(),
        challengerSnapshot, defenderSnapshot, session.WagerSats, winnerPayout, receipt,
        session.ConfigVersion ?? "", session.ContentVersion ?? ""));
});

api.MapGet("/matches", async (string? status, GameService game, GameStore store, CancellationToken ct) =>
{
    // Lazily expire abandoned covenant matches (e.g. a refunded stake) so they
    // drop out of the open/accepted lists instead of lingering as stale rows.
    await game.ReconcileAbandonedMatchesAsync(ct);
    return Results.Ok(store.Matches.Values
        .Where(m => status is null || m.Status == status)
        .OrderByDescending(m => m.CreatedAt)
        .Take(50)
        .Select(ToMatchDto)
        .ToList());
});

api.MapGet("/matches/{matchId}", (string matchId, GameStore store) =>
    store.Matches.TryGetValue(matchId, out var session)
        ? Results.Ok(ToMatchDto(session))
        : Results.NotFound());

// Public spectator replay: everything to replay a RESOLVED match in the arena + verify it was fair
// (VerifyMatch re-derives the fight from the revealed seed). 404 until resolved. No auth — a shareable link.
api.MapGet("/matches/{matchId}/replay", (string matchId, GameStore store) =>
    store.Matches.TryGetValue(matchId, out var s)
        && s.Result is not null && s.ChallengerSnapshot is not null && s.DefenderSnapshot is not null
        ? Results.Ok(new MatchReplayDto(
            s.ChallengerSnapshot, s.DefenderSnapshot, s.Result.ToDto(), s.Result.WinnerId,
            s.CommitmentHex, Convert.ToHexString(s.ServerSeed).ToLowerInvariant(),
            s.EntropyHex ?? "", s.Nonce ?? "", s.ConfigVersion ?? "", s.ContentVersion ?? ""))
        : Results.NotFound());

// ── Team 3v3 squad matches (open → accept → resolve), reusing the wager escrow ─────────────────
api.MapPost("/squad/open", async (OpenSquadMatchRequest request, HttpContext http, GameService game, CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    var (session, invoice, feeInvoice) = await game.OpenSquadMatchAsync(player, request, ct);
    return Results.Ok(new SquadOpenResponse(session.Id, session.CommitmentHex, session.WagerSats, session.Status,
        invoice?.ToDto(), session.EscrowChallengerAddress, session.EscrowChallengerAddress is null ? 0 : session.WagerSats,
        feeInvoice?.ToDto()));
});

api.MapPost("/squad/{matchId}/accept", async (string matchId, HttpContext http, GameService game, CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    var (session, invoice, feeInvoice) = await game.AcceptSquadMatchAsync(player, matchId, ct);
    return Results.Ok(new SquadAcceptResponse(ToSquadMatchDto(session), invoice?.ToDto(),
        session.EscrowDefenderAddress, session.EscrowDefenderAddress is null ? 0 : session.WagerSats, feeInvoice?.ToDto()));
});

api.MapPost("/squad/{matchId}/resolve", async (string matchId, FightRequest request, HttpContext http, GameService game, CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    var (_, result, serverSeedHex, entropyHex, challSnaps, defSnaps, winnerPayout, receipts) =
        await game.ResolveSquadMatchAsync(player, matchId, request.Nonce, ct);
    return Results.Ok(new SquadResolveResponse(result.ToDto(challSnaps, defSnaps), serverSeedHex, entropyHex, winnerPayout, receipts));
});

api.MapGet("/squad", (string? status, GameStore store) =>
    Results.Ok(store.SquadMatches.Values
        .Where(m => status is null || m.Status == status)
        .Select(ToSquadMatchDto).Take(50).ToList()));

// Public spectator replay of a resolved squad match (VerifySquad re-runs the best-of-3 from the revealed seed).
api.MapGet("/squad/{matchId}/replay", (string matchId, GameStore store) =>
    store.SquadMatches.TryGetValue(matchId, out var s)
        && s.Result is { } r && s.ChallengerSnapshots is not null && s.DefenderSnapshots is not null
        ? Results.Ok(new SquadReplayDto(
            s.ChallengerSnapshots, s.DefenderSnapshots, r.ToDto(s.ChallengerSnapshots, s.DefenderSnapshots),
            s.CommitmentHex, Convert.ToHexString(s.ServerSeed).ToLowerInvariant(), s.EntropyHex ?? "", s.Nonce ?? "",
            s.ConfigVersion ?? "", s.ContentVersion ?? ""))
        : Results.NotFound());

// ── Tournaments (open → join → resolve): a buy-in bracket, prizes to the podium minus the house rake ──
api.MapPost("/tournament/open", async (OpenTournamentRequest request, HttpContext http, GameService game, CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    var (session, buyIn) = await game.OpenTournamentAsync(player, request.HeroId, request.BuyInSats, request.Size, ct);
    return Results.Ok(new TournamentEntryResponse(ToTournamentDto(session), buyIn.ToDto()));
});

api.MapPost("/tournament/{id}/join", async (string id, JoinTournamentRequest request, HttpContext http, GameService game, CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    var (session, buyIn) = await game.JoinTournamentAsync(player, id, request.HeroId, ct);
    return Results.Ok(new TournamentEntryResponse(ToTournamentDto(session), buyIn.ToDto()));
});

api.MapPost("/tournament/{id}/resolve", async (string id, FightRequest request, HttpContext http, GameService game, CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    var (session, result, seedHex, entropyHex, prizes) = await game.ResolveTournamentAsync(player, id, request.Nonce, ct);
    return Results.Ok(new TournamentResolveResponse(ToTournamentDto(session),
        result.Matches.Where(m => m.Result is not null)
            .Select(m => new TournamentMatchDto(m.Round, m.Index, m.AId, m.BId, m.WinnerId)).ToList(),
        seedHex, entropyHex, prizes));
});

// Safety valve for a STRANDED bracket (an entrant hero lost to a restart, or burned/merged away): it can
// never resolve, so every PAID buy-in goes back to its entrant. Any signed-in player may trigger it — a
// still-resolvable bracket is refused, so the pot can't be unwound out from under a live tournament.
api.MapPost("/tournament/{id}/refund", async (string id, HttpContext http, GameService game, CancellationToken ct) =>
{
    game.Authenticate(BearerToken(http));
    var (session, entrantsRefunded, refundedSats) = await game.RefundTournamentAsync(id, ct);
    return Results.Ok(new TournamentRefundResponse(ToTournamentDto(session), entrantsRefunded, refundedSats));
});

api.MapGet("/tournament", (GameStore store) =>
    Results.Ok(store.Tournaments.Values.OrderByDescending(t => t.Id).Select(ToTournamentDto).Take(50).ToList()));

api.MapGet("/tournament/{id}", (string id, GameStore store) =>
    store.Tournaments.TryGetValue(id, out var t) ? Results.Ok(ToTournamentDto(t)) : Results.NotFound());

// Public spectator replay of a resolved tournament (VerifyTournament re-runs the bracket from the revealed seed).
api.MapGet("/tournament/{id}/replay", (string id, GameStore store) =>
    store.Tournaments.TryGetValue(id, out var t) && t.Result is { } r && t.EntrantSnapshots is not null
        ? Results.Ok(new TournamentReplayDto(
            t.EntrantSnapshots,
            r.Matches.Where(m => m.Result is not null)
                .Select(m => new TournamentMatchDto(m.Round, m.Index, m.AId, m.BId, m.WinnerId)).ToList(),
            r.ChampionId, t.CommitmentHex, Convert.ToHexString(t.ServerSeed).ToLowerInvariant(),
            t.EntropyHex ?? "", t.Nonce ?? "", t.EntrantsCommitmentHex, t.ConfigVersion ?? "", t.ContentVersion ?? ""))
        : Results.NotFound());

// XP-weighted matchmaking: other players' heroes ranked by level proximity to the
// given hero, each annotated with the conserved XP a staked win/loss would move.
api.MapGet("/matchmaking/{heroId}", (string heroId, HttpContext http, GameService game) =>
{
    var player = game.Authenticate(BearerToken(http));
    return Results.Ok(game.SuggestOpponents(player, heroId));
});

// Rarity leaderboard: heroes ranked by their trustless, genome-derived rarity score.
api.MapGet("/rarest", (GameStore store) =>
    Results.Ok(store.Heroes.Values
        .OrderByDescending(h => ArkadeHeroes.Core.Progression.Rarity.Of(h.Genome).Score)
        .ThenByDescending(h => h.Generation)
        .ThenBy(h => h.Id, StringComparer.Ordinal)   // unique id last → a TOTAL order, so the board is stable
        .Take(20)
        .Select(h => h.ToDto())
        .ToList()));

// The Fancy board: heroes whose genome hits a named cosmetic set (Sovereign/Emberlord/...), a concrete
// breeding target beyond a rarity number. Pure genome derivation — same trustless basis as /rarest.
api.MapGet("/fancies", (GameStore store) =>
    Results.Ok(store.Heroes.Values
        .Where(h => ArkadeHeroes.Core.Progression.FancySets.TitleFor(h.Genome) is not null)
        .OrderByDescending(h => ArkadeHeroes.Core.Progression.Rarity.Of(h.Genome).Score)
        .ThenByDescending(h => h.Generation)
        .ThenBy(h => h.Id, StringComparer.Ordinal)   // unique id last → a TOTAL order, so the board is stable
        .Take(30)
        .Select(h => h.ToDto())
        .ToList()));

// The Founders board: generation-0 heroes — the starter-issued originals, ranked by how far their owners have
// leveled them. NOT sorted by rarity: every gen-0 genome has its trait bytes zeroed (Genome.NewGen0), so a
// rarity score would be a constant 0 and sort nothing. Ordered by level, id-tiebroken for a stable board.
api.MapGet("/founders", (GameStore store) =>
    Results.Ok(store.Heroes.Values
        .Where(h => h.Generation == 0)
        .OrderByDescending(h => h.Level)
        .ThenBy(h => h.Id, StringComparer.Ordinal)
        .Take(30)
        .Select(h => h.ToDto())
        .ToList()));

// Public escrow parameters of a covenant match: everything a player needs to
// rebuild the per-party contracts locally (WagerEscrowContracts.Build) and
// reclaim a timelocked refund WITHOUT trusting this server. 404 for
// invoice-mode or unknown matches.
api.MapGet("/matches/{matchId}/escrow", async (string matchId, IChainService chain, CancellationToken ct) =>
    await chain.GetWagerEscrowParamsAsync(matchId, ct) is { } parameters
        ? Results.Ok(parameters)
        : Results.NotFound());

// ── Hero transfer: the owner's wallet moves the asset; this confirms ───────

api.MapPost("/heroes/{heroId}/transfer", async (string heroId, TransferRequest request, HttpContext http, GameService game, CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    var hero = await game.ConfirmTransferAsync(player, heroId, request.ToPlayerId, ct);
    return Results.Ok(new TransferResponse(hero.ToDto()));
});

// Unique-name registry: request a custom name (returns the treasury fee-invoice to pay), then confirm.
api.MapPost("/heroes/{heroId}/rename", async (string heroId, RenameHeroRequest request, HttpContext http, GameService game, CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    var fee = await game.RequestRenameAsync(player, heroId, request.Name, ct);
    return Results.Ok(new RenameHeroResponse(fee?.AmountSats ?? 0, fee?.ToDto()));
});

api.MapPost("/heroes/{heroId}/rename/confirm", async (string heroId, HttpContext http, GameService game, CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    var hero = await game.ConfirmRenameAsync(player, heroId, ct);
    return Results.Ok(hero.ToDto());
});

// ── Items ──────────────────────────────────────────────────────────────────

api.MapGet("/items", () => Results.Ok(ItemCatalog.All.Select(i => i.ToDto()).ToList()));

// The catalog ids the signed-in player already owns — lets the shop mark them without re-buying.
api.MapGet("/items/mine", async (HttpContext http, GameService game, CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    return Results.Ok(await game.OwnedItemIdsAsync(player, ct));
});

api.MapPost("/items/{itemId}/buy", async (string itemId, HttpContext http, GameService game, CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    var (_, invoice) = await game.CreateItemInvoiceAsync(player, itemId, ct);
    return Results.Ok(new ItemInvoiceResponse(invoice.ToDto()));
});

api.MapPost("/items/claim", async (ClaimItemRequest request, HttpContext http, GameService game, CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    var (itemAssetId, arkTxId, unitsHeld) = await game.ClaimItemAsync(player, request.InvoiceId, ct);
    return Results.Ok(new ClaimItemResponse(itemAssetId, arkTxId, unitsHeld));
});

api.MapPost("/heroes/{heroId}/equip", async (string heroId, EquipRequest request, HttpContext http, GameService game, CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    var hero = await game.EquipAsync(player, heroId, request.ItemId, ct);
    return Results.Ok(new EquipResponse(hero.ToDto()));
});

api.MapPost("/heroes/{heroId}/unequip", async (string heroId, UnequipRequest request, HttpContext http, GameService game) =>
{
    var player = game.Authenticate(BearerToken(http));
    var hero = await game.UnequipAsync(player, heroId, request.Slot);
    return Results.Ok(new EquipResponse(hero.ToDto()));
});

// ── Marketplace: resting item offers (covenant-enforced, buyer-funded) ─────

api.MapPost("/offers", async (CreateOfferRequest request, HttpContext http, GameService game, CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    var (listing, info) = await game.CreateOfferAsync(player, request.ItemId, request.AskSats, ct);
    return Results.Ok(new CreateOfferResponse(info.OfferId, info.OfferAddress, info.ItemAssetId,
        info.AskSats, info.OfferValueSats, info.RefundAfterUnixSeconds, listing.ListingFeeSats));
});

// Hero sales reuse the same offer covenant (a hero is a unique asset).
api.MapPost("/offers/hero", async (CreateHeroOfferRequest request, HttpContext http, GameService game, CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    var (listing, info) = await game.CreateHeroOfferAsync(player, request.HeroId, request.AskSats, ct);
    return Results.Ok(new CreateOfferResponse(info.OfferId, info.OfferAddress, info.ItemAssetId,
        info.AskSats, info.OfferValueSats, info.RefundAfterUnixSeconds, listing.ListingFeeSats));
});

// The buyer claims game-side ownership after fulfilling a hero offer from their
// own wallet (the server verifies the chain shows them holding the hero asset).
api.MapPost("/offers/{offerId}/claim-hero", async (string offerId, HttpContext http, GameService game, CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    return Results.Ok(new TransferResponse((await game.ClaimPurchasedHeroAsync(player, offerId, ct)).ToDto()));
});

api.MapGet("/offers", async (GameService game, GameStore store, CancellationToken ct) =>
    Results.Ok((await game.ListOffersAsync(ct)).Select(o => ToOfferDto(o, store)).ToList()));

// Recently sold (closed) offers — the marketplace's "just changed hands" strip.
api.MapGet("/offers/sold", async (GameService game, GameStore store, int? take, CancellationToken ct) =>
    Results.Ok((await game.ListSoldOffersAsync(take ?? 6, ct)).Select(o => ToOfferDto(o, store)).ToList()));

api.MapGet("/offers/{offerId}", async (string offerId, GameService game, GameStore store, CancellationToken ct) =>
{
    try { return Results.Ok(ToOfferDto(await game.GetOfferAsync(offerId, ct), store)); }
    catch (GameRuleException) { return Results.NotFound(); }
});

// Public offer parameters: everything a BUYER needs to rebuild the offer
// covenant locally, verify the address matches the listing, and fulfil it (or
// the SELLER to reclaim after expiry). 404 for unknown offers.
api.MapGet("/offers/{offerId}/params", async (string offerId, IChainService chain, CancellationToken ct) =>
    await chain.GetOfferParamsAsync(offerId, ct) is { } p ? Results.Ok(p) : Results.NotFound());

// ── Chain / health ─────────────────────────────────────────────────────────

api.MapGet("/chain/info", async (IChainService chain, ReceiptSigner receipts, IConfiguration config,
    Microsoft.Extensions.Options.IOptions<GameOptions> gameOptions, CancellationToken ct) =>
{
    var info = await chain.GetInfoAsync(ct);
    // Advertised so clients can run covenant refunds without out-of-band
    // config; defaults mirror NArkChainOptions. Meaningless in InMemory mode.
    var isNArk = info.Mode.Equals("NArk", StringComparison.OrdinalIgnoreCase);
    var g = gameOptions.Value;
    return Results.Ok(new ChainInfoDto(info.Mode, info.Network, info.TreasuryAddress, info.SpeciesAssetId,
        info.EmulatorSignerKey, receipts.PublicKeyHex,
        isNArk ? config["Chain:NArk:EmulatorUri"] ?? "http://localhost:7073" : null,
        isNArk ? config["Chain:NArk:EsploraUri"] ?? "http://localhost:3000/api" : null,
        g.AbsorbChance, g.AbsorbContinueChance,
        GameConfigDto.From(g.ToGameConfig())));
});

// Resolve a STAMPED rules version to the rules themselves, so a client holding a replay stamped with
// something other than its own compiled-in GameConfig.Default can still replay it faithfully.
//
// This registry is the RUNNING PROCESS's rules plus GameConfig.Default — deliberately not a durable archive
// of every config ever served. That is exactly sufficient for what this server can serve: it stamps every
// outcome from its own live config and holds resolved sessions in memory, so every replay it hands out
// carries a version it can resolve; and Default covers every pre-stamp artifact plus any replay from a
// default-rules deployment.
//
// An UNKNOWN version is a 404 — an explicit, honest failure. It must never fall back to Default: replaying
// under rules that are merely PLAUSIBLE is precisely the bug this endpoint exists to fix, and it would
// print "fairness ✗ SERVER CHEATED" over an honest result. A client that gets a 404 here (a replay saved
// across a retune + restart) must say it cannot verify, not guess.
api.MapGet("/config/{version}", (string version, GameService game) =>
{
    foreach (var candidate in new[] { game.Config, ArkadeHeroes.Core.GameConfig.Default })
    {
        var rules = GameRulesDto.From(candidate);
        if (rules.Version.Equals(version, StringComparison.OrdinalIgnoreCase))
            return Results.Ok(rules);
    }
    return Results.NotFound(new ErrorResponse(
        $"Unknown game-config version '{version}'. This server cannot serve the rules that outcome was " +
        "resolved under, so it cannot be verified here."));
});

// Resolve a STAMPED content version to the authored gear and dungeons themselves. Exactly the same
// contract as /config/{version} above, for the same reason: item stats feed combat resolution, so a
// verifier that rebuilt a hero's loadout from DIFFERENT content than the match ran on would replay a
// different fight and call an honest server a cheat.
//
// The registry is this build's own pack — which is exactly what this process can honestly serve, since it
// stamps every outcome from that pack. An UNKNOWN version is a 404, never a fall back to the compiled-in
// pack: a client that gets one must say it cannot verify rather than guess.
api.MapGet("/content/{version}", (string version, GameService game) =>
{
    var pack = game.Content;
    if (!ArkadeHeroes.Core.Content.ContentPackVersion.Compute(pack)
            .Equals(version, StringComparison.OrdinalIgnoreCase))
        return Results.NotFound(new ErrorResponse(
            $"Unknown content version '{version}'. This server cannot serve the gear and dungeons that " +
            "outcome was resolved under, so it cannot be verified here."));

    return Results.Ok(new ContentPackDto(pack.Version, pack.ItemsJson, pack.DungeonsJson));
});

// Receipts are signed public facts — anyone can pull a hero's chain and
// recompute its progression (the server DB is just a cache of this).
api.MapGet("/receipts/hero/{heroId}", (string heroId, GameStore store) =>
    Results.Ok(store.ReceiptsByHero.TryGetValue(heroId, out var list)
        ? list.ToArray()
        : Array.Empty<ProgressionReceiptDto>()));

// The leaderboard, recomputed from the signed receipts (each match receipt is
// filed under both heroes, so dedupe by receipt id). No trust of its own — a
// client holding the receipt chain gets the same ranking.
api.MapGet("/leaderboard", (GameStore store) =>
{
    var heroes = store.Heroes.Values.ToDictionary(
        h => h.Id, h => (h.Name, h.Level, h.OwnerId));
    var receipts = store.ReceiptsByHero.Values
        .SelectMany(list => list)
        .DistinctBy(r => r.Id);
    return Results.Ok(LeaderboardBuilder.Build(heroes, receipts));
});

// The current SEASON's ranked ladder — staked-match wins within a time-boxed window that auto-resets each
// season (a renewable competitive goal). Same trustless receipt tally as /leaderboard, windowed by the clock.
api.MapGet("/leaderboard/season", async (GameService game, CancellationToken ct) => Results.Ok(await game.SeasonLeaderboard(ct)));

// Daily engagement loop: the signed-in player's quests + streak (GET), and the once-per-UTC-day claim
// that pays base + per-quest bonus (streak-scaled) from the treasury (POST).
api.MapGet("/daily", (HttpContext http, GameService game) =>
    Results.Ok(game.DailyStatus(game.Authenticate(BearerToken(http)))));
api.MapPost("/daily/claim", async (HttpContext http, GameService game, CancellationToken ct) =>
    Results.Ok(await game.ClaimDailyAsync(game.Authenticate(BearerToken(http)), ct)));

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

// ── Operator console (/api/admin/*) ────────────────────────────────────────
// One shared secret in the X-Admin-Token header, compared in constant time (AdminGate), required on EVERY
// route here — the check is a group-wide endpoint filter rather than a per-route call, so a route added
// later cannot be born unauthenticated.
//
// With the secret unset the group is NOT MAPPED: these routes do not exist and every one of them 404s.
// That is the fail-closed direction and the one a deployment gets by omission — this server holds real
// bitcoin, and an admin surface that defaults to open is worse than no admin surface at all.
//
// The read is pure observation. The three ACTIONS are the exception, and each is an operation that already
// existed, is already tested, and is already reachable by other means — none of them is a new way to move
// money. Every one of them is logged with what was done and to what.
var adminToken = builder.Configuration["Game:AdminToken"];
if (AdminGate.IsEnabled(adminToken))
{
    var admin = api.MapGroup("/admin").AddEndpointFilter(async (context, next) =>
    {
        var supplied = context.HttpContext.Request.Headers[AdminApiContract.TokenHeader].ToString();
        if (AdminGate.Matches(adminToken, supplied)) return await next(context);
        // The FACT of the refusal and the path — never the supplied value. A rejected guess is still a
        // secret-shaped string, and a log full of them is a dictionary for whoever reads the logs.
        app.Logger.LogWarning("Admin request to {Path} refused: missing or wrong {Header}.",
            context.HttpContext.Request.Path, AdminApiContract.TokenHeader);
        return Results.Json(new ErrorResponse("Admin authorisation required."),
            statusCode: StatusCodes.Status401Unauthorized);
    });

    admin.MapGet("/overview", async (GameService game, CancellationToken ct) =>
        Results.Ok(await game.AdminOverviewAsync(ct)));

    // ── The append-only audit log ──────────────────────────────────────────
    // A pure read of history. Behind the admin gate because the log names every player, every amount and
    // every counterparty in the game — it is the most sensitive read on the server, and strictly more
    // revealing than the overview beside it.
    //
    // Paged on the SEQUENCE, exclusive: `after` is the last sequence you saw, so a poller can never skip
    // an event or re-read one, and the page size is clamped (SqliteAuditLog.MaxPageSize) because this table
    // grows forever by design and an unbounded read is how you fall over on your own history.
    admin.MapGet("/audit", async (
        long? after, int? take, string? subject, string? type, string? actor,
        IAuditLog audit, CancellationToken ct) =>
    {
        var from = after ?? 0;
        var events = await audit.ReadAsync(from, take ?? 100, subject, type, actor, ct);
        return Results.Ok(new AuditPageDto(
            events, events.Count > 0 ? events[^1].Sequence : from, audit.WriteFailures));
    });

    // The per-subject read as a first-class URL: everything that ever happened to ONE hero, match,
    // death-match, offer, tournament, stud proposal or player, in the order it happened. Same filter the
    // query parameter above exposes — it exists separately because "show me this hero's history" is the
    // question an operator actually arrives with, and it should not require knowing the query shape.
    admin.MapGet("/audit/subjects/{subjectId}", async (
        string subjectId, long? after, int? take, IAuditLog audit, CancellationToken ct) =>
    {
        var from = after ?? 0;
        var events = await audit.ReadAsync(from, take ?? 100, subjectId, null, null, ct);
        return Results.Ok(new AuditPageDto(
            events, events.Count > 0 ? events[^1].Sequence : from, audit.WriteFailures));
    });

    // ACTION — the strand refund (#103): a bracket that can never resolve pays every CLEARED buy-in back
    // to its entrant. Unchanged from the player-facing endpoint that already exposes it; safe because the
    // service itself refuses a bracket that can still be played, marks durably BEFORE paying, and is
    // single-shot. The operator route exists so this can be done without holding a player account.
    admin.MapPost("/tournaments/{id}/refund", async (string id, GameService game, IAuditLog audit, CancellationToken ct) =>
    {
        // The REQUEST, then the OUTCOME — two lines, because the service refuses a bracket that can still
        // be played. A request logged with no outcome after it is a refusal, and reads as one.
        app.Logger.LogInformation("ADMIN ACTION refund-tournament: requested for bracket {TournamentId}.", id);
        var (session, entrantsRefunded, refundedSats) = await game.RefundTournamentAsync(id, ct);
        app.Logger.LogInformation(
            "ADMIN ACTION refund-tournament: bracket {TournamentId} is now {Status}; {Entrants} entrant(s) "
            + "refunded {Sats} sat in total.", id, session.Status, entrantsRefunded, refundedSats);
        // The actor is NULL and stays null: the operator console authenticates with ONE shared token that
        // names no person, so any player id put here would be an invention. The event records that the
        // OPERATOR did it; who was holding the token is a deployment question, not one this server can answer.
        await audit.RecordAsync(new AuditEntry(AuditEventType.AdminAction, null, [id],
            new { action = "refund-tournament", tournamentId = id, status = session.Status, entrantsRefunded, refundedSats }));
        return Results.Ok(new TournamentRefundResponse(ToTournamentDto(session), entrantsRefunded, refundedSats));
    });

    // ACTION — expire covenant matches abandoned past their refund window. Moves NO money: it flips a
    // status so the stake becomes reclaimable by each player's OWN wallet. Already runs on every listing
    // of /api/matches, so this only chooses the moment.
    admin.MapPost("/actions/reconcile-matches", async (GameService game, GameStore store, IAuditLog audit, CancellationToken ct) =>
    {
        var before = store.Matches.Values.Count(m => m.Status == "expired");
        await game.ReconcileAbandonedMatchesAsync(ct);
        var after = store.Matches.Values.Count(m => m.Status == "expired");
        // Worded as what the counter DID across this call, not as what this call caused: the same lazy
        // reconcile runs on every /api/matches listing, so a concurrent visitor can move it too. An audit
        // line that over-claims is worse than one that merely reports the window it observed.
        var detail = $"Expired matches went {before} → {after} across this run; "
                     + $"{after} of {store.Matches.Count} now expired.";
        app.Logger.LogInformation("ADMIN ACTION reconcile-matches: {Detail}", detail);
        await audit.RecordAsync(new AuditEntry(AuditEventType.AdminAction, null, [],
            new { action = "reconcile-matches", expiredBefore = before, expiredAfter = after, detail }));
        return Results.Ok(new AdminActionResultDto("reconcile-matches", detail));
    });

    // ACTION — settle any season that has ENDED but not been paid. This is the SAME call GET
    // /api/leaderboard/season already makes on every anonymous page load, so it grants no capability an
    // unauthenticated visitor does not already have; it only lets an operator choose the moment and see
    // the result. Already idempotent: the settled marker advances before a sat moves, under a lock.
    admin.MapPost("/actions/settle-seasons", async (GameService game, GameStore store, IAuditLog audit, CancellationToken ct) =>
    {
        var before = store.LastSettledSeason;
        var board = await game.SeasonLeaderboard(ct);
        // Same discipline as reconcile-matches: the marker's movement is REPORTED, not claimed, because an
        // anonymous read of the season board settles too and could have moved it during this call.
        var detail = store.LastSettledSeason > before
            ? $"The settled-season marker advanced {before} → {store.LastSettledSeason}; "
              + $"season {board.SeasonNumber} is live."
            : $"Nothing was due — season {board.SeasonNumber} is live, last settled {store.LastSettledSeason}.";
        app.Logger.LogInformation("ADMIN ACTION settle-seasons: {Detail}", detail);
        // The settle ITSELF logs season.settled + treasury.outflow from inside the service, so this entry
        // records only that an operator chose the moment — never a second copy of what was paid.
        await audit.RecordAsync(new AuditEntry(AuditEventType.AdminAction, null, [],
            new { action = "settle-seasons", settledBefore = before, settledAfter = store.LastSettledSeason, detail }));
        return Results.Ok(new AdminActionResultDto("settle-seasons", detail));
    });
}
else
{
    // Said once at boot so "my token doesn't work" has an answer that isn't guesswork. Names the key, never
    // a value — there is no value to name.
    app.Logger.LogInformation(
        "Operator console DISABLED: no Game:AdminToken configured, so /api/admin/* is not mapped. "
        + "Set Game__AdminToken to enable it.");
}

// ── Dev-only simulation of the CLIENT wallet (InMemory chain mode only) ────
// These stand in for the player's own wallet actions until/unless a real
// wallet is attached. They do not exist in NArk mode — there, the client's
// actual wallet pays invoices and moves assets.
if (!chainMode.Equals("NArk", StringComparison.OrdinalIgnoreCase))
{
    var dev = app.MapGroup("/api/dev");

    dev.MapPost("/pay-invoice", (PayInvoiceDevRequest request, HttpContext http, GameService game, IChainService chain) =>
    {
        var player = game.Authenticate(BearerToken(http));
        Sim(chain).PayInvoiceFromPlayer(player.Id, request.InvoiceId);
        return Results.Ok(new { paid = true });
    });

    // Credit the simulated treasury so the daily faucet has reserves to pay out (a fresh treasury
    // has no fee income yet). In NArk the treasury is a real funded address, so this doesn't exist.
    dev.MapPost("/fund-treasury", (FundTreasuryDevRequest request, IChainService chain) =>
    {
        Sim(chain).FundTreasury(request.Sats);
        return Results.Ok(new { funded = true });
    });

    // Mint one extra gen-0 hero (InMemory only) — lets tests build a full 3-hero squad lineup.
    dev.MapPost("/mint-hero", async (HttpContext http, GameService game, CancellationToken ct) =>
    {
        var player = game.Authenticate(BearerToken(http));
        var hero = await game.DevMintHeroAsync(player, ct);
        return Results.Ok(hero.ToDto());
    });

    dev.MapPost("/transfer-asset", (TransferAssetDevRequest request, HttpContext http, GameService game, IChainService chain) =>
    {
        var player = game.Authenticate(BearerToken(http));
        Sim(chain).TransferAssetFromPlayer(player.Id, request.ToPlayerId, request.AssetId);
        return Results.Ok(new { transferred = true });
    });

    dev.MapPost("/stake-escrow", (StakeEscrowDevRequest request, HttpContext http, GameService game, IChainService chain) =>
    {
        var player = game.Authenticate(BearerToken(http));
        Sim(chain).StakeEscrowFromPlayer(player.Id, request.MatchId);
        return Results.Ok(new { staked = true });
    });

    dev.MapPost("/refund-escrow", (RefundEscrowDevRequest request, HttpContext http, GameService game, IChainService chain) =>
    {
        var player = game.Authenticate(BearerToken(http));
        try
        {
            Sim(chain).RefundEscrowFromPlayer(player.Id, request.MatchId);
        }
        catch (InvalidOperationException ex)
        {
            // Domain refusals (locked, not a party, nothing staked, settled)
            // are 400s, matching what the real covenant path surfaces.
            throw new GameRuleException(ex.Message);
        }
        return Results.Ok(new { refunded = true });
    });

    dev.MapPost("/fund-breed-escrow", (FundBreedEscrowDevRequest request, HttpContext http, GameService game, IChainService chain) =>
    {
        var player = game.Authenticate(BearerToken(http));
        try { Sim(chain).FundBreedEscrowFromPlayer(player.Id, request.BreedingId); }
        catch (InvalidOperationException ex) { throw new GameRuleException(ex.Message); }
        return Results.Ok(new { funded = true });
    });

    dev.MapPost("/fund-merge-escrow", (FundMergeEscrowDevRequest request, HttpContext http, GameService game, IChainService chain) =>
    {
        var player = game.Authenticate(BearerToken(http));
        try { Sim(chain).FundMergeEscrowFromPlayer(player.Id, request.MergeId); }
        catch (InvalidOperationException ex) { throw new GameRuleException(ex.Message); }
        return Results.Ok(new { funded = true });
    });

    dev.MapPost("/fund-deathmatch-escrow", (FundDeathMatchEscrowDevRequest request, HttpContext http, GameService game, IChainService chain) =>
    {
        var player = game.Authenticate(BearerToken(http));
        try { Sim(chain).FundDeathMatchEscrowFromPlayer(player.Id, request.DeathMatchId, request.Role); }
        catch (InvalidOperationException ex) { throw new GameRuleException(ex.Message); }
        return Results.Ok(new { funded = true });
    });

    dev.MapPost("/refund-merge", (RefundMergeDevRequest request, HttpContext http, GameService game, IChainService chain) =>
    {
        var player = game.Authenticate(BearerToken(http));
        try { Sim(chain).RefundMergeEscrowFromPlayer(player.Id, request.MergeId); }
        catch (InvalidOperationException ex) { throw new GameRuleException(ex.Message); }
        return Results.Ok(new { refunded = true });
    });

    dev.MapPost("/refund-breed", (RefundBreedDevRequest request, HttpContext http, GameService game, IChainService chain) =>
    {
        var player = game.Authenticate(BearerToken(http));
        try { Sim(chain).RefundBreedEscrowFromPlayer(player.Id, request.BreedingId); }
        catch (InvalidOperationException ex) { throw new GameRuleException(ex.Message); }
        return Results.Ok(new { refunded = true });
    });

    dev.MapPost("/reclaim-deathmatch", (ReclaimDeathMatchDevRequest request, HttpContext http, GameService game, IChainService chain) =>
    {
        var player = game.Authenticate(BearerToken(http));
        try { Sim(chain).ReclaimDeathMatchFromPlayer(player.Id, request.DeathMatchId); }
        catch (InvalidOperationException ex) { throw new GameRuleException(ex.Message); }
        return Results.Ok(new { reclaimed = true });
    });

    // Marketplace: the seller deposits the item, a buyer fulfils, or the seller
    // reclaims — each stands in for the corresponding client-wallet covenant op.
    dev.MapPost("/fund-offer", (OfferDevRequest request, HttpContext http, GameService game, IChainService chain) =>
    {
        var player = game.Authenticate(BearerToken(http));
        try { Sim(chain).FundOfferFromSeller(player.Id, request.OfferId); }
        catch (InvalidOperationException ex) { throw new GameRuleException(ex.Message); }
        return Results.Ok(new { funded = true });
    });

    dev.MapPost("/fulfill-offer", (OfferDevRequest request, HttpContext http, GameService game, IChainService chain) =>
    {
        var player = game.Authenticate(BearerToken(http));
        try { Sim(chain).FulfillOfferFromBuyer(player.Id, request.OfferId); }
        catch (InvalidOperationException ex) { throw new GameRuleException(ex.Message); }
        return Results.Ok(new { fulfilled = true });
    });

    dev.MapPost("/reclaim-offer", (OfferDevRequest request, HttpContext http, GameService game, IChainService chain) =>
    {
        var player = game.Authenticate(BearerToken(http));
        try { Sim(chain).ReclaimOfferToSeller(player.Id, request.OfferId); }
        catch (InvalidOperationException ex) { throw new GameRuleException(ex.Message); }
        return Results.Ok(new { reclaimed = true });
    });
}

app.Run();

static InMemoryChainService Sim(IChainService chain) =>
    // The dev endpoints drive the simulator directly — paying an invoice the way a player's own wallet
    // would. Tests decorate IChainService to inject failures, and a decorator cannot be cast to the
    // simulator, so ask it for the one underneath before giving up.
    chain as InMemoryChainService
    ?? (chain as ISimulatedChain)?.Simulator
    ?? throw new InvalidOperationException("The dev endpoints need the in-memory chain simulator.");

static string? BearerToken(HttpContext http)
{
    var header = http.Request.Headers.Authorization.ToString();
    return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..] : null;
}

static SquadMatchDto ToSquadMatchDto(SquadMatchSession s) => new(
    s.Id, s.ChallengerLineup, s.DefenderLineup, s.WagerSats, s.Status,
    s.Result is { } r && s.ChallengerSnapshots is not null && s.DefenderSnapshots is not null
        ? r.ToDto(s.ChallengerSnapshots, s.DefenderSnapshots) : null);

static TournamentDto ToTournamentDto(TournamentSession t) => new(
    t.Id, t.OpenerPlayerId, t.BuyInSats, t.Size, t.Entrants.Count, t.Status,
    t.Entrants.Select(e => new TournamentEntrantDto(e.PlayerId, e.HeroId)).ToList(),
    t.Result?.ChampionId, t.Prizes.Count > 0 ? t.Prizes[0] : 0, t.EntrantsCommitmentHex);

// Status is DERIVED from the proposal's flags rather than stored, so it can't drift from them: completed
// wins over declined wins over accepted, matching the order the flow can only move through.
static StudProposalDto ToStudDto(StudProposal s) => new(
    s.Id, s.ProposerPlayerId, s.StudOwnerPlayerId, s.ProposerHeroId, s.StudHeroId, s.StudFeeSats,
    s.Completed ? "completed" : s.Declined ? "declined" : s.Accepted ? "accepted" : "proposed",
    s.ChildHeroId);

// Status comes off HeroBid.Status, which derives it from the flags rather than storing it, so the wire
// state can never drift from the state machine that gates the money.
static BidDto ToBidDto(HeroBid b) => new(
    b.Id, b.HeroId, b.BidderPlayerId, b.OwnerPlayerId, b.BidSats, b.FeeSats, b.Status,
    b.ReclaimAfterUnixSeconds);

static HeroTombstoneDto ToTombstoneDto(HeroTombstone t) => new(
    t.HeroId, t.Name, t.OwnerId, t.Generation, t.Level, t.GenomeHex, t.Reason, t.SessionId,
    t.ReplacedByHeroId, t.DestroyedAtUnixSeconds, t.ParentAId, t.ParentBId);

static MatchDto ToMatchDto(MatchSession session) => new(
    session.Id, session.ChallengerHeroId, session.DefenderHeroId,
    session.Status, session.CommitmentHex, session.Result?.ToDto(),
    session.WagerSats, session.DefenderPlayerId);

static OfferDto ToOfferDto(OfferListing o, GameStore store)
{
    var name = o.Kind == "hero"
        ? (store.Heroes.TryGetValue(o.HeroId ?? "", out var hero) ? hero.Name : o.HeroId ?? "hero")
        : (ArkadeHeroes.Core.Equipment.ItemCatalog.Find(o.ItemId)?.Name ?? o.ItemId);
    var rarityTier = o.Kind == "hero" && store.Heroes.TryGetValue(o.HeroId ?? "", out var h)
        ? ArkadeHeroes.Core.Progression.Rarity.Of(h.Genome).Tier.ToString()
        : null;
    return new OfferDto(o.Id, o.SellerId, o.ItemId, name,
        o.AskSats, o.OfferAddress, o.ItemAssetId, o.OfferValueSats, o.RefundAfterUnixSeconds, o.Status,
        o.Kind, o.HeroId, rarityTier);
}

/// <summary>Dev-only (InMemory mode): simulated client-wallet invoice payment.</summary>
public record PayInvoiceDevRequest(string InvoiceId);
public record FundTreasuryDevRequest(long Sats);

/// <summary>Dev-only (InMemory mode): simulated client-wallet asset transfer.</summary>
public record TransferAssetDevRequest(string AssetId, string ToPlayerId);

/// <summary>Dev-only (InMemory mode): simulated client-wallet escrow stake.</summary>
public record StakeEscrowDevRequest(string MatchId);

/// <summary>Dev-only (InMemory mode): simulated client-wallet timelocked refund reclaim.</summary>
public record RefundEscrowDevRequest(string MatchId);

/// <summary>Dev-only (InMemory mode): simulated client-wallet deposit of both parents + fee into a breed escrow.</summary>
public record FundBreedEscrowDevRequest(string BreedingId);

/// <summary>Dev-only (InMemory mode): simulated client-wallet timelocked breed refund reclaim.</summary>
public record RefundBreedDevRequest(string BreedingId);

/// <summary>Dev-only (InMemory mode): simulated client-wallet deposit of base + sacrifice + fee into the merge escrow.</summary>
public record FundMergeEscrowDevRequest(string MergeId);

/// <summary>Dev-only (InMemory mode): simulated client-wallet stake of the staker's hero into their death-match escrow.</summary>
public record FundDeathMatchEscrowDevRequest(string DeathMatchId, string Role);

/// <summary>Dev-only (InMemory mode): simulated client-wallet timelocked merge refund reclaim.</summary>
public record RefundMergeDevRequest(string MergeId);

/// <summary>Dev-only (InMemory mode): simulated timelocked per-side death-match reclaim.</summary>
public record ReclaimDeathMatchDevRequest(string DeathMatchId);

/// <summary>Dev-only (InMemory mode): simulated client-wallet offer op (deposit / fulfil / reclaim), keyed by offer.</summary>
public record OfferDevRequest(string OfferId);

/// <summary>Exposed for WebApplicationFactory-based integration tests.</summary>
public partial class Program;
