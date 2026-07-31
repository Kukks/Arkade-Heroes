using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace ArkadeHeroes.Server.Persistence;

/// <summary>
/// One immutable record of something the server DID. Append-only: written once, never updated, never
/// deleted — enforced in SQLite itself by BEFORE UPDATE / BEFORE DELETE triggers the migration installs,
/// not merely by the absence of an update method here.
///
/// This sits ALONGSIDE the state model, it does not replace it. <see cref="GameStore"/> plus the durable
/// rows next to this one remain the source of truth for CURRENT state; this table is the record of how that
/// state came to be. A server holding real bitcoin does not get rebuilt into event sourcing in one pass.
/// </summary>
public class PersistedAuditEvent
{
    /// <summary>The monotonic sequence — a SQLite AUTOINCREMENT rowid, so it strictly increases for the
    /// life of the database and is the log's total order. (AUTOINCREMENT, not a bare INTEGER PRIMARY KEY,
    /// so the number is never reused; nothing deletes from here anyway, and belt-and-braces is cheap.)</summary>
    public long Sequence { get; set; }

    /// <summary>When the action happened, in UTC.</summary>
    public required DateTimeOffset AtUtc { get; set; }

    /// <summary>The player who caused it, or NULL for the server itself — a lazy season settle, an offer
    /// the chain closed under us, an operator action taken through the shared admin token (which names no
    /// player, so attributing one would be a lie).</summary>
    public string? ActorPlayerId { get; set; }

    /// <summary>What happened — one of the <see cref="AuditEventType"/> constants.</summary>
    public required string EventType { get; set; }

    /// <summary>The specifics as JSON: amounts in sats, the counterparty, the outcome. Free-form on
    /// purpose — this is a record of the past, and pinning it to a schema would mean migrating history
    /// every time a flow learns a new field.</summary>
    public required string PayloadJson { get; set; }

    /// <summary>The once-only key, when the action HAS one — <c>daily-claim:{player}:{day}</c>,
    /// <c>stud-fee-paid:{proposal}</c>. UNIQUE, so a retried action cannot log twice: the money paths in
    /// this repo already sit behind durable latches and invoice-id dedup, and the log matches that
    /// discipline rather than inventing a weaker one. NULL where the action genuinely can recur (a mint, a
    /// listing) and repetition is the fact worth keeping.</summary>
    public string? DedupKey { get; set; }

    /// <summary>The ids this event touches — hero ids, session/match ids, offer ids, player ids. Its own
    /// table so "everything that ever happened to THIS hero" is an indexed lookup rather than a scan of a
    /// table designed to grow forever.</summary>
    public List<PersistedAuditSubject> Subjects { get; set; } = [];
}

/// <summary>One id an <see cref="PersistedAuditEvent"/> touches. Append-only with its parent.</summary>
public class PersistedAuditSubject
{
    public long Sequence { get; set; }
    public required string SubjectId { get; set; }
    public PersistedAuditEvent? Event { get; set; }
}

/// <summary>
/// Every event type the server writes. Constants rather than an enum so the stored string is stable
/// forever — a renumbered enum would silently re-label history that is supposed to be immutable.
/// </summary>
public static class AuditEventType
{
    // ── Players ──
    public const string PlayerRegistered = "player.registered";
    public const string PlayerLoggedIn = "player.logged-in";
    public const string PlayerAcceptedTerms = "player.terms-accepted";

    // ── Heroes: the identity events ──
    public const string HeroMinted = "hero.minted";
    public const string HeroBurned = "hero.burned";
    public const string HeroRenameRequested = "hero.rename-requested";
    public const string HeroRenamed = "hero.renamed";
    public const string HeroTransferred = "hero.transferred";
    public const string HeroEquipped = "hero.equipped";
    public const string HeroUnequipped = "hero.unequipped";

    // ── Starters ──
    public const string StarterRequested = "starter.requested";
    public const string StarterClaimed = "starter.claimed";

