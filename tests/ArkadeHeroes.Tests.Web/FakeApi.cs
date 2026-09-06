using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ArkadeHeroes.Shared;

namespace ArkadeHeroes.Tests.Web;

/// <summary>
/// A route table standing in for the game server, wired underneath the REAL
/// <see cref="ArkadeHeroes.Client.Sdk.ArkadeHeroesClient"/>.
///
/// <para>Deliberately a transport-level fake rather than a mocked SDK: a page's bug and a page's
/// contract with the server are the same bug when the DTO shape drifts, so the tests go through the
/// SDK's own serialization. A canned response that no longer deserializes into the DTO the page reads
/// fails here exactly as it would in a browser.</para>
///
/// <para>Any route that was not registered answers 404 — an unstubbed call is a test-authoring mistake,
/// and a page that silently swallows it is the failure mode these tests exist to expose, so it must not
/// be papered over with a permissive default.</para>
/// </summary>
public sealed class FakeApi : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _routes = new();

    /// <summary>Per-route latency, so replies can overtake each other (see <see cref="GetSlow"/>).</summary>
    private readonly Dictionary<string, TimeSpan> _delays = new();

    /// <summary>Every path this handler was actually asked for, in order — lets a test assert that a page
    /// did NOT bill something (e.g. never POSTed a fee invoice just by being opened).</summary>
    public List<string> Requested { get; } = new();

    /// <summary>The same calls WITH their query strings. Separate because <see cref="Requested"/> is
    /// exact-matched by existing tests — but a filter living only in the query is invisible there.</summary>
    public List<string> RequestedUrls { get; } = new();

    private static string Key(HttpMethod method, string path) => $"{method.Method} {path}";

    /// <summary>Serve <paramref name="body"/> as JSON for GET <paramref name="path"/>.</summary>
    public FakeApi Get<T>(string path, T body)
    {
        _routes[Key(HttpMethod.Get, path)] = _ => Json(body);
        // Re-registering a route plainly also makes it fast again: swapping a slow answer for a quick one
        // mid-test is how a reply gets to overtake an earlier one still on the wire.
        _delays.Remove(Key(HttpMethod.Get, path));
        return this;
    }

    /// <summary>
    /// Serve GET <paramref name="path"/> only after <paramref name="delay"/>.
    ///
    /// <para>The seam for the one thing an instant fake cannot express: a response that was ISSUED first
    /// but ARRIVES last. A page that keeps whichever request returned last, rather than whichever it
    /// started last, shows the older answer — and no test can catch that while every route answers in
    /// zero time and replies can never overtake each other.</para>
    /// </summary>
    public FakeApi GetSlow<T>(string path, T body, TimeSpan delay)
    {
        _routes[Key(HttpMethod.Get, path)] = _ => Json(body);
        _delays[Key(HttpMethod.Get, path)] = delay;
        return this;
    }

    /// <summary>Serve <paramref name="body"/> as JSON for POST <paramref name="path"/>.</summary>
    public FakeApi Post<T>(string path, T body)
    {
        _routes[Key(HttpMethod.Post, path)] = _ => Json(body);
        return this;
    }

    /// <summary>
    /// Fail GET <paramref name="path"/> the way a real outage does. Used to prove a page tells the player
    /// its roster read FAILED rather than that they own nothing.
    /// </summary>
    public FakeApi GetFails(string path, HttpStatusCode status = HttpStatusCode.ServiceUnavailable)
    {
        _routes[Key(HttpMethod.Get, path)] = _ => new HttpResponseMessage(status)
        {
            Content = JsonContent.Create(new ErrorResponse("the arena is unreachable")),
        };
        return this;
    }

    private static HttpResponseMessage Json<T>(T body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            System.Text.Encoding.UTF8,
            "application/json"),
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        // Match on the path alone: pages append paging query strings (?skip=0&take=24) that a test has no
        // reason to restate, and the distinction those make is not what any of these tests are about.
        var path = request.RequestUri!.AbsolutePath;
        var key = Key(request.Method, path);
        Requested.Add($"{request.Method.Method} {path}");
        RequestedUrls.Add($"{request.Method.Method} {request.RequestUri.PathAndQuery}");

        // Read the latency at DISPATCH time, so a test can slow a route, fire a request, and then swap the
        // route out — which is how a slow old answer gets to race a fast new one.
        if (_delays.TryGetValue(key, out var delay)) await Task.Delay(delay, ct);

        if (_routes.TryGetValue(key, out var handler))
            return handler(request);

        return new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = JsonContent.Create(new ErrorResponse($"no stub for {request.Method} {path}")),
        };
    }

    /// <summary>An <see cref="HttpClient"/> on this handler, based at the origin the app is served from.</summary>
    public HttpClient CreateClient() => new(this, disposeHandler: false)
    {
        BaseAddress = new Uri("https://arena.test/"),
    };
}
