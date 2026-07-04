using System.Net.Http.Json;
using System.Text.Json;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ArkadeHeroes.Tests;

/// <summary>
/// Progression mirrored on-chain: resolving a match delivers the earned XP to
/// each hero owner as a fungible XP-asset balance (best-effort; the signed
/// receipts stay the verification root). Both winner and loser gain XP.
/// </summary>
public class XpAssetTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;

    public XpAssetTests()
    {
        Environment.SetEnvironmentVariable("Game__DeliverXpAssetsOnChain", "true");
        _factory = new WebApplicationFactory<Program>();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("Game__DeliverXpAssetsOnChain", null);
        _factory.Dispose();
    }

    private static async Task<ulong> XpAsync(HttpClient client)
    {
        var body = await client.GetStringAsync("/api/players/xp");
        return JsonDocument.Parse(body).RootElement.GetProperty("xp").GetUInt64();
    }

    [Fact]
    public async Task ResolvingAMatch_DeliversXpToBothOwners()
    {
        var (alice, _) = await _factory.RegisterAsync("Xp-Alice");
        var (bob, _) = await _factory.RegisterAsync("Xp-Bob");
        var aliceHeroes = await alice.ClaimStartersAsync();
        var bobHeroes = await bob.ClaimStartersAsync();

        Assert.Equal(0UL, await XpAsync(alice));
        Assert.Equal(0UL, await XpAsync(bob));

        // Covenant match: stake both, then resolve.
        var open = (await (await alice.PostAsJsonAsync("/api/matches/open",
                new OpenMatchRequest(aliceHeroes[0].Id, bobHeroes[0].Id, 3_000, "covenant")))
            .Content.ReadFromJsonAsync<OpenMatchResponse>())!;
        await alice.PostAsJsonAsync("/api/dev/stake-escrow", new { MatchId = open.MatchId });
        await bob.PostAsync($"/api/matches/{open.MatchId}/accept", null);
        await bob.PostAsJsonAsync("/api/dev/stake-escrow", new { MatchId = open.MatchId });

        var fight = (await (await alice.PostAsJsonAsync($"/api/matches/{open.MatchId}/fight",
                new FightRequest("xp-duel")))
            .Content.ReadFromJsonAsync<FightResponse>())!;
        Assert.NotNull(fight.Result);

        // XP is delivered in the background (a treasury spend off the duel's
        // hot path), so poll until both owners hold XP on-chain.
        async Task<ulong> WaitXpAsync(HttpClient c)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                if (await XpAsync(c) is var xp && xp > 0) return xp;
                await Task.Delay(100);
            }
            return 0;
        }
        Assert.True(await WaitXpAsync(alice) > 0, "alice earned no XP");
        Assert.True(await WaitXpAsync(bob) > 0, "bob earned no XP");
    }
}