    // ── Breeding ──
    public const string BreedCommitted = "breed.committed";
    public const string BreedRevealed = "breed.revealed";

    // ── Stud service (cross-owner breeding) ──
    public const string StudProposed = "stud.proposed";
    public const string StudAccepted = "stud.accepted";
    public const string StudDeclined = "stud.declined";
    public const string StudRevealed = "stud.revealed";

    // ── Fusion / merge ──
    public const string MergeCommitted = "merge.committed";
    public const string MergeRevealed = "merge.revealed";

    // ── Death-match ──
    public const string DeathMatchOpened = "deathmatch.opened";
    public const string DeathMatchAccepted = "deathmatch.accepted";
    public const string DeathMatchSettled = "deathmatch.settled";

    // ── Duels ──
    public const string MatchOpened = "match.opened";
    public const string MatchAccepted = "match.accepted";
    public const string MatchResolved = "match.resolved";
    public const string MatchExpired = "match.expired";

    // ── Squad (3v3) ──
    public const string SquadOpened = "squad.opened";
    public const string SquadAccepted = "squad.accepted";
    public const string SquadResolved = "squad.resolved";

    // ── Tournaments ──
    public const string TournamentOpened = "tournament.opened";
    public const string TournamentJoined = "tournament.joined";
    public const string TournamentResolved = "tournament.resolved";
    public const string TournamentRefunded = "tournament.refunded";

    // ── PvE ──
    public const string GauntletOpened = "gauntlet.opened";
    public const string GauntletRun = "gauntlet.run";
    public const string TrialsOpened = "trials.opened";
    public const string TrialsRun = "trials.run";

    // ── Items ──
    public const string ItemInvoiced = "item.invoiced";
    public const string ItemClaimed = "item.claimed";

    // ── Marketplace ──
    public const string OfferListed = "offer.listed";
    public const string OfferClosed = "offer.closed";
    public const string OfferHeroClaimed = "offer.hero-claimed";

    // ── Daily loop + seasons ──
    public const string DailyClaimed = "daily.claimed";
    public const string SeasonSettled = "season.settled";

    // ── Treasury: written at the ONE choke point every sat passes through ──
    public const string TreasuryInflow = "treasury.inflow";
    public const string TreasuryOutflow = "treasury.outflow";

    // ── Operator console ──
    public const string AdminAction = "admin.action";
}

/// <summary>What a caller hands the log. <paramref name="Payload"/> is serialized to JSON as-is.</summary>
/// <param name="EventType">One of <see cref="AuditEventType"/>.</param>
/// <param name="ActorPlayerId">The player who caused it, or null for the server itself.</param>
/// <param name="SubjectIds">Every id this touches; blanks and duplicates are dropped.</param>
/// <param name="Payload">The specifics — amounts in sats, counterparty, outcome.</param>
/// <param name="DedupKey">The once-only key, when the action has one. See <see cref="PersistedAuditEvent.DedupKey"/>.</param>
public sealed record AuditEntry(
    string EventType,
    string? ActorPlayerId,
    IReadOnlyList<string?> SubjectIds,
    object Payload,
    string? DedupKey = null);

/// <summary>
/// The append-only record of every state-changing action the server takes.
///
/// <para>DELIBERATELY BEST-EFFORT ON WRITE, and this is the single most important decision in the file.
/// <see cref="RecordAsync"/> never throws. The alternative — failing the action when its log write fails —
/// is not the safe direction here, it is the catastrophic one: these calls sit on money paths whose catch
/// blocks UNWIND IN-MEMORY STATE. A throw out of the daily claim's log would restore <c>LastClaimDay</c>
/// over a durable consume and let the same day be paid twice; a throw out of the stud reveal's would
/// release the once-only stud-fee latch after the sats had already left the treasury. An audit log that can
/// abort a settled payout converts a logging outage into a double-spend, and there is no amount of missing
/// history worth that.</para>
///
/// <para>What "must not silently fail" therefore means here is that the failure is COUNTED and NAMED, never
/// swallowed: <see cref="WriteFailures"/> is a monotonic counter served on the audit endpoint (so an
/// operator can see the log has gone deaf without grepping), and every failure is logged at ERROR with the
/// event type, actor and subjects — so the lost entry is reconstructible from the application log. A
/// non-zero count means history is now incomplete; it never means a sat moved wrongly.</para>
/// </summary>
public interface IAuditLog
{
    /// <summary>Append one event. Never throws — see the type doc-comment for why that is the safe
    /// direction on a money path. Silently absorbs a repeat of an entry carrying a
    /// <see cref="AuditEntry.DedupKey"/> already written, so a retried action logs once.</summary>
    Task RecordAsync(AuditEntry entry);

