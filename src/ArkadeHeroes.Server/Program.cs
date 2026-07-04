using ArkadeHeroes.Chain;
using ArkadeHeroes.Chain.NArk;
using ArkadeHeroes.Core.Equipment;
using ArkadeHeroes.Server;
using ArkadeHeroes.Shared;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<GameOptions>(builder.Configuration.GetSection(GameOptions.SectionName));
builder.Services.AddSingleton<GameStore>();
builder.Services.AddSingleton<ReceiptSigner>();
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
    var (player, address, balance) = await game.RegisterPlayerAsync(request.Name, request.ArkadeAddress, ct);
    return Results.Ok(new PlayerDto(player.Id, player.Name, address, balance,
        player.StarterClaimed, player.Token));
});

api.MapGet("/players/me", async (HttpContext http, GameService game, IChainService chain, CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    var address = await chain.GetPlayerAddressAsync(player.Id, ct);
    var balance = await chain.GetAddressBalanceSatsAsync(player.Id, ct);
    return Results.Ok(new PlayerDto(player.Id, player.Name, address, balance, player.StarterClaimed));
});

// Public profile: players are addresses, and addresses are public — this is
// what a sender needs to transfer a hero to another player wallet-to-wallet.
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
    var (session, invoice) = await game.CommitBreedingAsync(player, request.ParentAId, request.ParentBId, ct);
    return Results.Ok(new BreedCommitResponse(session.Id, session.CommitmentHex, invoice.ToDto()));
});

api.MapPost("/breeding/{breedingId}/reveal", async (string breedingId, BreedRevealRequest request, HttpContext http, GameService game, CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    var (child, serverSeedHex, entropyHex, receipt) = await game.RevealBreedingAsync(player, breedingId, request.Nonce, ct);
    return Results.Ok(new BreedRevealResponse(child.ToDto(), serverSeedHex, entropyHex, "paid-at-commit", receipt));
});

// ── Matches (open → fight) ─────────────────────────────────────────────────

api.MapPost("/matches/open", async (OpenMatchRequest request, HttpContext http, GameService game, CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    var (session, invoice) = await game.OpenMatchAsync(player, request.ChallengerHeroId, request.DefenderHeroId,
        request.WagerSats, request.Mode, ct);
    // The challenger stakes into THEIR escrow address.
    return Results.Ok(new OpenMatchResponse(session.Id, session.CommitmentHex, session.WagerSats, session.Status,
        invoice?.ToDto(), session.EscrowChallengerAddress, session.EscrowChallengerAddress is null ? 0 : session.WagerSats));
});

api.MapPost("/matches/{matchId}/accept", async (string matchId, HttpContext http, GameService game, CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    var (session, invoice) = await game.AcceptMatchAsync(player, matchId, ct);
    // The defender stakes into THEIR escrow address.
    return Results.Ok(new AcceptMatchResponse(ToMatchDto(session), invoice?.ToDto(),
        session.EscrowDefenderAddress, session.EscrowDefenderAddress is null ? 0 : session.WagerSats));
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

// ── Hero transfer: the owner's wallet moves the asset; this confirms ───────

api.MapPost("/heroes/{heroId}/transfer", async (string heroId, TransferRequest request, HttpContext http, GameService game, CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    var hero = await game.ConfirmTransferAsync(player, heroId, request.ToPlayerId, ct);
    return Results.Ok(new TransferResponse(hero.ToDto()));
});

// ── Items ──────────────────────────────────────────────────────────────────

api.MapGet("/items", () => Results.Ok(ItemCatalog.All.Select(i => i.ToDto()).ToList()));

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

// ── Chain / health ─────────────────────────────────────────────────────────

api.MapGet("/chain/info", async (IChainService chain, ReceiptSigner receipts, CancellationToken ct) =>
{
    var info = await chain.GetInfoAsync(ct);
    return Results.Ok(new ChainInfoDto(info.Mode, info.Network, info.TreasuryAddress, info.SpeciesAssetId,
        info.EmulatorSignerKey, receipts.PublicKeyHex));
});

// Receipts are signed public facts — anyone can pull a hero's chain and
// recompute its progression (the server DB is just a cache of this).
api.MapGet("/receipts/hero/{heroId}", (string heroId, GameStore store) =>
    Results.Ok(store.ReceiptsByHero.TryGetValue(heroId, out var list)
        ? list.ToArray()
        : Array.Empty<ProgressionReceiptDto>()));

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
}

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

/// <summary>Dev-only (InMemory mode): simulated client-wallet invoice payment.</summary>
public record PayInvoiceDevRequest(string InvoiceId);

/// <summary>Dev-only (InMemory mode): simulated client-wallet asset transfer.</summary>
public record TransferAssetDevRequest(string AssetId, string ToPlayerId);

/// <summary>Dev-only (InMemory mode): simulated client-wallet escrow stake.</summary>
public record StakeEscrowDevRequest(string MatchId);

/// <summary>Exposed for WebApplicationFactory-based integration tests.</summary>
public partial class Program;
