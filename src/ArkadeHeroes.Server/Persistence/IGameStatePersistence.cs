using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Heroes;
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

    /// <summary>Durably record a hero's current state — the FULL surface. Awaited inline at every IDENTITY
    /// event — mint, burn's surviving output, transfer, rename — so a hero can never vanish or mis-own
    /// across a restart.</summary>
    Task SaveHeroAsync(Hero hero, CancellationToken ct = default);

    /// <summary>Durably record ONLY a hero's PROGRESSION (level/XP, equipment, cooldowns, breed count) —
    /// the periodic flush's save path, which accepts losing at most one flush window. Identity columns are
    /// deliberately NEVER written here: the flush snapshots a live hero it does not lock, so a full-surface
    /// flush write racing the inline saves above could commit LAST and revert an ownership or name change
    /// that was already durable — a hero mis-owned across a restart. A write that never carries identity
    /// cannot, whatever the interleaving.</summary>
    Task SaveHeroProgressionAsync(Hero hero, CancellationToken ct = default);

    /// <summary>Durably erase a burned hero (a merge's inputs, a death-match loser) right where it leaves the
    /// live store — a rehydrated ghost would be a fightable, listable hero whose on-chain asset is retired.</summary>
    Task DeleteHeroAsync(string heroId, CancellationToken ct = default);
}

/// <summary>No durability — the historical behaviour, where all state lives and dies with the process.</summary>
public sealed class NullGameStatePersistence : IGameStatePersistence
{
    public Task LoadIntoAsync(GameStore store, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveItemPurchaseAsync(ItemPurchase purchase, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveTournamentAsync(TournamentSession session, CancellationToken ct = default) => Task.CompletedTask;
    public Task SavePlayerAsync(Player player, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveFancyFindAsync(FancyFind find, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveHeroAsync(Hero hero, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveHeroProgressionAsync(Hero hero, CancellationToken ct = default) => Task.CompletedTask;
    public Task DeleteHeroAsync(string heroId, CancellationToken ct = default) => Task.CompletedTask;
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

        // Heroes next — the brackets below name entrant hero ids, and the tournament strand gate reads the
        // live roster, so heroes must be in place before any session that references one. Identity is exact;
        // progression rehydrates at its last flushed value (the bounded loss the hybrid save accepts).
        foreach (var row in await db.Heroes.AsNoTracking().ToListAsync(ct))
        {
            var hero = new Hero
            {
                Id = row.Id,
                OwnerId = row.OwnerId,
                Name = row.Name,
                Genome = Genome.FromHex(row.GenomeHex),
                Generation = row.Generation,
                ParentAId = row.ParentAId,
                ParentBId = row.ParentBId,
                Level = row.Level,
                Xp = row.Xp,
                BreedCount = row.BreedCount,
                BreedCooldownUntil = row.BreedCooldownUntil,
                GauntletCooldownUntil = row.GauntletCooldownUntil,
                EntropyHex = row.EntropyHex,
                ServerSeedHex = row.ServerSeedHex,
                PlayerNonce = row.PlayerNonce,
                AssetId = row.AssetId,
                MintArkTxId = row.MintArkTxId,
            };
            // Rebuild the loadout from the stored item ids — each catalog item knows its slot. An id the
            // catalog no longer carries is skipped, exactly as EquipmentLoadout.ResolveItems treats it live.
            foreach (var itemId in System.Text.Json.JsonSerializer.Deserialize<List<string>>(row.EquipmentJson) ?? [])
                if (Core.Equipment.ItemCatalog.Find(itemId) is { } item)
                    hero.Equipment.Equip(item);
            store.Heroes[hero.Id] = hero;
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

    /// <summary>The loadout as a SORTED JSON array of equipped item ids — sorted so the same loadout always
    /// serializes to the same bytes regardless of equip order (dictionary enumeration is unordered).</summary>
    private static string EquipmentJson(Hero hero) => System.Text.Json.JsonSerializer.Serialize(
        hero.Equipment.Slots.Values.OrderBy(id => id, StringComparer.Ordinal));

    /// <summary>A fresh row carrying the hero's FULL surface — used by the insert side of BOTH save paths,
    /// so a field added to the schema can't reach one and silently miss the other.</summary>
    private static PersistedHero NewRow(Hero hero) => new()
    {
        Id = hero.Id,
        OwnerId = hero.OwnerId,
        Name = hero.Name,
        GenomeHex = hero.Genome.ToHex(),
        Generation = hero.Generation,
        ParentAId = hero.ParentAId,
        ParentBId = hero.ParentBId,
        Level = hero.Level,
        Xp = hero.Xp,
        BreedCount = hero.BreedCount,
        BreedCooldownUntil = hero.BreedCooldownUntil,
        GauntletCooldownUntil = hero.GauntletCooldownUntil,
        EquipmentJson = EquipmentJson(hero),
        EntropyHex = hero.EntropyHex,
        ServerSeedHex = hero.ServerSeedHex,
        PlayerNonce = hero.PlayerNonce,
        AssetId = hero.AssetId,
        MintArkTxId = hero.MintArkTxId,
    };

    public async Task SaveHeroAsync(Hero hero, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.Heroes.FindAsync([hero.Id], ct);
        if (row is null)
        {
            db.Heroes.Add(NewRow(hero));
        }
        else
        {
            // Only the mutable surface updates — the genome, lineage and commit–reveal audit trail are
            // written once at mint and never change (they're init-only on the aggregate for the same reason).
            row.OwnerId = hero.OwnerId;
            row.Name = hero.Name;
            row.Level = hero.Level;
            row.Xp = hero.Xp;
            row.BreedCount = hero.BreedCount;
            row.BreedCooldownUntil = hero.BreedCooldownUntil;
            row.GauntletCooldownUntil = hero.GauntletCooldownUntil;
            row.EquipmentJson = EquipmentJson(hero);
            row.AssetId = hero.AssetId;
            row.MintArkTxId = hero.MintArkTxId;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task SaveHeroProgressionAsync(Hero hero, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.Heroes.FindAsync([hero.Id], ct);
        if (row is null)
        {
            // Shouldn't happen — every hero is inline-saved at mint before any progression path can mark
            // it dirty. If a row is missing anyway (a concurrent burn's delete, or a mint save that never
            // landed), insert the full snapshot rather than drop a live hero's only durable record: the
            // flush's burn compensation still gets the last word on a concurrently burned hero, and a
            // clashing concurrent insert simply faults this save into the flush's retry.
            db.Heroes.Add(NewRow(hero));
        }
        else
        {
            // ONLY the progression the dirty set tracks — never owner, name, or the mint identifiers.
            // Those belong to the inline identity saves, and a flush write that carried them could land
            // after one and revert it (see the interface doc-comment: the mis-own race).
            row.Level = hero.Level;
            row.Xp = hero.Xp;
            row.BreedCount = hero.BreedCount;
            row.BreedCooldownUntil = hero.BreedCooldownUntil;
            row.GauntletCooldownUntil = hero.GauntletCooldownUntil;
            row.EquipmentJson = EquipmentJson(hero);
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteHeroAsync(string heroId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        // Already gone = done: burns sit on retryable flows (merge/death-match settle), so the erase must
        // be idempotent under a retry that re-runs the settle's in-memory tail.
        if (await db.Heroes.FindAsync([heroId], ct) is not { } row) return;
        db.Heroes.Remove(row);
        await db.SaveChangesAsync(ct);
    }
}
