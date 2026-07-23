using Microsoft.EntityFrameworkCore;

namespace ArkadeHeroes.Server.Persistence;

/// <summary>
/// The durability seam for game state a restart must not lose. Persistence is strictly OPT-IN
/// (<c>Game:StateDbPath</c>): with no path configured the null implementation is registered and the server
/// behaves exactly as it always has — everything in memory, gone on restart.
/// </summary>
public interface IGameStatePersistence
{
    /// <summary>Rehydrate the store at boot. Called once, before the server serves traffic.</summary>
    Task LoadIntoAsync(GameStore store, CancellationToken ct = default);

    /// <summary>Durably record a purchase's current state (created, or delivered).</summary>
    Task SaveItemPurchaseAsync(ItemPurchase purchase, CancellationToken ct = default);

    /// <summary>Durably record a tournament — called whenever an entrant (and so a buy-in invoice) is added,
    /// and again the moment it is marked resolved.</summary>
    Task SaveTournamentAsync(TournamentSession session, CancellationToken ct = default);

    /// <summary>Durably record a player's identity and the once-only flags attached to it (starter claimed,
    /// day already claimed) — called at registration and whenever one of those flags moves.</summary>
    Task SavePlayerAsync(Player player, CancellationToken ct = default);

    /// <summary>Durably record a Fancy find — a hero's set, edition and finder — so "first to breed this set,
    /// forever" survives a restart. Called once per stamped hero; the row is append-only.</summary>
    Task SaveFancyFindAsync(FancyFind find, CancellationToken ct = default);
}

