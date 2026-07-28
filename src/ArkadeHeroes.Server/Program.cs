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
    // Hero-progression flush: identity events save inline, grinding rides this loop. Registered as a
    // resolvable singleton too so a test can force a deterministic flush instead of racing the timer.
    builder.Services.AddSingleton<HeroFlushService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<HeroFlushService>());
}
else
{
    builder.Services.AddSingleton<IGameStatePersistence, NullGameStatePersistence>();
}

var app = builder.Build();

// Rehydrate before serving: a purchase paid before the restart must be claimable after it.
if (!string.IsNullOrWhiteSpace(stateDbPath))
{
    // Apply migrations, not EnsureCreated: this creates the DB + every table on a fresh path AND evolves an
    // already-created durable DB (new tables/columns) — EnsureCreated is create-once and silently skips
    // schema changes on an existing file, so a shipped entity (e.g. Heroes) would be missing in production.
    await app.Services.GetRequiredService<IDbContextFactory<GameStateDbContext>>()
        .CreateDbContext().Database.MigrateAsync();
    await app.Services.GetRequiredService<IGameStatePersistence>()
        .LoadIntoAsync(app.Services.GetRequiredService<GameStore>());
}

app.UseCors();

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
        request.Name, request.ArkadeAddress, request.LoginPubKeyHex, request.NonceHex, request.SignatureHex, ct);
    return Results.Ok(new PlayerDto(player.Id, player.Name, address, balance,
        player.StarterClaimed, player.Token));
});

// "Sign in with your wallet" — resume an existing player after a restore: fetch a
// single-use challenge, sign its digest with the wallet's login key, prove it here.
api.MapGet("/players/login-challenge", (GameService game) =>
    Results.Ok(new LoginChallengeResponse(game.IssueLoginChallenge())));

api.MapPost("/players/login", async (LoginRequest request, GameService game, IChainService chain, CancellationToken ct) =>
{
    var player = game.Login(request.LoginPubKeyHex, request.NonceHex, request.SignatureHex);
    var address = await chain.GetPlayerAddressAsync(player.Id, ct);
    var balance = await chain.GetAddressBalanceSatsAsync(player.Id, ct);
    return Results.Ok(new PlayerDto(player.Id, player.Name, address, balance, player.StarterClaimed, player.Token));
});

api.MapGet("/players/me", async (HttpContext http, GameService game, IChainService chain, CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    var address = await chain.GetPlayerAddressAsync(player.Id, ct);
    var balance = await chain.GetAddressBalanceSatsAsync(player.Id, ct);
    return Results.Ok(new PlayerDto(player.Id, player.Name, address, balance, player.StarterClaimed));
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
    return Results.Ok(new GauntletRunResponse(run.WavesCleared, waves, xp, receipt.LevelA, item, itemAssetId, snapshot, seed, entropy, receipt));
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

api.MapPost("/trials/open", (TrialsOpenRequest request, HttpContext http, GameService game) =>
{
    var player = game.Authenticate(BearerToken(http));
    var session = game.OpenTrials(player, request.HeroId);
    return Results.Ok(new TrialsOpenResponse(session.Id, session.CommitmentHex, session.Affix.ToString(),
        ArkadeHeroes.Core.Progression.Trials.AffixDescription(session.Affix)));
});

api.MapPost("/trials/{id}/run", (string id, TrialsRunRequest request, HttpContext http, GameService game) =>
{
    var player = game.Authenticate(BearerToken(http));
    var (run, snapshot, title, best, affix, seed, entropy, receipt) = game.RunTrials(player, id, request.Nonce);
    // Surface each wave's ghost snapshot + fight log so the browser can replay the wave in the arena. The
    // ghost is a pure function of the run entropy + the run's pinned affix, so this reconstructs exactly
    // what Trials.Resolve fought — no soft-foe substitution possible.
    var entropyBytes = Convert.FromHexString(entropy);
    var waves = run.Waves.Select(w => new TrialsWaveDto(
        w.Wave, w.GhostLevel, w.Won,
        ArkadeHeroes.Core.Progression.Trials.GhostFor(entropyBytes, w.Wave, affix).ToDto(),
        w.Result.ToDto())).ToList();
    return Results.Ok(new TrialsRunResponse(
        run.WavesCleared, waves, title, best, affix.ToString(), snapshot, seed, entropy, receipt));
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
    return Results.Ok(new DeathMatchSettleResponse(result, winner, loser, challSnap, defSnap, seed, entropy, receipt, minted, absorbed, newGenome, newHero));
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
            s.EntropyHex ?? "", s.Nonce ?? ""))
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
        challengerSnapshot, defenderSnapshot, session.WagerSats, winnerPayout, receipt));
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
            s.EntropyHex ?? "", s.Nonce ?? ""))
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
            s.CommitmentHex, Convert.ToHexString(s.ServerSeed).ToLowerInvariant(), s.EntropyHex ?? "", s.Nonce ?? ""))
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
            t.EntropyHex ?? "", t.Nonce ?? "", t.EntrantsCommitmentHex))
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

