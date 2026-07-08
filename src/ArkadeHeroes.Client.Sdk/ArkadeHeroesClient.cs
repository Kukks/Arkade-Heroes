using System.Net.Http.Headers;
using System.Net.Http.Json;
using ArkadeHeroes.Shared;

namespace ArkadeHeroes.Client.Sdk;

/// <summary>Thrown when the game API returns a non-2xx response; carries the server's ErrorResponse.Error text.</summary>
public sealed class ArkadeHeroesApiException(string message) : Exception(message);

/// <summary>
/// A thin, typed, transport-only client over the Arkade Heroes HTTP API. Wraps an
/// injected <see cref="HttpClient"/> (the caller owns its lifetime + base address);
/// centralises bearer auth and error handling. Contains NO wallet/covenant logic —
/// those stay in ArkadeHeroes.Chain, orchestrated by the caller.
/// </summary>
public sealed class ArkadeHeroesClient
{
    private readonly HttpClient _http;

    public ArkadeHeroesClient(HttpClient http)
    {
        _http = http;
        Players = new PlayersApi(this);
        Heroes = new HeroesApi(this);
        Breeding = new BreedingApi(this);
        Merge = new MergeApi(this);
        DeathMatch = new DeathMatchApi(this);
        Matches = new MatchesApi(this);
        Offers = new OffersApi(this);
        Items = new ItemsApi(this);
        Chain = new ChainApi(this);
        Leaderboard = new LeaderboardApi(this);
        Receipts = new ReceiptsApi(this);
        Dev = new DevApi(this);
    }

    public PlayersApi Players { get; }
    public HeroesApi Heroes { get; }
    public BreedingApi Breeding { get; }
    public MergeApi Merge { get; }
    public DeathMatchApi DeathMatch { get; }
    public MatchesApi Matches { get; }
    public OffersApi Offers { get; }
    public ItemsApi Items { get; }
    public ChainApi Chain { get; }
    public LeaderboardApi Leaderboard { get; }
    public ReceiptsApi Receipts { get; }
    public DevApi Dev { get; }

    /// <summary>Sets the bearer token used for all subsequent requests (Register/Login call this on success).</summary>
    public void SetAuthToken(string token) =>
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    public void ClearAuthToken() => _http.DefaultRequestHeaders.Authorization = null;

    internal async Task<T> GetAsync<T>(string path) => await ReadAsync<T>(await _http.GetAsync(path));

    internal async Task<T> PostAsync<T>(string path, object? body = null) =>
        await ReadAsync<T>(body is null
            ? await _http.PostAsync(path, null)
            : await _http.PostAsJsonAsync(path, body));

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
            throw new ArkadeHeroesApiException(error?.Error ?? $"server returned {(int)response.StatusCode}");
        }
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
}