    /// <summary>Read a page of the log in append order, optionally narrowed to one subject id, event type
    /// or actor. <paramref name="afterSequence"/> is exclusive, so paging is
    /// <c>after = last sequence seen</c> and can never skip or repeat an event.</summary>
    Task<IReadOnlyList<Shared.AuditEventDto>> ReadAsync(
        long afterSequence, int take, string? subjectId, string? eventType, string? actorPlayerId,
        CancellationToken ct = default);

    /// <summary>How many log writes have been dropped since this server started. See the type doc-comment:
    /// the drop is deliberate and cannot be removed, so this counter is the only way it surfaces as a
    /// number. NOT persisted, for the obvious reason that persistence is the thing that is failing.</summary>
    long WriteFailures { get; }
}

/// <summary>No audit log — the behaviour with no <c>Game:StateDbPath</c> configured, where nothing else is
/// durable either and there is no database to append to.</summary>
public sealed class NullAuditLog : IAuditLog
{
    public Task RecordAsync(AuditEntry entry) => Task.CompletedTask;

    public Task<IReadOnlyList<Shared.AuditEventDto>> ReadAsync(
        long afterSequence, int take, string? subjectId, string? eventType, string? actorPlayerId,
        CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Shared.AuditEventDto>>([]);

    public long WriteFailures => 0;
}

/// <summary>SQLite-backed, in the same database file as the rest of the durable state (so it shares one
/// migration pipeline and one backup). Uses the context FACTORY because its consumers are singletons.</summary>
public sealed class SqliteAuditLog(
    IDbContextFactory<GameStateDbContext> factory, ILogger<SqliteAuditLog>? logger = null) : IAuditLog
{
    /// <summary>The largest page the read endpoint will serve. The table grows forever by design, so an
    /// unbounded read is a way to fall over on your own history.</summary>
    public const int MaxPageSize = 500;

    private long _writeFailures;
    public long WriteFailures => Interlocked.Read(ref _writeFailures);

    public async Task RecordAsync(AuditEntry entry)
    {
        try
        {
            // NOTE: no CancellationToken, deliberately. Every caller is on a request path holding a token
            // that trips when the browser tab closes — and the moment worth logging is exactly the one
            // AFTER the sats moved, when a client that got what it wanted may well have gone away. A log
            // a disconnect can erase is not a log. These are single-row local SQLite inserts.
            await using var db = await factory.CreateDbContextAsync();

            // The common retry, answered by an indexed lookup on the unique key rather than by an
            // in-process cache. A cache would be a memory leak with no upside: it would hold one string per
            // logged action for the life of the process on a log designed to grow forever, and it would not
            // be the guarantee anyway — the UNIQUE index below is, which is what makes the answer correct
            // across a restart and across two processes sharing the file.
            if (entry.DedupKey is { } key
                && await db.AuditEvents.AsNoTracking().AnyAsync(e => e.DedupKey == key))
                return;

            var row = new PersistedAuditEvent
            {
                AtUtc = DateTimeOffset.UtcNow,
                ActorPlayerId = entry.ActorPlayerId,
                EventType = entry.EventType,
                PayloadJson = JsonSerializer.Serialize(entry.Payload),
                DedupKey = entry.DedupKey,
            };
            foreach (var subject in entry.SubjectIds
                         .Where(s => !string.IsNullOrWhiteSpace(s))
                         .Select(s => s!)
                         .Distinct(StringComparer.Ordinal))
                row.Subjects.Add(new PersistedAuditSubject { SubjectId = subject });

            db.AuditEvents.Add(row);
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // The UNIQUE dedup index firing on a concurrent retry that raced past the lookup above: this
            // action IS recorded, by whoever won the race. Not a failure, and deliberately not counted as
            // one — this catch is the half of the guarantee the lookup cannot make on its own.
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _writeFailures);
            // ERROR, with everything needed to reconstruct the lost entry by hand. This is the ONLY place
            // the drop surfaces as prose; WriteFailures is the same fact as a number.
            logger?.LogError(ex,
                "AUDIT WRITE LOST: {EventType} by {Actor} touching {Subjects} was NOT recorded. The action "
                + "itself completed — the log is now incomplete, not the game state. Payload: {Payload}",
                entry.EventType, entry.ActorPlayerId ?? "(system)",
                string.Join(",", entry.SubjectIds.Where(s => !string.IsNullOrWhiteSpace(s))),
                SafeSerialize(entry.Payload));
        }
    }

