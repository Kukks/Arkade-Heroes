using Microsoft.EntityFrameworkCore;

namespace ArkadeHeroes.Server.Persistence;

/// <summary>
/// One payout that did NOT complete cleanly, and what an operator must do about it.
///
/// <para>The three money paths that write here — the tournament podium prize, the tournament
/// stranded-bracket refund, and the season prize — are all documented as NEVER retried. Before this table
/// the only trace was a log line, which rotates away and cannot be queried, so real sats owed to a NAMED
/// player were effectively unrecoverable the moment the log aged out.</para>
///
/// <para>DELIBERATELY NOT THE AUDIT LOG, and the reason is structural rather than tidiness. The audit log is
/// append-only enforced by SQLite triggers (see the <c>AppendOnlyAuditLog</c> migration), which is exactly
/// right for history and exactly wrong for a debt: a debt has a LIFECYCLE — it is owed until somebody
/// settles it — and a row that can never be updated can never be marked settled without a migration that
/// drops the guarantee off the whole log. Two further reasons: the audit payload is free-form JSON, so
/// "what do we owe right now" would be a string scan of a table designed to grow forever rather than an
/// indexed read; and the outcome below has to be a first-class queryable column, because getting it wrong
/// costs a double payment. This table sits BESIDE the audit log in the same database file, so it shares the
/// one migration pipeline and the one backup.</para>
///
/// <para>The <see cref="PayoutTag"/> is the reconciliation key: it names the exact prize or refund
/// (<c>tournament:{bracket}:rank1</c>, <c>season:{n}:rank1</c>, <c>tournament-refund:{bracket}:{player}</c>).
/// There is no UNIQUE index on it, matching the treasury ledger's own reasoning — a key that collapsed two
/// rows would silently drop the second of two real facts. It is not needed as a guard either: all three
/// flows commit their durable "resolved"/"refunded"/"settled" marker BEFORE a sat moves, so each tag is
/// attempted exactly once.</para>
/// </summary>
public class PersistedPayoutFailure
{
    /// <summary>The monotonic sequence — a SQLite AUTOINCREMENT rowid, so it strictly increases for the life
    /// of the database and gives the table a total order to page on.</summary>
    public long Id { get; set; }

    /// <summary>When the payout was attempted, in UTC.</summary>
    public required DateTimeOffset AtUtc { get; set; }

    /// <summary>The player the sats were meant for. Indexed: "what does this player claim we owe them" is
    /// the question an operator arrives with.</summary>
    public required string PlayerId { get; set; }

    /// <summary>How many sats the payout was for. Real bitcoin.</summary>
    public required long AmountSats { get; set; }

    /// <summary>The payout memo the chain call carried — see the type doc-comment.</summary>
    public required string PayoutTag { get; set; }

    /// <summary>One of the <see cref="PayoutFailureOutcome"/> constants: whether the sats moved, did not
    /// move, or could not be established. THE load-bearing column. Indexed, because the only useful query
    /// over this table is "everything still owed".</summary>
    public required string Outcome { get; set; }

    /// <summary>The invoice this hangs off where one exists — the buy-in whose paid-state could not be read
    /// on the refund path. NULL for the prize paths, which pay out of the treasury against no invoice.</summary>
    public string? InvoiceId { get; set; }

    /// <summary>The exception that caused it, type and message, for whoever has to work out WHY. Free text:
    /// nothing branches on it, it is there to be read.</summary>
    public string? Failure { get; set; }
}

/// <summary>
/// What a <see cref="PersistedPayoutFailure"/> row means. Constants rather than an enum so the stored string
/// is stable forever — a renumbered enum would silently re-label a debt, and re-labelling
/// <see cref="PaidNotBooked"/> as <see cref="Owed"/> is precisely how somebody gets paid twice.
/// </summary>
public static class PayoutFailureOutcome
{
    /// <summary>The payout call itself failed: the sats did NOT move and the player IS owed them. This is
    /// the row an operator settles by hand.</summary>
    public const string Owed = "owed";

    /// <summary>The payout SUCCEEDED and only the treasury book-keeping afterwards failed. The player
    /// already has the sats — DO NOT RE-PAY. Recorded because the treasury's outflow total now under-reports
    /// by this amount, which is a reconciliation job, not a payment one.</summary>
    public const string PaidNotBooked = "paid-not-booked";

    /// <summary>Whether anything is owed could not be established — the refund path could not read whether
    /// the buy-in ever cleared. Check <see cref="PersistedPayoutFailure.InvoiceId"/> by hand BEFORE paying
    /// anything: this is neither a confirmed debt nor a confirmed non-debt.</summary>
    public const string Unknown = "unknown";
}

/// <summary>What a caller hands the log. See <see cref="PersistedPayoutFailure"/> for what each field is
/// for.</summary>
/// <param name="PlayerId">The player the sats were meant for.</param>
/// <param name="AmountSats">How many sats.</param>
/// <param name="PayoutTag">The payout memo — the reconciliation key.</param>
/// <param name="Outcome">One of <see cref="PayoutFailureOutcome"/>.</param>
/// <param name="InvoiceId">The invoice this hangs off, where one exists.</param>
/// <param name="Failure">The exception behind it.</param>
public sealed record PayoutFailureEntry(
    string PlayerId,
    long AmountSats,
    string PayoutTag,
    string Outcome,
    string? InvoiceId = null,
    Exception? Failure = null);

