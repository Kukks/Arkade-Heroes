using ArkadeHeroes.Shared;

namespace ArkadeHeroes.Client.Sdk;

/// <summary>Player registration, wallet login, and profile lookups. Register/Login set the client's bearer token on success.</summary>
public sealed class PlayersApi(ArkadeHeroesClient client)
{
    public async Task<PlayerDto> RegisterAsync(RegisterPlayerRequest req)
    {
        var player = await client.PostAsync<PlayerDto>("/api/players", req);
        if (player.Token is not null) client.SetAuthToken(player.Token);
        return player;
    }

    public async Task<PlayerDto> LoginAsync(LoginRequest req)
    {
        var player = await client.PostAsync<PlayerDto>("/api/players/login", req);
        if (player.Token is not null) client.SetAuthToken(player.Token);
        return player;
    }

    public Task<LoginChallengeResponse> LoginChallengeAsync() =>
        client.GetAsync<LoginChallengeResponse>("/api/players/login-challenge");

    public Task<PlayerDto> MeAsync() => client.GetAsync<PlayerDto>("/api/players/me");

    /// <summary>What the server has on file for this player's Terms of Use acceptance, and the version it
    /// currently requires — the source of truth the browser gate reads.</summary>
    public Task<TermsAcceptanceDto> TermsAsync() => client.GetAsync<TermsAcceptanceDto>("/api/players/me/terms");

    /// <summary>Record this player's explicit acceptance of the Terms of Use at a given version.</summary>
    public Task<TermsAcceptanceDto> AcceptTermsAsync(int version) =>
        client.PostAsync<TermsAcceptanceDto>("/api/players/me/terms", new AcceptTermsRequest(version));

    /// <summary>The signed-in player's derived accomplishments + unlocked badges.</summary>
    public Task<PlayerAchievementsDto> AchievementsAsync() => client.GetAsync<PlayerAchievementsDto>("/api/players/me/achievements");

    /// <summary>Season-pass standing — points, tier and titles earned this season. Titles only, never sats.</summary>
    public Task<SeasonPassProgress> SeasonPassAsync() => client.GetAsync<SeasonPassProgress>("/api/players/me/season-pass");

    /// <summary>Escrows of this player's that may still hold assets with no path forward — the recovery
    /// list. Discovery only: reclaiming is a covenant spend from the player's own wallet.</summary>
    public Task<List<ReclaimableDto>> ReclaimableAsync() =>
        client.GetAsync<List<ReclaimableDto>>("/api/players/me/reclaimable");

    public Task<PlayerDto> GetAsync(string playerId) => client.GetAsync<PlayerDto>($"/api/players/{playerId}");

    /// <summary>Any player's public trophy case — season standing, achievements and best heroes. No auth needed.</summary>
    public Task<PlayerProfileDto> ProfileAsync(string playerId) =>
        client.GetAsync<PlayerProfileDto>($"/api/players/{playerId}/profile");
}
