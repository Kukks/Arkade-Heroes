using System.Net.Http.Json;
using ArkadeHeroes.Core.Fairness;
using ArkadeHeroes.Core.Progression;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Mvc.Testing;
using NBitcoin.Secp256k1;

namespace ArkadeHeroes.Tests;

public class ReceiptTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ReceiptTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private static ProgressionReceiptDto Unsigned()
    {
        var seed = CommitReveal.NewSeed();
        return new ProgressionReceiptDto(
            "match", "match-1", "hero-a", "hero-b", "hero-a",
            Convert.ToHexString(seed).ToLowerInvariant(), "nonce-1", CommitReveal.Commit(seed),
            40, -40, 1, 1, 1_760_000_000, "", "");
    }

    [Fact]
    public void SignedReceiptVerifies_TamperedReceiptDoesNot()
    {
        var key = ECPrivKey.Create(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        Span<byte> pub = stackalloc byte[32];
        key.CreateXOnlyPubKey().WriteToSpan(pub);

        var unsigned = Unsigned() with { GameSignerKeyHex = Convert.ToHexString(pub).ToLowerInvariant() };
        var receipt = unsigned with { SignatureHex = ReceiptVerifier.Sign(unsigned, key) };

        Assert.True(ReceiptVerifier.Verify(receipt).Ok);

        var tampered = receipt with { XpAwardA = 9_999 };
        var (ok, detail) = ReceiptVerifier.Verify(tampered);
        Assert.False(ok);
        Assert.Contains("tampered", detail);
    }

    [Fact]
    public void ReplayLevel_FoldsSignedDeltas_IncludingDelevelOnLoss()
    {
        // A receipt records the SIGNED XP delta the fight applied: the winner's is
        // positive, the loser's (HeroB) is the exact mirror — XP is conserved.
        ProgressionReceiptDto Match(int i, long heroXDelta, long ts)
        {
            var seed = CommitReveal.NewSeed();
            return new ProgressionReceiptDto(
                "match", $"m{i}", "hero-x", $"opp{i}", heroXDelta >= 0 ? "hero-x" : $"opp{i}",
                Convert.ToHexString(seed).ToLowerInvariant(), "n", CommitReveal.Commit(seed),
                heroXDelta, -heroXDelta, 1, 1, ts, "k", "s");
        }

        // A staked win big enough to reach level 2, then a loss of equal size.
        var swing = Leveling.XpToNext(1) + 30;
        var afterWin = new[] { Match(0, swing, 1000) };
        var afterWinThenLoss = new[] { Match(0, swing, 1000), Match(1, -swing, 1001) };

        // Replay == a manual fold of the same signed deltas via the leveling math.
        var lvl = 1; long xp = 0;
        (lvl, xp, _) = Leveling.Apply(lvl, xp, swing);
        Assert.Equal(2, lvl); // the win reached level 2
        Assert.Equal(lvl, ReceiptVerifier.ReplayLevel("hero-x", afterWin));

        // The losable ladder: the following loss pulls the level back down.
        Assert.True(ReceiptVerifier.ReplayLevel("hero-x", afterWinThenLoss)
                    < ReceiptVerifier.ReplayLevel("hero-x", afterWin));
        Assert.Equal(1, ReceiptVerifier.ReplayLevel("hero-x", afterWinThenLoss));

        // The empty chain (an unknown hero) holds at the starting level.
        Assert.Equal(1, ReceiptVerifier.ReplayLevel("unknown-hero", afterWinThenLoss));
    }

    [Fact]
    public async Task FightIssuesAVerifiableReceipt_AndLevelsReplayFromTheChain()
    {
        var (alice, _) = await _factory.RegisterAsync("R-Alice");
        var (bob, _) = await _factory.RegisterAsync("R-Bob");
        var aliceHeroes = await alice.ClaimStartersAsync();
        var bobHeroes = await bob.ClaimStartersAsync();

        var chainInfo = (await alice.GetFromJsonAsync<ChainInfoDto>("/api/chain/info"))!;
        Assert.False(string.IsNullOrEmpty(chainInfo.GameSignerKey));

        // Friendly fight → receipt in the response.
        var open = (await (await alice.PostAsJsonAsync("/api/matches/open",
                new OpenMatchRequest(aliceHeroes[0].Id, bobHeroes[0].Id)))
            .Content.ReadFromJsonAsync<OpenMatchResponse>())!;
        var fight = (await (await alice.PostAsJsonAsync($"/api/matches/{open.MatchId}/fight",
                new FightRequest("receipt-nonce")))
            .Content.ReadFromJsonAsync<FightResponse>())!;

        Assert.NotNull(fight.Receipt);
        Assert.Equal(chainInfo.GameSignerKey, fight.Receipt!.GameSignerKeyHex);
        var (ok, detail) = ReceiptVerifier.Verify(fight.Receipt);
        Assert.True(ok, detail);

        // The hero's public receipt chain replays to its server-side level.
        foreach (var heroId in new[] { aliceHeroes[0].Id, bobHeroes[0].Id })
        {
            var chain = (await alice.GetFromJsonAsync<List<ProgressionReceiptDto>>($"/api/receipts/hero/{heroId}"))!;
            Assert.NotEmpty(chain);
            var hero = (await alice.GetFromJsonAsync<HeroDto>($"/api/heroes/{heroId}"))!;
            Assert.Equal(hero.Level, ReceiptVerifier.ReplayLevel(heroId, chain));
        }

        // Breeding issues a receipt too.
        var (_, reveal) = await alice.BreedAsync(aliceHeroes[0].Id, aliceHeroes[1].Id, "receipt-breed");
        Assert.NotNull(reveal.Receipt);
        Assert.True(ReceiptVerifier.Verify(reveal.Receipt!).Ok);
        Assert.Equal("breeding", reveal.Receipt!.Type);
        Assert.Equal(reveal.Hero.Id, reveal.Receipt.ResultHeroId);
    }
}
