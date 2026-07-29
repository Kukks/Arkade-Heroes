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
}
