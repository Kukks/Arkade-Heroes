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

    /// <summary>A rename billed but not yet applied. Saved BEFORE the invoice reaches the player, so the
    /// window in which they have paid and the server could forget never opens.</summary>
    Task SaveRenameAsync(RenameSession session, CancellationToken ct = default);

    /// <summary>Drops a pending rename once it lands, so the paid fee stops being reusable the moment the
    /// name is applied — the same "one paid fee buys one APPLIED rename" rule the in-memory session had.</summary>
    Task DeleteRenameAsync(string heroId, CancellationToken ct = default);

    /// <summary>A gauntlet billed but not yet run. Saved BEFORE the invoice reaches the player.</summary>
    Task SaveGauntletAsync(GauntletSession session, CancellationToken ct = default);

    /// <summary>Drops a gauntlet once it has been run, so one fee buys exactly one run.</summary>
    Task DeleteGauntletAsync(string gauntletId, CancellationToken ct = default);

    /// <summary>An unfinished breeding. In covenant mode its escrow holds both parents, and /reclaim can
    /// only name it while this row exists.</summary>
    Task SaveBreedingAsync(BreedingSession session, CancellationToken ct = default);

    /// <summary>An unfinished fusion — same reason, holding base and sacrifice.</summary>
    Task SaveMergeAsync(MergeSession session, CancellationToken ct = default);

    /// <summary>Drops a breeding or fusion once it resolves; the escrow is spent and there is nothing left
    /// to reclaim.</summary>
    Task DeleteEscrowSessionAsync(string sessionId, CancellationToken ct = default);

    /// <summary>An unsettled death-match, whose joint escrow holds both heroes and their staked gear. Written
    /// at open and again at accept — acceptance IS the defender staking, and updates the only two fields that
    /// move after open (that flag and their fee invoice), so a row frozen at open would read as unstaked.</summary>
    Task SaveDeathMatchAsync(DeathMatchSession session, CancellationToken ct = default);

    /// <summary>Drops a death-match once it settles; the joint escrow is spent and the loser is burned.</summary>
    Task DeleteDeathMatchAsync(string deathMatchId, CancellationToken ct = default);

    /// <summary>An unresolved duel or squad match, whose stake sits in per-party covenant escrows /reclaim
    /// can only name while this row exists. Written at open, at accept, and when the refund window expires.</summary>
    Task SaveMatchAsync(MatchSession session, CancellationToken ct = default);

    /// <summary>The squad half — same row, same reason.</summary>
    Task SaveSquadMatchAsync(SquadMatchSession session, CancellationToken ct = default);

    /// <summary>Drops a match once it RESOLVES. An expired one stays: its stake is still escrowed behind a
    /// timelock, which is exactly what /reclaim lists.</summary>
    Task DeleteMatchSessionAsync(string matchId, CancellationToken ct = default);

    /// <summary>Durably record one completed hero sale — what a hero fetched, and between whom. Keyed by the
    /// offer that settled it, so the two paths that can prove the same sale write the same row and the second
    /// only ever fills in a buyer the first did not know. Books no sats and gates nothing: a lost row costs a
    /// line of a hero's history, never money.</summary>
    Task SaveHeroSaleAsync(HeroSale sale, CancellationToken ct = default);

    /// <summary>Durably record one hero's DESTRUCTION, at the burn site and BEFORE
    /// <see cref="DeleteHeroAsync"/> erases the hero — so the two can never both be missing. Keyed by the
    /// hero, so a retried settle re-running the in-memory tail overwrites nothing and invents no second
    /// death. Books no sats and gates nothing: a lost row costs a name on a page, never money.</summary>
    Task SaveHeroTombstoneAsync(HeroTombstone stone, CancellationToken ct = default);

    /// <summary>Durably record a bid on a hero — at the bid, at the owner's CONSENT (which is when the
    /// invoice is created), at each payout latch, and at every terminal transition. Called on all of them
    /// for the reason the stud proposal is: an un-consented bid must not come back consented, and sats
    /// already paid out — to the owner or back to the bidder — must not come back unpaid.</summary>
    Task SaveHeroBidAsync(HeroBid bid, CancellationToken ct = default);

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
    public Task SaveRenameAsync(RenameSession session, CancellationToken ct = default) => Task.CompletedTask;
    public Task DeleteRenameAsync(string heroId, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveGauntletAsync(GauntletSession session, CancellationToken ct = default) => Task.CompletedTask;
    public Task DeleteGauntletAsync(string gauntletId, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveBreedingAsync(BreedingSession session, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveMergeAsync(MergeSession session, CancellationToken ct = default) => Task.CompletedTask;
    public Task DeleteEscrowSessionAsync(string sessionId, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveDeathMatchAsync(DeathMatchSession session, CancellationToken ct = default) => Task.CompletedTask;
    public Task DeleteDeathMatchAsync(string deathMatchId, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveMatchAsync(MatchSession session, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveSquadMatchAsync(SquadMatchSession session, CancellationToken ct = default) => Task.CompletedTask;
    public Task DeleteMatchSessionAsync(string matchId, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveHeroSaleAsync(HeroSale sale, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveHeroTombstoneAsync(HeroTombstone stone, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveHeroBidAsync(HeroBid bid, CancellationToken ct = default) => Task.CompletedTask;
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

        // Every bracket comes back, TERMINAL ONES INCLUDED. They used to be filtered out for fear that a
        // settled bracket back in the live store could settle a second time — but what stops that is the
        // STATUS, which rides along with it: resolve refuses anything not `full`, and both resolve and
        // refund refuse a bracket that is already resolved or refunded. Filtering was belt to those braces,
        // and it cost the whole record of who won a real-sats pot and what they were paid. Pinned by
        // TournamentOutcomeDurabilityTests, which drives a restart and then tries to settle again.
        foreach (var row in await db.Tournaments.AsNoTracking().ToListAsync(ct))
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
            // The settled outcome, when there is one. Read back onto the SAME fields the live resolve
            // writes, so every reader — the list DTO, the replay endpoint, the achievements count — works
            // off one shape and none of them needs to know the bracket predates this process.
            if (row.ResultJson is { } resultJson)
            {
                session.Result = System.Text.Json.JsonSerializer.Deserialize<Core.Combat.TournamentResult>(resultJson);
                session.Prizes = row.PrizesJson is { } pj
                    ? System.Text.Json.JsonSerializer.Deserialize<List<long>>(pj) ?? []
                    : [];
                session.EntrantSnapshots = row.EntrantSnapshotsJson is { } sj
                    ? System.Text.Json.JsonSerializer.Deserialize<List<Shared.HeroDto>>(sj)
                    : null;
                session.Nonce = row.Nonce;
                session.EntropyHex = row.EntropyHex;
                session.EntrantsCommitmentHex = row.EntrantsCommitmentHex;
                session.ConfigVersion = row.ConfigVersion;
                session.ContentVersion = row.ContentVersion;
            }
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
        // already PAID against it — survives the restart. Completed, declined and refunded rows are TERMINAL
        // and are never rehydrated, as a resolved bracket or a closed offer isn't: their rows stay as audit
        // markers, but putting a completed one back would hand a stale client a second chance to reveal it,
        // and a reveal is a mint. StudFeePaid rehydrates EXACTLY as stored — it is the once-only payout
        // latch, and reviving a paid proposal with it cleared would pay the stud's owner twice.
        foreach (var row in await db.StudProposals.AsNoTracking()
                     .Where(s => !s.Completed && !s.Declined && !s.Refunded).ToListAsync(ct))
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

        // Renames come back because the fee is paid BEFORE the name is applied. Losing this row cost the
        // player twice: their pending rename was gone, and the reuse branch in RequestRenameAsync — which
        // reads this very dictionary — then found no prior invoice and billed them a second time for a
        // rename they had already bought. Rows are deleted the moment a rename lands, so anything still
        // here is by definition unapplied.
        foreach (var row in await db.Renames.AsNoTracking().ToListAsync(ct))
            store.Renames[row.HeroId] = new RenameSession
            {
                HeroId = row.HeroId, NewName = row.NewName, FeeInvoiceId = row.FeeInvoiceId,
            };

        // Gauntlets come back because the fee clears BEFORE the run. Losing one cost the player the fee
        // outright: there is no reuse branch here, so opening another simply bills again. Rows are deleted
        // when a run completes, so anything still here is unspent by construction.
        foreach (var row in await db.Gauntlets.AsNoTracking().ToListAsync(ct))
            store.Gauntlets[row.Id] = new GauntletSession
            {
                Id = row.Id, PlayerId = row.PlayerId, HeroId = row.HeroId,
                ServerSeed = row.ServerSeed, CommitmentHex = row.CommitmentHex,
                FeeInvoiceId = row.FeeInvoiceId, FeeSats = row.FeeSats, CreatedAt = row.CreatedAt,
            };

        // Breedings and fusions come back so /reclaim can still NAME the escrow holding two of the
        // player's heroes. Rows are deleted when the flow resolves, so anything here is unfinished.
        foreach (var row in await db.EscrowSessions.AsNoTracking().ToListAsync(ct))
        {
            if (row.Kind == "breed")
                store.Breedings[row.Id] = new BreedingSession
                {
                    Id = row.Id, PlayerId = row.PlayerId,
                    ParentAId = row.FirstHeroId, ParentBId = row.SecondHeroId,
                    ServerSeed = row.ServerSeed, CommitmentHex = row.CommitmentHex, Mode = row.Mode,
                    FeeInvoiceId = row.FeeInvoiceId, EscrowAddress = row.EscrowAddress,
                    FeeSats = row.FeeSats, CreatedAt = row.CreatedAt,
                };
            else
                store.Merges[row.Id] = new MergeSession
                {
                    Id = row.Id, PlayerId = row.PlayerId,
                    BaseId = row.FirstHeroId, SacrificeId = row.SecondHeroId,
                    ServerSeed = row.ServerSeed, CommitmentHex = row.CommitmentHex, Mode = row.Mode,
                    EscrowAddress = row.EscrowAddress, FeeSats = row.FeeSats, CreatedAt = row.CreatedAt,
                };
        }

        // Duels and squads come back so /reclaim can still name the per-party escrows holding their stakes.
        // Resolved rows are deleted; an EXPIRED one is kept, because its stake is still behind a timelock.
        foreach (var row in await db.MatchSessions.AsNoTracking().ToListAsync(ct))
        {
            var challengers = System.Text.Json.JsonSerializer.Deserialize<List<string>>(row.ChallengerLineupJson) ?? [];
            var defenders = System.Text.Json.JsonSerializer.Deserialize<List<string>>(row.DefenderLineupJson) ?? [];
            if (row.Kind == "duel")
                store.Matches[row.Id] = new MatchSession
                {
                    Id = row.Id,
                    ChallengerPlayerId = row.ChallengerPlayerId, DefenderPlayerId = row.DefenderPlayerId,
                    ChallengerHeroId = challengers.FirstOrDefault() ?? "",
                    DefenderHeroId = defenders.FirstOrDefault() ?? "",
                    ServerSeed = row.ServerSeed, CommitmentHex = row.CommitmentHex,
                    WagerSats = row.WagerSats, Mode = row.Mode,
                    EscrowChallengerAddress = row.EscrowChallengerAddress,
                    EscrowDefenderAddress = row.EscrowDefenderAddress,
                    RefundAfterUnixSeconds = row.RefundAfterUnixSeconds,
                    ChallengerInvoiceId = row.ChallengerInvoiceId, DefenderInvoiceId = row.DefenderInvoiceId,
                    ChallengerFeeInvoiceId = row.ChallengerFeeInvoiceId,
                    DefenderFeeInvoiceId = row.DefenderFeeInvoiceId,
                    Status = row.Status, CreatedAt = row.CreatedAt,
                };
            else
                store.SquadMatches[row.Id] = new SquadMatchSession
                {
                    Id = row.Id,
                    ChallengerPlayerId = row.ChallengerPlayerId, DefenderPlayerId = row.DefenderPlayerId,
                    ChallengerLineup = challengers, DefenderLineup = defenders,
                    ServerSeed = row.ServerSeed, CommitmentHex = row.CommitmentHex,
                    WagerSats = row.WagerSats, Mode = row.Mode,
                    EscrowChallengerAddress = row.EscrowChallengerAddress,
                    EscrowDefenderAddress = row.EscrowDefenderAddress,
                    RefundAfterUnixSeconds = row.RefundAfterUnixSeconds,
                    ChallengerInvoiceId = row.ChallengerInvoiceId, DefenderInvoiceId = row.DefenderInvoiceId,
                    ChallengerFeeInvoiceId = row.ChallengerFeeInvoiceId,
                    DefenderFeeInvoiceId = row.DefenderFeeInvoiceId,
                    Status = row.Status, CreatedAt = row.CreatedAt,
                };
        }

        // Death-matches come back so /reclaim can still name the joint escrow holding both heroes and the
        // gear staked with them. Rows are deleted at settle, so anything here is unsettled.
        foreach (var row in await db.DeathMatches.AsNoTracking().ToListAsync(ct))
            store.DeathMatches[row.Id] = new DeathMatchSession
            {
                Id = row.Id,
                ChallengerPlayerId = row.ChallengerPlayerId, DefenderPlayerId = row.DefenderPlayerId,
                ChallengerHeroId = row.ChallengerHeroId, DefenderHeroId = row.DefenderHeroId,
                ServerSeed = row.ServerSeed, CommitmentHex = row.CommitmentHex,
                JointEscrowAddress = row.JointEscrowAddress,
                ChallengerGearItemIds =
                    System.Text.Json.JsonSerializer.Deserialize<List<string>>(row.ChallengerGearJson) ?? [],
                DefenderGearItemIds =
                    System.Text.Json.JsonSerializer.Deserialize<List<string>>(row.DefenderGearJson) ?? [],
                ChallengerFeeInvoiceId = row.ChallengerFeeInvoiceId,
                DefenderFeeInvoiceId = row.DefenderFeeInvoiceId,
                Accepted = row.Accepted, Absorb = row.Absorb, SpeciesId = row.SpeciesId,
                CreatedAt = row.CreatedAt,
            };

        // Tombstones come back UNFILTERED, exactly as sales do and for the same reason: a headstone is a
        // historical fact, not a live position. It holds nothing and gates nothing, and it is the ONLY
        // thing that can name a hero whose row was erased — so filtering any of it out would restore the
        // hole it exists to close. There is no terminal state to skip; death is the terminal state.
        foreach (var row in await db.HeroTombstones.AsNoTracking().ToListAsync(ct))
            store.LoadTombstone(new HeroTombstone(
                row.HeroId, row.Name, row.OwnerId, row.Generation, row.Level, row.GenomeHex,
                row.Reason, row.SessionId, row.ReplacedByHeroId, row.DestroyedAtUnixSeconds,
                row.ParentAId, row.ParentBId));

        // Bids come back only while LIVE, on the same ruling stud proposals get. A settled or refunded bid
        // is terminal and its sats have already moved; rehydrating one would hand a stale client a second
        // chance to settle it, and a settle both pays a player and moves a hero. Declined and withdrawn
        // rows never billed anything, so they have nothing to strand. SellerPaid and RefundPaid rehydrate
        // EXACTLY as stored — they are the once-only payout latches, and reviving a paid bid with either
        // cleared would pay out twice from a treasury that cannot print.
        foreach (var row in await db.HeroBids.AsNoTracking()
                     .Where(b => !b.Settled && !b.Refunded && !b.Declined && !b.Withdrawn).ToListAsync(ct))
        {
            store.HeroBids[row.Id] = new HeroBid
            {
                Id = row.Id,
                BidderPlayerId = row.BidderPlayerId,
                OwnerPlayerId = row.OwnerPlayerId,
                HeroId = row.HeroId,
                BidSats = row.BidSats,
                FeeSats = row.FeeSats,
                BidInvoiceId = row.BidInvoiceId,
                CreatedAt = row.CreatedAt,
                Accepted = row.Accepted,
                SellerPaid = row.SellerPaid,
                RefundPaid = row.RefundPaid,
                ReclaimAfterUnixSeconds = row.ReclaimAfterUnixSeconds,
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
            row = new PersistedTournament
            {
                Id = session.Id,
                OpenerPlayerId = session.OpenerPlayerId,
                BuyInSats = session.BuyInSats,
                Size = session.Size,
                ServerSeed = session.ServerSeed,
                CommitmentHex = session.CommitmentHex,
                Status = session.Status,
                EntrantsJson = entrantsJson,
            };
            db.Tournaments.Add(row);
        }
        else
        {
            row.Status = session.Status;
            row.EntrantsJson = entrantsJson;
        }
        WriteOutcome(row, session);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Copies the settled outcome onto the row, once the bracket has one. Deliberately WRITE-ONLY-WHEN-SET:
    /// a session that is not resolved leaves every outcome column exactly as it found it, so a save driven
    /// by some later status change can never blank a result that is already on disk. Resolve stamps all of
    /// these before it marks the bracket resolved, so the record goes down complete or not at all.
    /// </summary>
    private static void WriteOutcome(PersistedTournament row, TournamentSession session)
    {
        if (session.Result is not { } result) return;
        row.ResultJson = System.Text.Json.JsonSerializer.Serialize(result);
        row.PrizesJson = System.Text.Json.JsonSerializer.Serialize(session.Prizes);
        row.EntrantSnapshotsJson = session.EntrantSnapshots is { } snaps
            ? System.Text.Json.JsonSerializer.Serialize(snaps)
            : null;
        row.Nonce = session.Nonce;
        row.EntropyHex = session.EntropyHex;
        row.EntrantsCommitmentHex = session.EntrantsCommitmentHex;
        row.ConfigVersion = session.ConfigVersion;
        row.ContentVersion = session.ContentVersion;
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

    public async Task SaveHeroTombstoneAsync(HeroTombstone stone, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        // Insert-once and NEVER update. A hero dies once, and the first write is the one that had the hero
        // still in hand to read its name, level and genome off — a retried settle re-running this after the
        // row was erased could only carry less. Idempotent for exactly that retry.
        if (await db.HeroTombstones.FindAsync([stone.HeroId], ct) is not null) return;
        db.HeroTombstones.Add(new PersistedHeroTombstone
        {
            HeroId = stone.HeroId,
            Name = stone.Name,
            OwnerId = stone.OwnerId,
            Generation = stone.Generation,
            Level = stone.Level,
            GenomeHex = stone.GenomeHex,
            Reason = stone.Reason,
            SessionId = stone.SessionId,
            ReplacedByHeroId = stone.ReplacedByHeroId,
            DestroyedAtUnixSeconds = stone.DestroyedAtUnixSeconds,
            ParentAId = stone.ParentAId,
            ParentBId = stone.ParentBId,
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task SaveHeroBidAsync(HeroBid bid, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.HeroBids.FindAsync([bid.Id], ct);
        if (row is null)
        {
            db.HeroBids.Add(new PersistedHeroBid
            {
                Id = bid.Id,
                BidderPlayerId = bid.BidderPlayerId,
                OwnerPlayerId = bid.OwnerPlayerId,
                HeroId = bid.HeroId,
                BidSats = bid.BidSats,
                FeeSats = bid.FeeSats,
                BidInvoiceId = bid.BidInvoiceId,
                CreatedAt = bid.CreatedAt,
                Accepted = bid.Accepted,
                Declined = bid.Declined,
                Withdrawn = bid.Withdrawn,
                Settled = bid.Settled,
                Refunded = bid.Refunded,
                SellerPaid = bid.SellerPaid,
                RefundPaid = bid.RefundPaid,
                ReclaimAfterUnixSeconds = bid.ReclaimAfterUnixSeconds,
            });
        }
        else
        {
            // Only the state machine and what ACCEPT stamps on it ever move, exactly as for a stud proposal.
            // The parties, the hero and the bid amount are fixed at proposal: they are what the owner
            // consented to, and a row able to rewrite them could re-price a sale already agreed.
            row.FeeSats = bid.FeeSats;
            row.BidInvoiceId = bid.BidInvoiceId;
            row.Accepted = bid.Accepted;
            row.Declined = bid.Declined;
            row.Withdrawn = bid.Withdrawn;
            row.Settled = bid.Settled;
            row.Refunded = bid.Refunded;
            row.SellerPaid = bid.SellerPaid;
            row.RefundPaid = bid.RefundPaid;
            row.ReclaimAfterUnixSeconds = bid.ReclaimAfterUnixSeconds;
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
                Refunded = proposal.Refunded,
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
            row.Refunded = proposal.Refunded;
            row.StudFeePaid = proposal.StudFeePaid;
            row.ChildHeroId = proposal.ChildHeroId;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task SaveRenameAsync(RenameSession session, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.Renames.FindAsync([session.HeroId], ct);
        if (row is null)
            db.Renames.Add(new PersistedRenameSession
            {
                HeroId = session.HeroId, NewName = session.NewName, FeeInvoiceId = session.FeeInvoiceId,
            });
        else
        {
            // The NAME may be retargeted; the invoice must not be, or a retarget would abandon the fee the
            // player already paid and the reuse branch would never find it again.
            row.NewName = session.NewName;
            row.FeeInvoiceId ??= session.FeeInvoiceId;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteRenameAsync(string heroId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (await db.Renames.FindAsync([heroId], ct) is { } row)
        {
            db.Renames.Remove(row);
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task SaveGauntletAsync(GauntletSession session, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (await db.Gauntlets.FindAsync([session.Id], ct) is not null) return;   // immutable once opened
        db.Gauntlets.Add(new PersistedGauntletSession
        {
            Id = session.Id, PlayerId = session.PlayerId, HeroId = session.HeroId,
            ServerSeed = session.ServerSeed, CommitmentHex = session.CommitmentHex,
            FeeInvoiceId = session.FeeInvoiceId, FeeSats = session.FeeSats, CreatedAt = session.CreatedAt,
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteGauntletAsync(string gauntletId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (await db.Gauntlets.FindAsync([gauntletId], ct) is { } row)
        {
            db.Gauntlets.Remove(row);
            await db.SaveChangesAsync(ct);
        }
    }

    public Task SaveBreedingAsync(BreedingSession session, CancellationToken ct = default) =>
        UpsertEscrowAsync(session.Id, "breed", session.PlayerId, session.ParentAId, session.ParentBId,
            session.ServerSeed, session.CommitmentHex, session.Mode, session.FeeInvoiceId,
            session.EscrowAddress, session.FeeSats, session.CreatedAt, ct);

    public Task SaveMergeAsync(MergeSession session, CancellationToken ct = default) =>
        UpsertEscrowAsync(session.Id, "merge", session.PlayerId, session.BaseId, session.SacrificeId,
            session.ServerSeed, session.CommitmentHex, session.Mode, feeInvoiceId: null,
            session.EscrowAddress, session.FeeSats, session.CreatedAt, ct);

    /// <summary>The ESCROW ADDRESS is the one field that moves after the row is written — it is assigned
    /// once the contract is built — so it is the only thing an existing row updates.</summary>
    private async Task UpsertEscrowAsync(
        string id, string kind, string playerId, string firstHeroId, string secondHeroId,
        byte[] serverSeed, string commitmentHex, string mode, string? feeInvoiceId,
        string? escrowAddress, long feeSats, DateTimeOffset createdAt, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (await db.EscrowSessions.FindAsync([id], ct) is { } row) row.EscrowAddress = escrowAddress;
        else
            db.EscrowSessions.Add(new PersistedEscrowSession
            {
                Id = id, Kind = kind, PlayerId = playerId,
                FirstHeroId = firstHeroId, SecondHeroId = secondHeroId,
                ServerSeed = serverSeed, CommitmentHex = commitmentHex, Mode = mode,
                FeeInvoiceId = feeInvoiceId, EscrowAddress = escrowAddress,
                FeeSats = feeSats, CreatedAt = createdAt,
            });
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteEscrowSessionAsync(string sessionId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (await db.EscrowSessions.FindAsync([sessionId], ct) is { } row)
        {
            db.EscrowSessions.Remove(row);
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task SaveDeathMatchAsync(DeathMatchSession session, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (await db.DeathMatches.FindAsync([session.Id], ct) is { } row)
        {
            row.Accepted = session.Accepted;
            row.DefenderFeeInvoiceId = session.DefenderFeeInvoiceId;
        }
        else
            db.DeathMatches.Add(new PersistedDeathMatchSession
            {
                Id = session.Id,
                ChallengerPlayerId = session.ChallengerPlayerId, DefenderPlayerId = session.DefenderPlayerId,
                ChallengerHeroId = session.ChallengerHeroId, DefenderHeroId = session.DefenderHeroId,
                ServerSeed = session.ServerSeed, CommitmentHex = session.CommitmentHex,
                JointEscrowAddress = session.JointEscrowAddress,
                ChallengerGearJson = System.Text.Json.JsonSerializer.Serialize(session.ChallengerGearItemIds),
                DefenderGearJson = System.Text.Json.JsonSerializer.Serialize(session.DefenderGearItemIds),
                ChallengerFeeInvoiceId = session.ChallengerFeeInvoiceId,
                DefenderFeeInvoiceId = session.DefenderFeeInvoiceId,
                Accepted = session.Accepted, Absorb = session.Absorb, SpeciesId = session.SpeciesId,
                CreatedAt = session.CreatedAt,
            });
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteDeathMatchAsync(string deathMatchId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (await db.DeathMatches.FindAsync([deathMatchId], ct) is { } row)
        {
            db.DeathMatches.Remove(row);
            await db.SaveChangesAsync(ct);
        }
    }

    public Task SaveMatchAsync(MatchSession m, CancellationToken ct = default) =>
        UpsertMatchAsync(m.Id, "duel", m.ChallengerPlayerId, m.DefenderPlayerId,
            [m.ChallengerHeroId], [m.DefenderHeroId], m.ServerSeed, m.CommitmentHex, m.WagerSats, m.Mode,
            m.EscrowChallengerAddress, m.EscrowDefenderAddress, m.RefundAfterUnixSeconds,
            m.ChallengerInvoiceId, m.DefenderInvoiceId, m.ChallengerFeeInvoiceId, m.DefenderFeeInvoiceId,
            m.Status, m.CreatedAt, ct);

    public Task SaveSquadMatchAsync(SquadMatchSession m, CancellationToken ct = default) =>
        UpsertMatchAsync(m.Id, "squad", m.ChallengerPlayerId, m.DefenderPlayerId,
            m.ChallengerLineup, m.DefenderLineup, m.ServerSeed, m.CommitmentHex, m.WagerSats, m.Mode,
            m.EscrowChallengerAddress, m.EscrowDefenderAddress, m.RefundAfterUnixSeconds,
            m.ChallengerInvoiceId, m.DefenderInvoiceId, m.ChallengerFeeInvoiceId, m.DefenderFeeInvoiceId,
            m.Status, m.CreatedAt, ct);

    /// <summary>Everything an accept or an expiry can move updates; the opening facts (parties, lineups,
    /// seed, wager) cannot change, so they are written once.</summary>
    private async Task UpsertMatchAsync(
        string id, string kind, string challengerPlayerId, string? defenderPlayerId,
        IReadOnlyList<string> challengerLineup, IReadOnlyList<string> defenderLineup,
        byte[] serverSeed, string commitmentHex, long wagerSats, string mode,
        string? escrowChallenger, string? escrowDefender, long? refundAfter,
        string? challengerInvoiceId, string? defenderInvoiceId,
        string? challengerFeeInvoiceId, string? defenderFeeInvoiceId,
        string status, DateTimeOffset createdAt, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (await db.MatchSessions.FindAsync([id], ct) is { } row)
        {
            row.DefenderPlayerId = defenderPlayerId;
            row.EscrowChallengerAddress = escrowChallenger;
            row.EscrowDefenderAddress = escrowDefender;
            row.RefundAfterUnixSeconds = refundAfter;
            row.DefenderInvoiceId = defenderInvoiceId;
            row.ChallengerFeeInvoiceId = challengerFeeInvoiceId;
            row.DefenderFeeInvoiceId = defenderFeeInvoiceId;
            row.Status = status;
        }
        else
            db.MatchSessions.Add(new PersistedMatchSession
            {
                Id = id, Kind = kind,
                ChallengerPlayerId = challengerPlayerId, DefenderPlayerId = defenderPlayerId,
                ChallengerLineupJson = System.Text.Json.JsonSerializer.Serialize(challengerLineup),
                DefenderLineupJson = System.Text.Json.JsonSerializer.Serialize(defenderLineup),
                ServerSeed = serverSeed, CommitmentHex = commitmentHex,
                WagerSats = wagerSats, Mode = mode,
                EscrowChallengerAddress = escrowChallenger, EscrowDefenderAddress = escrowDefender,
                RefundAfterUnixSeconds = refundAfter,
                ChallengerInvoiceId = challengerInvoiceId, DefenderInvoiceId = defenderInvoiceId,
                ChallengerFeeInvoiceId = challengerFeeInvoiceId, DefenderFeeInvoiceId = defenderFeeInvoiceId,
                Status = status, CreatedAt = createdAt,
            });
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteMatchSessionAsync(string matchId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (await db.MatchSessions.FindAsync([matchId], ct) is { } row)
        {
            db.MatchSessions.Remove(row);
            await db.SaveChangesAsync(ct);
        }
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
