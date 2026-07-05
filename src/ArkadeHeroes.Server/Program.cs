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

// ── Death-match (open → both stake a hero → settle; loser's hero burns) ─────

api.MapPost("/deathmatch/open", async (DeathMatchOpenRequest request, HttpContext http, GameService game, CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    var (session, escrow, favor) = await game.OpenDeathMatchAsync(player, request.ChallengerHeroId, request.DefenderHeroId, ct);
    return Results.Ok(new DeathMatchOpenResponse(session.Id, session.CommitmentHex, escrow, favor));
});

api.MapPost("/deathmatch/{id}/accept", async (string id, HttpContext http, GameService game, CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    var (_, escrow, defender) = await game.AcceptDeathMatchAsync(player, id, ct);
    return Results.Ok(new DeathMatchAcceptResponse(escrow, defender.ToDto()));
});

api.MapPost("/deathmatch/{id}/settle", async (string id, DeathMatchSettleRequest request, HttpContext http, GameService game, CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    var (result, winner, loser, challSnap, defSnap, seed, entropy, receipt) = await game.SettleDeathMatchAsync(player, id, request.Nonce, ct);
    return Results.Ok(new DeathMatchSettleResponse(result, winner, loser, challSnap, defSnap, seed, entropy, receipt));
});

api.MapGet("/deathmatch/{id}/escrow/{role}", async (string id, string role, IChainService chain, CancellationToken ct) =>
    await chain.GetDeathMatchEscrowParamsAsync(id, role, ct) is { } p ? Results.Ok(p) : Results.NotFound());

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
        .Take(20)
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

// ── Marketplace: resting item offers (covenant-enforced, buyer-funded) ─────

api.MapPost("/offers", async (CreateOfferRequest request, HttpContext http, GameService game, CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    var (_, info) = await game.CreateOfferAsync(player, request.ItemId, request.AskSats, ct);
    return Results.Ok(new CreateOfferResponse(info.OfferId, info.OfferAddress, info.ItemAssetId,
        info.AskSats, info.OfferValueSats, info.RefundAfterUnixSeconds));
});

// Hero sales reuse the same offer covenant (a hero is a unique asset).
api.MapPost("/offers/hero", async (CreateHeroOfferRequest request, HttpContext http, GameService game, CancellationToken ct) =>
{
    var player = game.Authenticate(BearerToken(http));
    var (_, info) = await game.CreateHeroOfferAsync(player, request.HeroId, request.AskSats, ct);
    return Results.Ok(new CreateOfferResponse(info.OfferId, info.OfferAddress, info.ItemAssetId,
        info.AskSats, info.OfferValueSats, info.RefundAfterUnixSeconds));
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

api.MapGet("/chain/info", async (IChainService chain, ReceiptSigner receipts, IConfiguration config, CancellationToken ct) =>
{
    var info = await chain.GetInfoAsync(ct);
    // Advertised so clients can run covenant refunds without out-of-band
    // config; defaults mirror NArkChainOptions. Meaningless in InMemory mode.
    var isNArk = info.Mode.Equals("NArk", StringComparison.OrdinalIgnoreCase);
    return Results.Ok(new ChainInfoDto(info.Mode, info.Network, info.TreasuryAddress, info.SpeciesAssetId,
        info.EmulatorSignerKey, receipts.PublicKeyHex,
        isNArk ? config["Chain:NArk:EmulatorUri"] ?? "http://localhost:7073" : null,
        isNArk ? config["Chain:NArk:EsploraUri"] ?? "http://localhost:3000/api" : null));
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

/// <summary>Dev-only (InMemory mode): simulated client-wallet asset transfer.</summary>
public record TransferAssetDevRequest(string AssetId, string ToPlayerId);

/// <summary>Dev-only (InMemory mode): simulated client-wallet escrow stake.</summary>
public record StakeEscrowDevRequest(string MatchId);

/// <summary>Dev-only (InMemory mode): simulated client-wallet timelocked refund reclaim.</summary>
public record RefundEscrowDevRequest(string MatchId);

/// <summary>Dev-only (InMemory mode): simulated client-wallet deposit of both parents + fee into a breed escrow.</summary>
public record FundBreedEscrowDevRequest(string BreedingId);

/// <summary>Dev-only (InMemory mode): simulated client-wallet deposit of base + sacrifice + fee into the merge escrow.</summary>
public record FundMergeEscrowDevRequest(string MergeId);

/// <summary>Dev-only (InMemory mode): simulated client-wallet stake of the staker's hero into their death-match escrow.</summary>
public record FundDeathMatchEscrowDevRequest(string DeathMatchId, string Role);

/// <summary>Dev-only (InMemory mode): simulated client-wallet offer op (deposit / fulfil / reclaim), keyed by offer.</summary>
public record OfferDevRequest(string OfferId);

/// <summary>Exposed for WebApplicationFactory-based integration tests.</summary>
public partial class Program;