    public async Task<IReadOnlyList<Shared.AuditEventDto>> ReadAsync(
        long afterSequence, int take, string? subjectId, string? eventType, string? actorPlayerId,
        CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        // AsNoTracking throughout: nothing read out of the log is ever a candidate for being written back.
        var query = db.AuditEvents.AsNoTracking().Where(e => e.Sequence > afterSequence);
        if (!string.IsNullOrWhiteSpace(eventType)) query = query.Where(e => e.EventType == eventType);
        if (!string.IsNullOrWhiteSpace(actorPlayerId)) query = query.Where(e => e.ActorPlayerId == actorPlayerId);
        if (!string.IsNullOrWhiteSpace(subjectId))
            query = query.Where(e => e.Subjects.Any(s => s.SubjectId == subjectId));

        var rows = await query
            .OrderBy(e => e.Sequence)
            .Take(Math.Clamp(take, 1, MaxPageSize))
            .Select(e => new
            {
                e.Sequence, e.AtUtc, e.ActorPlayerId, e.EventType, e.PayloadJson,
                Subjects = e.Subjects.Select(s => s.SubjectId).ToList(),
            })
            .ToListAsync(ct);

        return rows.Select(r => new Shared.AuditEventDto(
                r.Sequence, r.AtUtc.ToUnixTimeSeconds(), r.ActorPlayerId, r.EventType,
                r.Subjects.OrderBy(s => s, StringComparer.Ordinal).ToList(), r.PayloadJson))
            .ToList();
    }

    /// <summary>
    /// SQLITE_CONSTRAINT_UNIQUE (2067) — the dedup INDEX refusing a second row under the same key, which is
    /// the one constraint failure that means "already recorded" rather than "this write was wrong".
    ///
    /// The EXTENDED code, deliberately. The primary code (SQLITE_CONSTRAINT, 19) is shared by every
    /// constraint in the schema — NOT NULL, foreign key, primary key, check — so matching on it would let a
    /// genuinely broken write (a subject row failing its composite primary key, say, which reports 1555)
    /// be swallowed as a benign dedup hit and never reach <see cref="WriteFailures"/>. A log that hides its
    /// own failures behind the mechanism that makes it correct is worse than one that simply drops rows.
    /// </summary>
    private const int SqliteConstraintUnique = 2067;

    private static bool IsUniqueViolation(DbUpdateException ex)
        => ex.InnerException is Microsoft.Data.Sqlite.SqliteException
        { SqliteExtendedErrorCode: SqliteConstraintUnique };

    /// <summary>Serializing the payload for the failure log must not be able to throw a SECOND time and
    /// take out the only trace of the lost event.</summary>
    private static string SafeSerialize(object payload)
    {
        try { return JsonSerializer.Serialize(payload); }
        catch (Exception ex) { return $"(unserializable: {ex.GetType().Name})"; }
    }
}