/// <summary>
/// The durable record of every payout that did not complete cleanly.
///
/// <para>DELIBERATELY BEST-EFFORT ON WRITE, for the same reason <see cref="IAuditLog"/> is, only sharper.
/// <see cref="RecordAsync"/> never throws. Every call site is INSIDE a catch block, and two of the three are
/// inside a LOOP over podium places or bracket entrants: a throw out of here would escape the catch that was
/// containing one player's loss and abort the loop, so recording that rank 1's prize was dropped would cost
/// rank 2 and rank 3 theirs. Turning a database hiccup into more unpaid players is the one outcome this
/// table exists to prevent.</para>
///
/// <para>So a failed write is COUNTED and NAMED rather than swallowed: <see cref="WriteFailures"/> is a
/// monotonic counter served beside the rows, and the failure is logged at ERROR carrying the whole entry.
/// The original per-site log lines stay exactly where they are — they are the fallback for the case where
/// this table is the thing that is broken.</para>
///
/// <para>THIS IS A RECORD, NOT A QUEUE. Nothing here retries a payout, and nothing should: re-sending value
/// is a decision for a human looking at the outcome column, not for a process.</para>
/// </summary>
public interface IPayoutFailureLog
{
    /// <summary>Append one record. Never throws — see the type doc-comment for why that is the safe
    /// direction inside a catch block on a money path.</summary>
    Task RecordAsync(PayoutFailureEntry entry);

    /// <summary>Read a page in append order, optionally narrowed to one outcome or player.
    /// <paramref name="afterId"/> is exclusive, so paging is <c>after = last id seen</c>.</summary>
    Task<IReadOnlyList<PersistedPayoutFailure>> ReadAsync(
        long afterId, int take, string? outcome, string? playerId, CancellationToken ct = default);

    /// <summary>How many records have been dropped since this server started. Non-zero means a payout
    /// failed AND its record failed, so the application log is the only surviving trace of that one.</summary>
    long WriteFailures { get; }
}

/// <summary>No durable record — the behaviour with no <c>Game:StateDbPath</c> configured, where there is no
/// database to append to. The per-site ERROR logs still fire; they are all that exists in that mode.</summary>
public sealed class NullPayoutFailureLog : IPayoutFailureLog
{
    public Task RecordAsync(PayoutFailureEntry entry) => Task.CompletedTask;

    public Task<IReadOnlyList<PersistedPayoutFailure>> ReadAsync(
        long afterId, int take, string? outcome, string? playerId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<PersistedPayoutFailure>>([]);

    public long WriteFailures => 0;
}

/// <summary>SQLite-backed, in the same database file as the rest of the durable state. Uses the context
/// FACTORY because its consumer is a singleton.</summary>
public sealed class SqlitePayoutFailureLog(
    IDbContextFactory<GameStateDbContext> factory, ILogger<SqlitePayoutFailureLog>? logger = null)
    : IPayoutFailureLog
{
    /// <summary>The largest page the read endpoint will serve.</summary>
    public const int MaxPageSize = 500;

    private long _writeFailures;
    public long WriteFailures => Interlocked.Read(ref _writeFailures);

    public async Task RecordAsync(PayoutFailureEntry entry)
    {
        try
        {
            // NOTE: no CancellationToken, deliberately — the same call the audit log makes and for the same
            // reason. Every caller is on a request path holding a token that trips when the browser tab
            // closes, and the moment worth recording is exactly the one where something already went wrong.
            // A record a disconnect can erase is not a record.
            await using var db = await factory.CreateDbContextAsync();
            db.PayoutFailures.Add(new PersistedPayoutFailure
            {
                AtUtc = DateTimeOffset.UtcNow,
                PlayerId = entry.PlayerId,
                AmountSats = entry.AmountSats,
                PayoutTag = entry.PayoutTag,
                Outcome = entry.Outcome,
                InvoiceId = entry.InvoiceId,
                Failure = entry.Failure is null ? null : $"{entry.Failure.GetType().Name}: {entry.Failure.Message}",
            });
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _writeFailures);
            // ERROR, carrying everything the lost row held. This is the last line of defence for real sats:
            // the payout failed AND the record of it failed, so this message is the only trace left.
            logger?.LogError(ex,
                "PAYOUT FAILURE RECORD LOST: player {PlayerId}, {Sats} sats, {Tag}, outcome {Outcome}. The "
                + "payout outcome itself is unchanged — what is missing is the durable record of it, so this "
                + "line is now the ONLY trace. Reconcile by hand.",
                entry.PlayerId, entry.AmountSats, entry.PayoutTag, entry.Outcome);
        }
    }

    public async Task<IReadOnlyList<PersistedPayoutFailure>> ReadAsync(
        long afterId, int take, string? outcome, string? playerId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        // AsNoTracking: nothing read out of here is a candidate for being written back by this path.
        var query = db.PayoutFailures.AsNoTracking().Where(r => r.Id > afterId);
        if (!string.IsNullOrWhiteSpace(outcome)) query = query.Where(r => r.Outcome == outcome);
        if (!string.IsNullOrWhiteSpace(playerId)) query = query.Where(r => r.PlayerId == playerId);

        return await query
            .OrderBy(r => r.Id)
            .Take(Math.Clamp(take, 1, MaxPageSize))
            .ToListAsync(ct);
    }
}