/// <summary>No durability — the historical behaviour, where all state lives and dies with the process.</summary>
public sealed class NullGameStatePersistence : IGameStatePersistence
{
    public Task LoadIntoAsync(GameStore store, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveItemPurchaseAsync(ItemPurchase purchase, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveTournamentAsync(TournamentSession session, CancellationToken ct = default) => Task.CompletedTask;
    public Task SavePlayerAsync(Player player, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveFancyFindAsync(FancyFind find, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// SQLite-backed durability. Uses a context FACTORY because its consumers (GameService, the store) are
/// singletons, and a pooled DbContext is not safe to share across concurrent requests.
/// </summary>
public sealed class SqliteGameStatePersistence(IDbContextFactory<GameStateDbContext> factory) : IGameStatePersistence
{
    /// <summary>"delivering" is a TRANSIENT, in-flight state. Persisting it would mean a server that died
    /// mid-delivery reloads the purchase as permanently undeliverable, so it always durably reads as
    /// pending — the paid purchase stays claimable, which is the whole point of persisting it.</summary>
    private static string Durable(string status) => status == "claimed" ? "claimed" : "pending";

    /// <summary>One entrant, as stored inside the tournament row's JSON blob.</summary>
    private sealed record PersistedEntrant(string PlayerId, string HeroId, string BuyInInvoiceId);

    public async Task LoadIntoAsync(GameStore store, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        // Identity first — the purchases and brackets below reference these player ids, and without them
        // a returning player couldn't claim what they'd already paid for.
        foreach (var row in await db.Players.AsNoTracking().ToListAsync(ct))
        {
            var player = new Player
            {
                Id = row.Id,
                Name = row.Name,
                // A FRESH session token: the old one died with the process, and the wallet re-authenticates
                // by signing a login challenge. Never store bearer credentials at rest.
                Token = Guid.NewGuid().ToString("N"),
                StarterClaimed = row.StarterClaimed,
                LoginPubKeyHex = row.LoginPubKeyHex,
                StreakCount = row.StreakCount,
                LastClaimDay = row.LastClaimDay,
            };
            store.Players[player.Id] = player;
            store.PlayersByToken[player.Token] = player;
        }

        // Resolved and refunded brackets are NEVER rehydrated — both are TERMINAL. Their rows survive as
        // audit markers, but putting one back into the live store would let it settle a SECOND time —
        // paying the podium (or every buy-in) twice out of a treasury that can't print. Unsettled brackets
        // are exactly the ones holding paid buy-ins.
        foreach (var row in await db.Tournaments.AsNoTracking().Where(t => t.Status != "resolved" && t.Status != "refunded").ToListAsync(ct))
        {
            var session = new TournamentSession
            {
                Id = row.Id,
                OpenerPlayerId = row.OpenerPlayerId,
                BuyInSats = row.BuyInSats,
                Size = row.Size,
                ServerSeed = row.ServerSeed,
                CommitmentHex = row.CommitmentHex,
                Status = row.Status,
            };
            foreach (var e in System.Text.Json.JsonSerializer.Deserialize<List<PersistedEntrant>>(row.EntrantsJson) ?? [])
                session.Entrants.Add(new TournamentEntrant
                {
                    PlayerId = e.PlayerId, HeroId = e.HeroId, BuyInInvoiceId = e.BuyInInvoiceId,
                });
            store.Tournaments[session.Id] = session;
        }

        foreach (var row in await db.ItemPurchases.AsNoTracking().ToListAsync(ct))
        {
            store.ItemPurchases[row.InvoiceId] = new ItemPurchase
            {
                InvoiceId = row.InvoiceId,
                PlayerId = row.PlayerId,
                ItemId = row.ItemId,
                Status = Durable(row.Status),
                ItemAssetId = row.ItemAssetId,
                DeliveryTxId = row.DeliveryTxId,
            };
        }

        // Fancy finds rebuild the discovery board, the per-hero editions, and — load-bearing — the per-set
        // count, so the next live find of a set takes the right number instead of a second "#1".
        foreach (var row in await db.FancyFinds.AsNoTracking().ToListAsync(ct))
            store.LoadFancyFind(new FancyFind(row.Title, row.HeroId, row.HeroName, row.OwnerId, row.UnixSeconds, row.Edition));
    }

    public async Task SaveItemPurchaseAsync(ItemPurchase purchase, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.ItemPurchases.FindAsync([purchase.InvoiceId], ct);
        if (row is null)
        {
            db.ItemPurchases.Add(new PersistedItemPurchase
            {
                InvoiceId = purchase.InvoiceId,
                PlayerId = purchase.PlayerId,
                ItemId = purchase.ItemId,
                Status = Durable(purchase.Status),
                ItemAssetId = purchase.ItemAssetId,
                DeliveryTxId = purchase.DeliveryTxId,
            });
        }
        else
        {
            row.Status = Durable(purchase.Status);
            row.ItemAssetId = purchase.ItemAssetId;
            row.DeliveryTxId = purchase.DeliveryTxId;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task SaveTournamentAsync(TournamentSession session, CancellationToken ct = default)
    {
        var entrantsJson = System.Text.Json.JsonSerializer.Serialize(
            session.Entrants.Select(e => new PersistedEntrant(e.PlayerId, e.HeroId, e.BuyInInvoiceId)));

        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.Tournaments.FindAsync([session.Id], ct);
        if (row is null)
        {
            db.Tournaments.Add(new PersistedTournament
            {
                Id = session.Id,
                OpenerPlayerId = session.OpenerPlayerId,
                BuyInSats = session.BuyInSats,
                Size = session.Size,
                ServerSeed = session.ServerSeed,
                CommitmentHex = session.CommitmentHex,
                Status = session.Status,
                EntrantsJson = entrantsJson,
            });
        }
        else
        {
            row.Status = session.Status;
            row.EntrantsJson = entrantsJson;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task SaveFancyFindAsync(FancyFind find, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        // Assigned-once and never renumbered: if this hero is already stored, there is nothing to update.
        if (await db.FancyFinds.FindAsync([find.HeroId], ct) is not null) return;
        db.FancyFinds.Add(new PersistedFancyFind
        {
            HeroId = find.HeroId,
            Title = find.Title,
            HeroName = find.HeroName,
            OwnerId = find.OwnerId,
            UnixSeconds = find.UnixSeconds,
            Edition = find.Edition,
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task SavePlayerAsync(Player player, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.Players.FindAsync([player.Id], ct);
        if (row is null)
        {
            db.Players.Add(new PersistedPlayer
            {
                Id = player.Id,
                Name = player.Name,
                StarterClaimed = player.StarterClaimed,
                LoginPubKeyHex = player.LoginPubKeyHex,
                StreakCount = player.StreakCount,
                LastClaimDay = player.LastClaimDay,
            });
        }
        else
        {
            row.Name = player.Name;
            row.StarterClaimed = player.StarterClaimed;
            row.LoginPubKeyHex = player.LoginPubKeyHex;
            row.StreakCount = player.StreakCount;
            row.LastClaimDay = player.LastClaimDay;
        }
        await db.SaveChangesAsync(ct);
    }
}