api.MapPost("/heroes/{heroId}/unequip", (string heroId, UnequipRequest request, HttpContext http, GameService game) =>
{
    var player = game.Authenticate(BearerToken(http));
    var hero = game.Unequip(player, heroId, request.Slot);
    return Results.Ok(new EquipResponse(hero.ToDto()));
});

// ── Marketplace: resting item offers (covenant-enforced, buyer-funded) ─────

api.MapPost("/offers", async (CreateOfferRequest request, HttpContext http, GameService game, CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    var (_, info, fee) = await game.CreateOfferAsync(player, request.ItemId, request.AskSats, ct);
    return Results.Ok(new CreateOfferResponse(info.OfferId, info.OfferAddress, info.ItemAssetId,
        info.AskSats, info.OfferValueSats, info.RefundAfterUnixSeconds, fee?.AmountSats ?? 0, fee?.ToDto()));
});

// Hero sales reuse the same offer covenant (a hero is a unique asset).
api.MapPost("/offers/hero", async (CreateHeroOfferRequest request, HttpContext http, GameService game, CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    var (_, info, fee) = await game.CreateHeroOfferAsync(player, request.HeroId, request.AskSats, ct);
    return Results.Ok(new CreateOfferResponse(info.OfferId, info.OfferAddress, info.ItemAssetId,
        info.AskSats, info.OfferValueSats, info.RefundAfterUnixSeconds, fee?.AmountSats ?? 0, fee?.ToDto()));
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
        ((InMemoryChainService)chain).PayInvoiceFromPlayer(player.Id, request.InvoiceId);
        return Results.Ok(new { paid = true });
    });

    // Credit the simulated treasury so the daily faucet has reserves to pay out (a fresh treasury
    // has no fee income yet). In NArk the treasury is a real funded address, so this doesn't exist.
    dev.MapPost("/fund-treasury", (FundTreasuryDevRequest request, IChainService chain) =>
    {
        ((InMemoryChainService)chain).FundTreasury(request.Sats);
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
        ((InMemoryChainService)chain).TransferAssetFromPlayer(player.Id, request.ToPlayerId, request.AssetId);
        return Results.Ok(new { transferred = true });
    });

    dev.MapPost("/stake-escrow", (StakeEscrowDevRequest request, HttpContext http, GameService game, IChainService chain) =>
    {
        var player = game.Authenticate(BearerToken(http));
        ((InMemoryChainService)chain).StakeEscrowFromPlayer(player.Id, request.MatchId);
        return Results.Ok(new { staked = true });
    });

    dev.MapPost("/refund-escrow", (RefundEscrowDevRequest request, HttpContext http, GameService game, IChainService chain) =>
    {
        var player = game.Authenticate(BearerToken(http));
        try
        {
            ((InMemoryChainService)chain).RefundEscrowFromPlayer(player.Id, request.MatchId);
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
        try { ((InMemoryChainService)chain).FundBreedEscrowFromPlayer(player.Id, request.BreedingId); }
        catch (InvalidOperationException ex) { throw new GameRuleException(ex.Message); }
        return Results.Ok(new { funded = true });
    });

    dev.MapPost("/fund-merge-escrow", (FundMergeEscrowDevRequest request, HttpContext http, GameService game, IChainService chain) =>
    {
        var player = game.Authenticate(BearerToken(http));
        try { ((InMemoryChainService)chain).FundMergeEscrowFromPlayer(player.Id, request.MergeId); }
        catch (InvalidOperationException ex) { throw new GameRuleException(ex.Message); }
        return Results.Ok(new { funded = true });
    });

    dev.MapPost("/fund-deathmatch-escrow", (FundDeathMatchEscrowDevRequest request, HttpContext http, GameService game, IChainService chain) =>
    {
        var player = game.Authenticate(BearerToken(http));
        try { ((InMemoryChainService)chain).FundDeathMatchEscrowFromPlayer(player.Id, request.DeathMatchId, request.Role); }
        catch (InvalidOperationException ex) { throw new GameRuleException(ex.Message); }
        return Results.Ok(new { funded = true });
    });

    dev.MapPost("/refund-merge", (RefundMergeDevRequest request, HttpContext http, GameService game, IChainService chain) =>
    {
        var player = game.Authenticate(BearerToken(http));
        try { ((InMemoryChainService)chain).RefundMergeEscrowFromPlayer(player.Id, request.MergeId); }
        catch (InvalidOperationException ex) { throw new GameRuleException(ex.Message); }
        return Results.Ok(new { refunded = true });
    });

    dev.MapPost("/refund-breed", (RefundBreedDevRequest request, HttpContext http, GameService game, IChainService chain) =>
    {
        var player = game.Authenticate(BearerToken(http));
        try { ((InMemoryChainService)chain).RefundBreedEscrowFromPlayer(player.Id, request.BreedingId); }
        catch (InvalidOperationException ex) { throw new GameRuleException(ex.Message); }
        return Results.Ok(new { refunded = true });
    });

    dev.MapPost("/reclaim-deathmatch", (ReclaimDeathMatchDevRequest request, HttpContext http, GameService game, IChainService chain) =>
    {
        var player = game.Authenticate(BearerToken(http));
        try { ((InMemoryChainService)chain).ReclaimDeathMatchFromPlayer(player.Id, request.DeathMatchId); }
        catch (InvalidOperationException ex) { throw new GameRuleException(ex.Message); }
        return Results.Ok(new { reclaimed = true });
    });

    // Marketplace: the seller deposits the item, a buyer fulfils, or the seller
    // reclaims — each stands in for the corresponding client-wallet covenant op.
    dev.MapPost("/fund-offer", (OfferDevRequest request, HttpContext http, GameService game, IChainService chain) =>
    {
        var player = game.Authenticate(BearerToken(http));
        try { ((InMemoryChainService)chain).FundOfferFromSeller(player.Id, request.OfferId); }
        catch (InvalidOperationException ex) { throw new GameRuleException(ex.Message); }
        return Results.Ok(new { funded = true });
    });

    dev.MapPost("/fulfill-offer", (OfferDevRequest request, HttpContext http, GameService game, IChainService chain) =>
    {
        var player = game.Authenticate(BearerToken(http));
        try { ((InMemoryChainService)chain).FulfillOfferFromBuyer(player.Id, request.OfferId); }
        catch (InvalidOperationException ex) { throw new GameRuleException(ex.Message); }
        return Results.Ok(new { fulfilled = true });
    });

    dev.MapPost("/reclaim-offer", (OfferDevRequest request, HttpContext http, GameService game, IChainService chain) =>
    {
        var player = game.Authenticate(BearerToken(http));
        try { ((InMemoryChainService)chain).ReclaimOfferToSeller(player.Id, request.OfferId); }
        catch (InvalidOperationException ex) { throw new GameRuleException(ex.Message); }
        return Results.Ok(new { reclaimed = true });
    });
}

app.Run();

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
