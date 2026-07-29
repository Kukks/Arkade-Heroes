using System.Security.Cryptography;
using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Core;
using ArkadeHeroes.Core.Fairness;
using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Heroes;
using ArkadeHeroes.Server;   // DtoMapper ToDto extensions
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ArkadeHeroes.Tests;

/// <summary>
/// The absorb verifier replays under the odds the SETTLEMENT ran on, resolved from its own
/// <see cref="GameConfigVersion"/> stamp — not under whatever <c>/api/chain/info</c> reports now.
///
/// This is the same bug class #148 removed from the other five verifiers, and the worst instance of it:
/// absorb decides whether a hero is permanently destroyed and who takes the stake, on real satoshis. The
/// odds are <c>GameOptions</c>-tunable AND verification-critical (they are inside the version id), so
/// retuning them made every historical absorb disagree with the receipt it was checking — an honest server
/// reading as "SERVER CHEATED", or a wrong genome reading as fine.
///
/// The odds deliberately do NOT ride the settle response as their own field: a server that could DECLARE
/// its own odds could declare whichever ones make a fabricated absorb recompute. The stamp is the only
/// carrier that stays trustless, because the client re-hashes what it is served.
/// </summary>
public class AbsorbConfigStampTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public AbsorbConfigStampTests(WebApplicationFactory<Program> factory) => _factory = factory;

    /// <summary>Rules whose ONLY difference from Default is the absorb odds — so any verification split
    /// between them is attributable to the odds and nothing else.</summary>
    private static GameConfig Retuned { get; } =
        GameConfig.Default with { Absorb = new AbsorbOdds(255, 255) };

    /// <summary>A winner with no expressed cosmetic traits and a loser expressing all six Legendary: six
    /// absorbable candidates, so the COUNT grafted — and therefore the minted genome — is a direct
    /// function of the odds.</summary>
    private static (Hero Winner, Hero Loser) Pair()
    {
        var w = new byte[Genome.Size];
        var l = new byte[Genome.Size];
        for (var i = 0; i < 16; i++) { w[i] = 180; l[i] = 150; }
        for (var cat = 0; cat < 6; cat++) l[16 + cat * 2] = 255;
        return (
            new Hero { Id = "w", OwnerId = "p", Name = "w", Genome = new Genome(w), Level = 20 },
            new Hero { Id = "l", OwnerId = "p", Name = "l", Genome = new Genome(l), Level = 20 });
    }

    /// <summary>A deterministic seed sweep for one whose outcome genuinely DIFFERS between the two configs —
    /// asserted by the caller, so a fixture that stopped diverging fails the test instead of quietly passing.</summary>
    private static byte[]? FindSeed(Func<byte[], bool> diverges, int tries = 400)
    {
        for (var i = 0; i < tries; i++)
        {
            var seed = SHA256.HashData(BitConverter.GetBytes(i));
            if (diverges(seed)) return seed;
        }
        return null;
    }

    [Fact]
    public void AMintedAbsorb_VerifiesOnlyUnderTheOddsItSettledUnder()
    {
        const string dmId = "stamp-absorb-mint";
        const string nonce = "stamp-absorb-mint-nonce";
        var (winner, loser) = Pair();

        AbsorbOutcome? settled = null;
        var seed = FindSeed(s =>
        {
            var e = CommitReveal.DeriveEntropy(s, dmId, winner.Id, loser.Id, nonce);
            var on = Absorb.Resolve(winner.Genome, loser.Genome, e, Retuned.Absorb);
            var off = Absorb.Resolve(winner.Genome, loser.Genome, e, GameConfig.Default.Absorb);
            // Require a REAL divergence in the minted genome, not merely a different roll count.
            if (!on.Minted || on.Result == off.Result) return false;
            settled = on;
            return true;
        });
        Assert.NotNull(seed);
        Assert.NotNull(settled);

        var commitment = CommitReveal.Commit(seed!);
        var entropy = CommitReveal.DeriveEntropy(seed!, dmId, winner.Id, loser.Id, nonce);
        var chal = winner.ToDto();
        var def = loser.ToDto();
        var stamp = GameConfigVersion.Compute(Retuned);

        // The stamp names rules that are NOT this client's compiled-in default — that is the whole point.
        Assert.NotEqual(GameConfigVersion.Default, stamp);

        // Resolved under the stamp → the honest settlement verifies.
        var resolved = GameRulesDto.From(Retuned).ToGameConfig();
        Assert.NotNull(resolved);
        Assert.Equal(stamp, GameConfigVersion.Compute(resolved!));
        var (ok, detail) = FairnessAudit.VerifyAbsorb(
            dmId, chal, def, challengerWon: true, nonce, commitment,
            settled!.Minted, settled.Result.ToHex(), Convert.ToHexString(seed!),
            Convert.ToHexString(entropy), resolved);
        Assert.True(ok, detail);

        // Under the CURRENT odds (the pre-fix path: whatever /api/chain/info reports now), the same
        // honest settlement reads as a cheat. This is the bug, pinned.
        Assert.False(FairnessAudit.VerifyAbsorb(
            dmId, chal, def, challengerWon: true, nonce, commitment,
            settled.Minted, settled.Result.ToHex(), Convert.ToHexString(seed!),
            Convert.ToHexString(entropy), GameConfig.Default).Ok);
    }

    [Fact]
    public void AKeepRoll_IsOddsSensitiveToo_SoAFabricatedAbsorbCannotHideInARetune()
    {
        // The mirror case, and the one that decides whether a hero SURVIVES: under odds that never fire,
        // the winner keeps its exact hero. Verified under odds that always fire, that honest keep would
        // read as a suppressed absorb — so the keep path needs the settle-time odds just as much.
        const string dmId = "stamp-absorb-keep";
        const string nonce = "stamp-absorb-keep-nonce";
        var (winner, loser) = Pair();
        var never = GameConfig.Default with { Absorb = new AbsorbOdds(0, 0) };

        var seed = SHA256.HashData("keep"u8.ToArray());
        var commitment = CommitReveal.Commit(seed);
        var entropy = CommitReveal.DeriveEntropy(seed, dmId, winner.Id, loser.Id, nonce);

        var kept = Absorb.Resolve(winner.Genome, loser.Genome, entropy, never.Absorb);
        Assert.False(kept.Minted);
        Assert.True(Absorb.Resolve(winner.Genome, loser.Genome, entropy, Retuned.Absorb).Minted);

        var chal = winner.ToDto();
        var def = loser.ToDto();
        var (ok, detail) = FairnessAudit.VerifyAbsorb(
            dmId, chal, def, challengerWon: true, nonce, commitment,
            minted: false, null, Convert.ToHexString(seed), Convert.ToHexString(entropy), never);
        Assert.True(ok, detail);

        Assert.False(FairnessAudit.VerifyAbsorb(
            dmId, chal, def, challengerWon: true, nonce, commitment,
            minted: false, null, Convert.ToHexString(seed), Convert.ToHexString(entropy), Retuned).Ok);
    }

    [Fact]
    public void AnUnstampedAbsorb_StillVerifiesUnderTheDefaultOdds()
    {
        // Every death-match settled before stamping existed carries no stamp and ran on GameConfig.Default
        // — AbsorbOdds.Default (102, 90), which is also what a stock server publishes on /api/chain/info,
        // so this is exactly what those artifacts verify under today. The stamp is additive, never a new
        // requirement: an omitted config resolves to Default, as ConfigApi.ResolveAsync("") does.
        Assert.Equal(AbsorbOdds.Default, GameConfig.Default.Absorb);

        const string dmId = "legacy-absorb";
        const string nonce = "legacy-absorb-nonce";
        var (winner, loser) = Pair();

        AbsorbOutcome? settled = null;
        var seed = FindSeed(s =>
        {
            var e = CommitReveal.DeriveEntropy(s, dmId, winner.Id, loser.Id, nonce);
            var on = Absorb.Resolve(winner.Genome, loser.Genome, e, AbsorbOdds.Default);
            if (!on.Minted) return false;
            settled = on;
            return true;
        });
        Assert.NotNull(seed);
        Assert.NotNull(settled);

        var commitment = CommitReveal.Commit(seed!);
        var entropy = CommitReveal.DeriveEntropy(seed!, dmId, winner.Id, loser.Id, nonce);
        var chal = winner.ToDto();
        var def = loser.ToDto();

        // No config argument at all — the pre-stamp callsite shape.
        var (ok, detail) = FairnessAudit.VerifyAbsorb(
            dmId, chal, def, challengerWon: true, nonce, commitment,
            settled!.Minted, settled.Result.ToHex(), Convert.ToHexString(seed!), Convert.ToHexString(entropy));
        Assert.True(ok, detail);

        // And explicitly passing Default is the same thing, so the resolver's "" → Default path agrees.
        Assert.True(FairnessAudit.VerifyAbsorb(
            dmId, chal, def, challengerWon: true, nonce, commitment,
            settled.Minted, settled.Result.ToHex(), Convert.ToHexString(seed!),
            Convert.ToHexString(entropy), GameConfig.Default).Ok);
    }

    [Fact]
    public async Task AnUnresolvableStamp_RefusesToVerify_RatherThanFallingBackToCurrentOdds()
    {
        // A stamp the server cannot serve must stop the absorb gate dead. Falling back to the running
        // server's odds is the original bug wearing a new hat: it would hand the winner's permakill a
        // green tick computed under rules nobody can show were the ones in force.
        var api = new ArkadeHeroesClient(_factory.CreateClient());
        const string unknown = "00000000000000000000000000000000000000000000000000000000absc0de5";

        var (config, error) = await api.Config.ResolveAsync(unknown);
        Assert.Null(config);
        Assert.NotNull(error);
        Assert.Contains("cannot verify", error!, StringComparison.OrdinalIgnoreCase);

        // The production callsites branch on exactly that null and surface the error instead of a verdict
        // (GameClient.SettleDeathAsync / GameSession.SettleDeathMatchAsync).
    }

    [Fact]
    public async Task ARetunedServersSettleStamp_ResolvesToTheOddsItActuallySettledUnder()
    {
        // The wire half: the odds must be RECOVERABLE from the record. A retuned server's stamp resolves
        // (trustlessly — the SDK re-hashes what it is served) to the very odds its settle ran on.
        using var factory = _factory.WithWebHostBuilder(b => b.UseSetting("Game:AbsorbChance", "200"));
        var api = new ArkadeHeroesClient(factory.CreateClient());
        var expected = GameConfigVersion.Compute(new GameOptions { AbsorbChance = 200 }.ToGameConfig());

        var info = await api.Chain.InfoAsync();
        Assert.Equal(expected, info.Config!.Version);

        var (resolved, error) = await api.Config.ResolveAsync(expected);
        Assert.Null(error);
        Assert.Equal(new AbsorbOdds(200, 90), resolved!.Absorb);
        Assert.NotEqual(GameConfig.Default.Absorb, resolved.Absorb);
    }
}
