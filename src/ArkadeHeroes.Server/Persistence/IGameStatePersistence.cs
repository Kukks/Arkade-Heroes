using System.Collections.Concurrent;
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

    /// <summary>Durably record a marketplace offer — called the moment it is created (BEFORE the seller is
    /// handed the address to deposit into) and again on each status transition. Without the row a restart keeps
    /// the covenant's params but loses the only thing that names the offer, so the escrowed asset stops being
    /// discoverable by the market or by its own seller.</summary>
    Task SaveOfferAsync(OfferListing offer, CancellationToken ct = default);

    /// <summary>Durably record a stud proposal — at proposal, at the stud owner's CONSENT (which is when the
    /// invoices are created), at the stud-fee payout latch, and at completion. Called on every one of those
    /// transitions because each is a step a restart must not be able to undo or repeat: an un-consented
    /// proposal must not come back consented, and a stud fee already paid must not come back unpaid.</summary>
    Task SaveStudProposalAsync(StudProposal proposal, CancellationToken ct = default);

    /// <summary>Durably record one completed hero sale — what a hero fetched, and between whom. Keyed by the
    /// offer that settled it, so the two paths that can prove the same sale write the same row and the second
    /// only ever fills in a buyer the first did not know. Books no sats and gates nothing: a lost row costs a
    /// line of a hero's history, never money.</summary>
    Task SaveHeroSaleAsync(HeroSale sale, CancellationToken ct = default);

    /// <summary>Durably append one treasury movement — a fee captured or a payout made — so the by-tag totals
    /// can be grouped back out of the rows at boot. On the INFLOW side <paramref name="id"/> is the invoice id
    /// and the row IS the dedup marker: an id already stored is silently left alone, which is what stops an
    /// invoice counted before a restart from being counted again after one.</summary>
    Task SaveTreasuryFlowAsync(string id, string direction, string tag, long sats, CancellationToken ct = default);
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
    public Task SaveOfferAsync(OfferListing offer, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveStudProposalAsync(StudProposal proposal, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveHeroSaleAsync(HeroSale sale, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveTreasuryFlowAsync(string id, string direction, string tag, long sats, CancellationToken ct = default) => Task.CompletedTask;
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
                StarterFeeInvoiceId = row.StarterFeeInvoiceId,
                LoginPubKeyHex = row.LoginPubKeyHex,
                StreakCount = row.StreakCount,
                LastClaimDay = row.LastClaimDay,
                TermsAcceptedVersion = row.TermsAcceptedVersion,
                TermsAcceptedAtUtc = row.TermsAcceptedAtUtc,
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
            // This hero predates the process. Its receipts do not — they live in memory and died with the
            // last one — so anything derived from them is a partial history, and the timeline says so
            // rather than presenting a life that begins at boot as a whole one.
            store.RehydratedHeroes[hero.Id] = 0;
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

        // Offers come back so the market can find them again — and, far more importantly, so their SELLER
        // can. A `closed` offer is TERMINAL and is never rehydrated, exactly as a resolved bracket isn't:
        // its row stays as an audit marker, but putting one back would re-list a sale that already happened
        // and hand the reconcile a second chance to book its fee. Status is rehydrated as it was stored and
        // is NOT trusted: every non-closed offer is re-reconciled against the chain before it is served
        // (ListOffersAsync/GetOfferAsync/ListReclaimableAsync all reconcile first), because the chain — not
        // this row — is the source of truth for whether the asset is still resting in the covenant.
        foreach (var row in await db.Offers.AsNoTracking().Where(o => o.Status != "closed").ToListAsync(ct))
        {
            store.Offers[row.Id] = new OfferListing
            {
                Id = row.Id,
                SellerId = row.SellerId,
                Kind = row.Kind,
                ItemId = row.ItemId,
                HeroId = row.HeroId,
                AskSats = row.AskSats,
                OfferAddress = row.OfferAddress,
                ItemAssetId = row.ItemAssetId,
                OfferValueSats = row.OfferValueSats,
                RefundAfterUnixSeconds = row.RefundAfterUnixSeconds,
                CreatedAt = row.CreatedAt,
                Status = row.Status,
                ListingFeeSats = row.ListingFeeSats,
                // AssetDeposited is deliberately left false — the next reconcile derives it from the chain.
            };
        }

        // Hero sales come back UNFILTERED — unlike the offers above, there is no terminal state to skip.
        // A sale row is a historical fact, not a live position: it holds nothing, gates nothing, and can
        // never be re-settled, so the only thing rehydrating one can do is let a hero's page still say what
        // it fetched. This is the half of a hero's provenance that survives a restart.
        foreach (var row in await db.HeroSales.AsNoTracking().ToListAsync(ct))
            store.LoadHeroSale(new HeroSale(
                row.OfferId, row.HeroId, row.SellerId, row.BuyerId,
                row.AskSats, row.ListingFeeSats, row.SoldAtUnixSeconds));

        // Stud proposals come back so a consent already given — and, far more importantly, a stud fee
        // already PAID against it — survives the restart. Completed and declined rows are TERMINAL and are
        // never rehydrated, exactly as a resolved bracket or a closed offer isn't: their rows stay as audit
        // markers, but putting a completed one back would hand a stale client a second chance to reveal it,
        // and a reveal is a mint. StudFeePaid rehydrates EXACTLY as stored — it is the once-only payout
        // latch, and reviving a paid proposal with it cleared would pay the stud's owner twice.
        foreach (var row in await db.StudProposals.AsNoTracking().Where(s => !s.Completed && !s.Declined).ToListAsync(ct))
        {
            store.StudProposals[row.Id] = new StudProposal
            {
                Id = row.Id,
                ProposerPlayerId = row.ProposerPlayerId,
                StudOwnerPlayerId = row.StudOwnerPlayerId,
                ProposerHeroId = row.ProposerHeroId,
                StudHeroId = row.StudHeroId,
                ServerSeed = row.ServerSeed,
                CommitmentHex = row.CommitmentHex,
                StudFeeSats = row.StudFeeSats,
                BreedFeeSats = row.BreedFeeSats,
                BreedFeeInvoiceId = row.BreedFeeInvoiceId,
                StudFeeInvoiceId = row.StudFeeInvoiceId,
                CreatedAt = row.CreatedAt,
                Accepted = row.Accepted,
                StudFeePaid = row.StudFeePaid,
            };
        }

        // Fancy finds rebuild the discovery board, the per-hero editions, and — load-bearing — the per-set
        // count, so the next live find of a set takes the right number instead of a second "#1".
        foreach (var row in await db.FancyFinds.AsNoTracking().ToListAsync(ct))
            store.LoadFancyFind(new FancyFind(row.Title, row.HeroId, row.HeroName, row.OwnerId, row.UnixSeconds, row.Edition));

        // Treasury flows fold back into the by-tag totals — which are never stored, only grouped out of the
        // rows, so they cannot drift from the movements they summarise. Folding row by row (rather than
        // letting SQL do the GROUP BY) is the same arithmetic and rebuilds the inflow dedup set in the same
        // pass — and that set is the half that must not be lost: totals without it would let an invoice
        // already counted before the restart be counted a second time after it.
        foreach (var row in await db.TreasuryFlows.AsNoTracking().ToListAsync(ct))
            store.LoadTreasuryFlow(row.Id, row.Direction, row.Tag, row.Sats);
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
                StarterFeeInvoiceId = player.StarterFeeInvoiceId,
                LoginPubKeyHex = player.LoginPubKeyHex,
                StreakCount = player.StreakCount,
                LastClaimDay = player.LastClaimDay,
                TermsAcceptedVersion = player.TermsAcceptedVersion,
                TermsAcceptedAtUtc = player.TermsAcceptedAtUtc,
            });
        }
        else
        {
            row.Name = player.Name;
            row.StarterClaimed = player.StarterClaimed;
            row.StarterFeeInvoiceId = player.StarterFeeInvoiceId;
            row.LoginPubKeyHex = player.LoginPubKeyHex;
            row.StreakCount = player.StreakCount;
            row.LastClaimDay = player.LastClaimDay;
            row.TermsAcceptedVersion = player.TermsAcceptedVersion;
            row.TermsAcceptedAtUtc = player.TermsAcceptedAtUtc;
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

    /// <summary>
    /// Serializes every write of ONE hero's row. All three writers below are load → copy-the-live-hero →
    /// commit with awaits between each step, so two that overlap can commit in the opposite order to the one
    /// they read in, and the loser's stale copy becomes the durable word: a hero transferred twice inside one
    /// save's window ends up durably owned by the intermediate holder while memory holds the final one. Column
    /// narrowing alone can't fix that — it settled the flush-vs-identity case only because the flush stopped
    /// writing identity at all; two IDENTITY saves racing each other both legitimately carry owner and name.
    ///
    /// Safe to take here because this is a leaf: nothing in this class calls back into GameService or
    /// GameStore, so a request already holding a GameStore money-path lock only ever acquires this one INSIDE
    /// it, never the reverse, and each method holds exactly one hero's gate at a time — no cycle to deadlock
    /// on. Semaphores accrue one per hero id and are never removed, mirroring GameStore's keyed locks;
    /// evicting one a waiter is still parked on is the riskier trade.
    /// </summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _heroLocks = new();

    private async Task<IDisposable> LockHeroAsync(string heroId, CancellationToken ct)
    {
        var gate = _heroLocks.GetOrAdd(heroId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        return new HeroLockReleaser(gate);
    }

    private sealed class HeroLockReleaser(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }

    public async Task SaveHeroAsync(Hero hero, CancellationToken ct = default)
    {
        using var heroGate = await LockHeroAsync(hero.Id, ct);
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
        using var heroGate = await LockHeroAsync(hero.Id, ct);
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
        using var heroGate = await LockHeroAsync(heroId, ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        // Already gone = done: burns sit on retryable flows (merge/death-match settle), so the erase must
        // be idempotent under a retry that re-runs the settle's in-memory tail.
        if (await db.Heroes.FindAsync([heroId], ct) is not { } row) return;
        db.Heroes.Remove(row);
        await db.SaveChangesAsync(ct);
    }

    public async Task SaveOfferAsync(OfferListing offer, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.Offers.FindAsync([offer.Id], ct);
        if (row is null)
        {
            db.Offers.Add(new PersistedOffer
            {
                Id = offer.Id,
                SellerId = offer.SellerId,
                Kind = offer.Kind,
                ItemId = offer.ItemId,
                HeroId = offer.HeroId,
                AskSats = offer.AskSats,
                OfferAddress = offer.OfferAddress,
                ItemAssetId = offer.ItemAssetId,
                OfferValueSats = offer.OfferValueSats,
                RefundAfterUnixSeconds = offer.RefundAfterUnixSeconds,
                CreatedAt = offer.CreatedAt,
                Status = offer.Status,
                ListingFeeSats = offer.ListingFeeSats,
            });
        }
        else
        {
            // Status is the ONLY thing that ever moves. The rest is fixed at listing: the covenant is built
            // from those values and its address is derived from them, so a row that disagreed with the
            // deployed covenant would describe an offer that does not exist on-chain.
            //
            // Which is also why this needs no per-offer gate, where SaveHeroAsync needs one: the hero saves
            // race over IDENTITY, so a loser's stale copy can revert an ownership change and durably mis-own
            // a hero. Status carries no such fact — it is re-derived from the chain on the next reconcile, so
            // the worst a lost race leaves is a row reading `active` for an offer memory has already closed,
            // and a restart then reconciles it closed again (re-booking under the same once-only key).
            row.Status = offer.Status;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task SaveHeroSaleAsync(HeroSale sale, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.HeroSales.FindAsync([sale.OfferId], ct);
        if (row is null)
        {
            db.HeroSales.Add(new PersistedHeroSale
            {
                OfferId = sale.OfferId,
                HeroId = sale.HeroId,
                SellerId = sale.SellerId,
                BuyerId = sale.BuyerId,
                AskSats = sale.AskSats,
                ListingFeeSats = sale.ListingFeeSats,
                SoldAtUnixSeconds = sale.SoldAtUnixSeconds,
            });
        }
        else if (row.BuyerId is null && sale.BuyerId is not null)
        {
            // The ONLY field that ever moves, and it only ever moves from unknown to known. Reconcile can
            // prove a sale without learning who bought it; a later claim names them. Nothing else is
            // writable: the price, the parties and the moment are what the trade WAS, and a row able to
            // restate them could rewrite a hero's history after the fact.
            row.BuyerId = sale.BuyerId;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task SaveStudProposalAsync(StudProposal proposal, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.StudProposals.FindAsync([proposal.Id], ct);
        if (row is null)
        {
            db.StudProposals.Add(new PersistedStudProposal
            {
                Id = proposal.Id,
                ProposerPlayerId = proposal.ProposerPlayerId,
                StudOwnerPlayerId = proposal.StudOwnerPlayerId,
                ProposerHeroId = proposal.ProposerHeroId,
                StudHeroId = proposal.StudHeroId,
                ServerSeed = proposal.ServerSeed,
                CommitmentHex = proposal.CommitmentHex,
                StudFeeSats = proposal.StudFeeSats,
                BreedFeeSats = proposal.BreedFeeSats,
                BreedFeeInvoiceId = proposal.BreedFeeInvoiceId,
                StudFeeInvoiceId = proposal.StudFeeInvoiceId,
                CreatedAt = proposal.CreatedAt,
                Accepted = proposal.Accepted,
                Declined = proposal.Declined,
                Completed = proposal.Completed,
                StudFeePaid = proposal.StudFeePaid,
                ChildHeroId = proposal.ChildHeroId,
            });
        }
        else
        {
            // Only the state machine and what ACCEPT stamps on it ever move. The parties, the heroes, the
            // stud fee and the committed seed are fixed at proposal: they are what the stud's owner
            // consented to, so a row allowed to rewrite them could re-price or re-target a breed the
            // counterparty already agreed to.
            row.BreedFeeSats = proposal.BreedFeeSats;
            row.BreedFeeInvoiceId = proposal.BreedFeeInvoiceId;
            row.StudFeeInvoiceId = proposal.StudFeeInvoiceId;
            row.Accepted = proposal.Accepted;
            row.Declined = proposal.Declined;
            row.Completed = proposal.Completed;
            row.StudFeePaid = proposal.StudFeePaid;
            row.ChildHeroId = proposal.ChildHeroId;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task SaveTreasuryFlowAsync(string id, string direction, string tag, long sats, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        // Append-only, never updated. A row already at this key is the INFLOW dedup firing: that invoice's
        // fee is already counted, so there is nothing to add — the same silent no-op the in-memory tally
        // has always given a repeat record call, now surviving a restart.
        if (await db.TreasuryFlows.FindAsync([direction, id], ct) is not null) return;
        db.TreasuryFlows.Add(new PersistedTreasuryFlow { Id = id, Direction = direction, Tag = tag, Sats = sats });
        await db.SaveChangesAsync(ct);
    }
}
