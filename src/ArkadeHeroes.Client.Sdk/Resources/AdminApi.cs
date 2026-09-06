using ArkadeHeroes.Shared;

namespace ArkadeHeroes.Client.Sdk;

/// <summary>
/// The operator console (<c>/api/admin/*</c>) — an authenticated analytics read plus three management
/// actions. Every method takes the admin secret EXPLICITLY and sends it on that one request: the token is
/// never stored on the client and never becomes a default header, so an ordinary call made later cannot
/// carry it by accident. If the server has no <c>Game:AdminToken</c> configured these routes do not exist
/// and every call here fails with the server's 404.
/// </summary>
public sealed class AdminApi(ArkadeHeroesClient client)
{
    /// <summary>The full analytics picture in one read — economy health (tripwires included), players,
    /// hero supply by generation and rarity, market, flow backlogs, the season, and the brackets.
    /// Pure observation: it never reconciles, settles or pays.</summary>
    public Task<AdminOverviewDto> OverviewAsync(string adminToken) =>
        client.SendWithAdminTokenAsync<AdminOverviewDto>(HttpMethod.Get, "/api/admin/overview", adminToken);

    /// <summary>Refunds a STRANDED bracket — one that can never resolve. Every cleared buy-in goes back to
    /// its entrant; the server refuses a bracket that can still be played.</summary>
    public Task<TournamentRefundResponse> RefundTournamentAsync(string adminToken, string tournamentId) =>
        client.SendWithAdminTokenAsync<TournamentRefundResponse>(
            HttpMethod.Post, $"/api/admin/tournaments/{tournamentId}/refund", adminToken);

    /// <summary>Expires covenant matches abandoned past their refund window. Moves no money — it flips a
    /// status so each player's own wallet can reclaim its stake.</summary>
    public Task<AdminActionResultDto> ReconcileMatchesAsync(string adminToken) =>
        client.SendWithAdminTokenAsync<AdminActionResultDto>(
            HttpMethod.Post, "/api/admin/actions/reconcile-matches", adminToken);

    /// <summary>Settles any season that has ended but not been paid — the same idempotent settle a public
    /// read of the season board already triggers.</summary>
    public Task<AdminActionResultDto> SettleSeasonsAsync(string adminToken) =>
        client.SendWithAdminTokenAsync<AdminActionResultDto>(
            HttpMethod.Post, "/api/admin/actions/settle-seasons", adminToken);

    /// <summary>
    /// One page of the append-only audit log, in append order. <paramref name="after"/> is EXCLUSIVE and
    /// is the cursor: pass back <c>AuditPageDto.NextAfter</c> to walk forward without skipping or
    /// repeating an event. The optional filters narrow to one subject id, event type or actor.
    ///
    /// A pure read of history — it changes nothing, and it moves no money.
    /// </summary>
    public Task<AuditPageDto> AuditAsync(
        string adminToken, long after = 0, int take = 100,
        string? subject = null, string? type = null, string? actor = null)
    {
        var query = $"?after={after}&take={take}";
        if (subject is not null) query += $"&subject={Uri.EscapeDataString(subject)}";
        if (type is not null) query += $"&type={Uri.EscapeDataString(type)}";
        if (actor is not null) query += $"&actor={Uri.EscapeDataString(actor)}";
        return client.SendWithAdminTokenAsync<AuditPageDto>(HttpMethod.Get, $"/api/admin/audit{query}", adminToken);
    }

    /// <summary>Everything that ever happened to ONE subject — a hero, a match, a death-match, an offer, a
    /// tournament, a stud proposal, a player — in the order it happened.</summary>
    public Task<AuditPageDto> AuditForSubjectAsync(
        string adminToken, string subjectId, long after = 0, int take = 100) =>
        client.SendWithAdminTokenAsync<AuditPageDto>(
            HttpMethod.Get,
            $"/api/admin/audit/subjects/{Uri.EscapeDataString(subjectId)}?after={after}&take={take}",
            adminToken);

    /// <summary>Payouts that did not complete cleanly. A pure READ by design: there is no retry call, because
    /// a <c>paid-not-booked</c> row is one the player was ALREADY paid and re-sending pays twice.</summary>
    public Task<PayoutFailurePageDto> PayoutFailuresAsync(
        string adminToken, long after = 0, int take = 100, string? outcome = null, string? player = null)
    {
        var query = $"?after={after}&take={take}";
        if (outcome is not null) query += $"&outcome={Uri.EscapeDataString(outcome)}";
        if (player is not null) query += $"&player={Uri.EscapeDataString(player)}";
        return client.SendWithAdminTokenAsync<PayoutFailurePageDto>(
            HttpMethod.Get, $"/api/admin/payout-failures{query}", adminToken);
    }
}
