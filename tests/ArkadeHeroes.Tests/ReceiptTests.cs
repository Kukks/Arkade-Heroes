using ArkadeHeroes.Client.Sdk;
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

    private static ECPrivKey NewKey()
        => ECPrivKey.Create(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    /// <summary>Signs a receipt with <paramref name="key"/> and embeds that key as the claimed signer.</summary>
    private static (ProgressionReceiptDto Receipt, string PubKeyHex) SignedBy(ECPrivKey key)
    {
        Span<byte> pub = stackalloc byte[32];
        key.CreateXOnlyPubKey().WriteToSpan(pub);
        var hex = Convert.ToHexString(pub).ToLowerInvariant();
        var unsigned = Unsigned() with { GameSignerKeyHex = hex };
        return (unsigned with { SignatureHex = ReceiptVerifier.Sign(unsigned, key) }, hex);
    }

    // The hole a bare signature check cannot see: a forger mints their own key and signs
    // whatever progression they like. The receipt is internally self-consistent, so Verify
    // says yes. Only comparing against the key the arena advertises separates "signed"
    // from "signed by the arena" — that anchor is what makes a receipt portable evidence.
    [Fact]
    public void SelfSignedReceipt_PassesBareVerify_ButIsNotTrustedAgainstTheArenaKey()
    {
        var (_, arenaKeyHex) = SignedBy(NewKey());
        var (forged, forgerKeyHex) = SignedBy(NewKey());

        Assert.NotEqual(arenaKeyHex, forgerKeyHex);
        Assert.True(ReceiptVerifier.Verify(forged).Ok);   // internally sound — this is the trap

        var (trust, detail) = ReceiptVerifier.VerifyAgainst(forged, arenaKeyHex);
        Assert.Equal(ReceiptTrust.UnknownSigner, trust);
        Assert.Contains("unrecognised", detail);
    }

    [Fact]
    public void ReceiptSignedByTheArena_IsTrusted()
    {
        var key = NewKey();
        var (receipt, arenaKeyHex) = SignedBy(key);

        // Case-insensitive: the advertised key is hex and casing is not part of the identity.
        Assert.Equal(ReceiptTrust.Verified, ReceiptVerifier.VerifyAgainst(receipt, arenaKeyHex).Trust);
        Assert.Equal(ReceiptTrust.Verified, ReceiptVerifier.VerifyAgainst(receipt, arenaKeyHex.ToUpperInvariant()).Trust);
    }

    // Not knowing the arena's key is an absence of evidence, not evidence of forgery.
    // It must never read as a pass (fail-open) nor as an accusation (a false alarm on
    // a receipt that is perfectly good) — it is its own answer.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WithoutAnAnchor_TrustIsUnknown_NotGrantedAndNotAccused(string? advertised)
    {
        var (receipt, _) = SignedBy(NewKey());

        var (trust, detail) = ReceiptVerifier.VerifyAgainst(receipt, advertised);
        Assert.Equal(ReceiptTrust.NoAnchor, trust);
        Assert.NotEqual(ReceiptTrust.Verified, trust);
        Assert.DoesNotContain("forged", detail);
        Assert.DoesNotContain("unrecognised", detail);
    }

    // A broken signature outranks the anchor question: report the tampering, not the key.
    [Fact]
    public void TamperedReceipt_ReadsAsInvalid_EvenWhenTheSignerKeyMatches()
    {
        var (receipt, arenaKeyHex) = SignedBy(NewKey());
        var tampered = receipt with { XpAwardA = 9_999 };

        var (trust, detail) = ReceiptVerifier.VerifyAgainst(tampered, arenaKeyHex);
        Assert.Equal(ReceiptTrust.Invalid, trust);
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
    public void ReplayLevel_SeedsFromAMergeReceiptGenesisLevel()
    {
        // A merge receipt: fused hero "fused" is the RESULT (ResultHeroId), inheriting the
        // base's level 7 — carried in LevelA (position 11). ReplayLevel ignores seed/commitment
        // for level math, so dummy strings are fine here.
        var merge = new ProgressionReceiptDto(
            "merge", "mrg-1", "base", "sac", "fused",
            "seed", "n", "commit",
            0, 0, /*LevelA*/ 7, /*LevelB*/ 3, 1000, "key", "sig");
        Assert.Equal(7, ReceiptVerifier.ReplayLevel("fused", [merge]));

        // A hero with no merge-genesis receipt still starts at 1 (breeding/gen-0 unaffected).
        Assert.Equal(1, ReceiptVerifier.ReplayLevel("someone-else", [merge]));

        // A later match win folds ON TOP of the inherited genesis level, not from 1.
        var win = new ProgressionReceiptDto(
            "match", "m1", "fused", "opp", "fused",
            "seed", "n", "commit",
            Leveling.XpToNext(7) + 5, 0, 8, 1, 2000, "key", "sig");
        Assert.True(ReceiptVerifier.ReplayLevel("fused", [merge, win]) > 7);
    }

    [Fact]
    public void ReplayLevel_FoldsGauntletXp_ButNotFriendly()
    {
        // F1: a "gauntlet" (PvE) receipt awards XP toward the hero's level — folded exactly like a
        // "match". A "friendly" (unstaked spar) awards none and must NOT move the level. Same XP on
        // both, so the ONLY thing under test is which receipt types ReplayLevel folds.
        var bigXp = Leveling.XpToNext(1) + 25;   // enough to reach level 2 on its own
        ProgressionReceiptDto One(string type) => new(
            type, "g1", "hero-x", "", null,
            "seed", "n", "commit",
            bigXp, 0, 2, 1, 1000, "key", "sig");

        Assert.Equal(2, ReceiptVerifier.ReplayLevel("hero-x", [One("gauntlet")]));
        Assert.Equal(1, ReceiptVerifier.ReplayLevel("hero-x", [One("friendly")]));
    }

    [Fact]
    public async Task FightIssuesAVerifiableReceipt_AndLevelsReplayFromTheChain()
    {
        var (alice, _) = await _factory.RegisterAsync("R-Alice");
        var (bob, _) = await _factory.RegisterAsync("R-Bob");
        var aliceHeroes = await alice.ClaimStartersAsync();
        var bobHeroes = await bob.ClaimStartersAsync();

        var chainInfo = await alice.Chain.InfoAsync();
        Assert.False(string.IsNullOrEmpty(chainInfo.GameSignerKey));

        // Friendly fight → receipt in the response.
        var open = await alice.Matches.OpenAsync(new OpenMatchRequest(aliceHeroes[0].Id, bobHeroes[0].Id));
        var fight = await alice.Matches.FightAsync(open.MatchId, new FightRequest("receipt-nonce"));

        Assert.NotNull(fight.Receipt);
        Assert.Equal(chainInfo.GameSignerKey, fight.Receipt!.GameSignerKeyHex);
        var (ok, detail) = ReceiptVerifier.Verify(fight.Receipt);
        Assert.True(ok, detail);

        // The hero's public receipt chain replays to its server-side level.
        foreach (var heroId in new[] { aliceHeroes[0].Id, bobHeroes[0].Id })
        {
            var chain = await alice.Receipts.ForHeroAsync(heroId);
            Assert.NotEmpty(chain);
            var hero = await alice.Heroes.GetAsync(heroId);
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
