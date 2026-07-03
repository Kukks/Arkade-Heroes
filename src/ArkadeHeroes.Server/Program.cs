using ArkadeHeroes.Chain;
using ArkadeHeroes.Chain.NArk;
using ArkadeHeroes.Core.Equipment;
using ArkadeHeroes.Server;
using ArkadeHeroes.Shared;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<GameOptions>(builder.Configuration.GetSection(GameOptions.SectionName));
builder.Services.AddSingleton<GameStore>();
builder.Services.AddSingleton<GameService>();

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

var app = builder.Build();

// GameRuleException → 400 with a readable message.
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
});

var api = app.MapGroup("/api");

// ── Players ────────────────────────────────────────────────────────────────

api.MapPost("/players", async (RegisterPlayerRequest request, GameService game, CancellationToken ct) =>
{
    var (player, wallet, balance) = await game.RegisterPlayerAsync(request.Name, ct);
    return Results.Ok(new PlayerDto(player.Id, player.Name, wallet.ArkadeAddress, balance,
        player.StarterClaimed, player.Token));
});

api.MapGet("/players/me", async (HttpContext http, GameService game, IChainService chain, CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    var wallet = await chain.GetOrCreatePlayerWalletAsync(player.Id, ct);
    var balance = await chain.GetBalanceSatsAsync(player.Id, ct);
    return Results.Ok(new PlayerDto(player.Id, player.Name, wallet.ArkadeAddress, balance, player.StarterClaimed));
});

// ── Heroes ─────────────────────────────────────────────────────────────────

api.MapPost("/heroes/starter", async (HttpContext http, GameService game, CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    var heroes = await game.ClaimStartersAsync(player, ct);
    return Results.Ok(new StarterResponse(heroes.Select(h => h.ToDto()).ToList()));
});

api.MapGet("/heroes", (string? owner, GameStore store) =>
{
    var heroes = store.Heroes.Values
        .Where(h => owner is null || h.OwnerId == owner)
        .OrderBy(h => h.Name)
        .Select(h => h.ToDto())
        .ToList();
    return Results.Ok(heroes);
});

api.MapGet("/heroes/mine", (HttpContext http, GameService game, GameStore store) =>
{
    var player = game.Authenticate(BearerToken(http));
    return Results.Ok(store.Heroes.Values
        .Where(h => h.OwnerId == player.Id)
        .OrderBy(h => h.Name)
        .Select(h => h.ToDto())
        .ToList());
});

api.MapGet("/heroes/{heroId}", (string heroId, GameService game) =>
    Results.Ok(game.GetHero(heroId).ToDto()));

// ── Breeding (commit → reveal) ─────────────────────────────────────────────

api.MapPost("/breeding/commit", async (BreedCommitRequest request, HttpContext http, GameService game, CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    var (session, fee) = await game.CommitBreedingAsync(player, request.ParentAId, request.ParentBId, ct);
    return Results.Ok(new BreedCommitResponse(session.Id, session.CommitmentHex, fee));
});

api.MapPost("/breeding/{breedingId}/reveal", async (string breedingId, BreedRevealRequest request, HttpContext http, GameService game, CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    var (child, serverSeedHex, entropyHex) = await game.RevealBreedingAsync(player, breedingId, request.Nonce, ct);
    return Results.Ok(new BreedRevealResponse(child.ToDto(), serverSeedHex, entropyHex, "paid-at-commit"));
});

// ── Matches (open → fight) ─────────────────────────────────────────────────

api.MapPost("/matches/open", async (OpenMatchRequest request, HttpContext http, GameService game, CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    var session = await game.OpenMatchAsync(player, request.ChallengerHeroId, request.DefenderHeroId,
        request.WagerSats, ct);
    return Results.Ok(new OpenMatchResponse(session.Id, session.CommitmentHex, session.WagerSats, session.Status));
});

api.MapPost("/matches/{matchId}/accept", async (string matchId, HttpContext http, GameService game, CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    var session = await game.AcceptMatchAsync(player, matchId, ct);
    return Results.Ok(ToMatchDto(session));
});

api.MapPost("/matches/{matchId}/fight", async (string matchId, FightRequest request, HttpContext http, GameService game, CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    var (session, result, serverSeedHex, entropyHex, challengerXp, defenderXp,
            challengerSnapshot, defenderSnapshot, winnerPayout) =
        await game.FightAsync(player, matchId, request.Nonce, ct);
    var challenger = game.GetHero(session.ChallengerHeroId);
    var defender = game.GetHero(session.DefenderHeroId);
    return Results.Ok(new FightResponse(result.ToDto(), serverSeedHex, entropyHex,
        challengerXp, defenderXp, challenger.ToDto(), defender.ToDto(),
        challengerSnapshot, defenderSnapshot, session.WagerSats, winnerPayout));
});

api.MapGet("/matches", (string? status, GameStore store) =>
    Results.Ok(store.Matches.Values
        .Where(m => status is null || m.Status == status)
        .OrderByDescending(m => m.CreatedAt)
        .Take(50)
        .Select(ToMatchDto)
        .ToList()));

api.MapGet("/matches/{matchId}", (string matchId, GameStore store) =>
    store.Matches.TryGetValue(matchId, out var session)
        ? Results.Ok(ToMatchDto(session))
        : Results.NotFound());

// ── Hero transfer ──────────────────────────────────────────────────────────

api.MapPost("/heroes/{heroId}/transfer", async (string heroId, TransferRequest request, HttpContext http, GameService game, CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    var (hero, arkTxId) = await game.TransferHeroAsync(player, heroId, request.ToPlayerId, ct);
    return Results.Ok(new TransferResponse(hero.ToDto(), arkTxId));
});

// ── Items ──────────────────────────────────────────────────────────────────

api.MapGet("/items", () => Results.Ok(ItemCatalog.All.Select(i => i.ToDto()).ToList()));

api.MapPost("/heroes/{heroId}/equip", async (string heroId, EquipRequest request, HttpContext http, GameService game, CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    var (hero, balance, paymentRef) = await game.BuyAndEquipAsync(player, heroId, request.ItemId, ct);
    return Results.Ok(new EquipResponse(hero.ToDto(), balance, paymentRef));
});

// ── Chain / health ─────────────────────────────────────────────────────────

api.MapGet("/chain/info", async (IChainService chain, CancellationToken ct) =>
{
    var info = await chain.GetInfoAsync(ct);
    return Results.Ok(new ChainInfoDto(info.Mode, info.Network, info.TreasuryAddress, info.SpeciesAssetId));
});

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.Run();

static string? BearerToken(HttpContext http)
{
    var header = http.Request.Headers.Authorization.ToString();
    return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..] : null;
}

static MatchDto ToMatchDto(MatchSession session) => new(
    session.Id, session.ChallengerHeroId, session.DefenderHeroId,
    session.Status, session.CommitmentHex, session.Result?.ToDto(),
    session.WagerSats, session.DefenderPlayerId);

/// <summary>Exposed for WebApplicationFactory-based integration tests.</summary>
public partial class Program;
