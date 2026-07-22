using ArkadeHeroes.Chain.NArk;
using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Core.Heroes;
using ArkadeHeroes.Server;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ArkadeHeroes.Tests.E2E;

/// <summary>
/// The ABSORB death-match end-to-end against the real stack: an opt-in match where a seed-driven
/// roll RE-MINTS the winner absorbing the loser's better traits. Both staked heroes are BURNED and
/// a NEW absorbed hero is minted under the species to the winner's wallet (the fee-less merge shape),
/// emulator-enforced. This is the first LIVE exercise of NArk's SettleDeathMatchAbsorbMintAsync. The
/// classic (keep) death-match is covered by <see cref="CovenantDeathMatchE2ETests"/>; the keep branch
/// of an absorb escrow is byte-identical to it. Absorb odds are forced to (nearly) always fire; the
/// blank-trait starters get a trait injected via the in-process GameStore (the winner keeps its own
/// stats and absorbs the loser's rarer gene).
/// </summary>
public class CovenantDeathMatchAbsorbE2ETests : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private string _serverDbPath = null!;
    private SelfCustodyWallet _alice = null!;
    private readonly List<string> _dbPaths = [];

    public async Task InitializeAsync()
    {
        await RegtestHelper.WaitForArkdReadyAsync(TimeSpan.FromSeconds(30));
        _serverDbPath = Path.Combine(Path.GetTempPath(), $"ah-dma-e2e-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("Chain__Mode", "NArk");
        Environment.SetEnvironmentVariable("Chain__NArk__ArkUri", "http://localhost:7070");
        Environment.SetEnvironmentVariable("Chain__NArk__DbPath", _serverDbPath);
        Environment.SetEnvironmentVariable("Game__AbsorbChance", "255"); // (nearly) always absorb given a candidate
        _factory = new WebApplicationFactory<Program>();
        _alice = await NewWalletAsync();
    }

    public async Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("Game__AbsorbChance", null);
        await _alice.DisposeAsync();
        _factory.Dispose();
        foreach (var p in _dbPaths.Append(_serverDbPath))
            try { if (File.Exists(p)) File.Delete(p); } catch { /* windows lock */ }
    }

    private async Task<SelfCustodyWallet> NewWalletAsync()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"ah-dma-wallet-{Guid.NewGuid():N}.db");
        _dbPaths.Add(dbPath);
        return await SelfCustodyWallet.CreateAsync(new SelfCustodyWalletOptions
        {
            ArkUri = "http://localhost:7070",
            DbPath = dbPath,
        });
    }

    private async Task<ArkadeHeroesClient> RegisterAsync(string name, SelfCustodyWallet wallet)
    {
        var http = _factory.CreateClient();
        http.Timeout = TimeSpan.FromMinutes(4);
        var client = new ArkadeHeroesClient(http);
        await client.Players.RegisterAsync(new RegisterPlayerRequest(name, wallet.Address));
        return client;
    }

    /// <summary>Clones a hero with one dominant trait gene set — starters are blank on traits, so this gives
    /// the loser a rarer gene the winner can absorb (Genome is init-only, so we replace the record).</summary>
    private static Hero WithTrait(Hero h, TraitCategory cat, byte value)
    {
        var bytes = h.Genome.Bytes.ToArray();
        bytes[16 + (int)cat * 2] = value;
        return new Hero
        {
            Id = h.Id, OwnerId = h.OwnerId, Name = h.Name, Genome = new Genome(bytes),
            Generation = h.Generation, ParentAId = h.ParentAId, ParentBId = h.ParentBId,
            Level = h.Level, Xp = h.Xp, BreedCount = h.BreedCount,
            EntropyHex = h.EntropyHex, ServerSeedHex = h.ServerSeedHex, PlayerNonce = h.PlayerNonce,
            AssetId = h.AssetId, MintArkTxId = h.MintArkTxId,
        };
    }

    private static async Task<DeathMatchSettleResponse> SettleWithRetryAsync(ArkadeHeroesClient client, string id, string nonce)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(120);
        while (true)
        {
            try { return await client.DeathMatch.SettleAsync(id, new DeathMatchSettleRequest(nonce)); }
            catch (ArkadeHeroesApiException ex)
            {
                Assert.True(DateTime.UtcNow < deadline, $"absorb death-match never settled: {ex.Message}");
                await Task.Delay(2000);
            }
        }
    }

    private async Task WaitForTreasuryAsync(string treasuryAddress, long sats)
    {
        var probe = _alice.GetService<global::NArk.Core.Transport.IClientTransport>();
        var treasuryHex = global::NArk.Abstractions.ArkAddress.Parse(treasuryAddress).ScriptPubKey.ToHex();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (true)
        {
            var seen = 0L;
            await foreach (var v in probe.GetVtxoByScriptsAsSnapshot(new HashSet<string> { treasuryHex }))
                if (!v.IsSpent()) seen += (long)v.Amount;
            if (seen >= sats) return;
            Assert.True(DateTime.UtcNow < deadline, $"treasury funding never appeared (saw {seen})");
            await Task.Delay(1500);
        }
    }

    [Fact]
    public async Task AbsorbDeathMatch_MintsAbsorbedHeroToWinner_BothBurned_Live()
    {
        var alice = await RegisterAsync("DMA-Alice", _alice);
        var boot = await alice.Chain.InfoAsync();
        await RegtestHelper.ArkSend(boot.TreasuryAddress, 400_000); // the winner + a fresh loser or two + the absorbed mint
        await WaitForTreasuryAsync(boot.TreasuryAddress, 400_000);

        var aliceHero = (await alice.Heroes.ClaimStartersAsync()).Heroes[0];
        await _alice.WaitForAssetAsync(aliceHero.AssetId!, TimeSpan.FromSeconds(30));

        // Alice pays an absorb death-match fee (3x MatchFee, and her hero is level 20) on EVERY
        // attempt below, and the settle refuses until it clears — so fund her for all of them.
        await RegtestHelper.ArkSend(_alice.Address, 300_000);
        await _alice.WaitForBalanceAsync(300_000, TimeSpan.FromSeconds(60));
        var store = _factory.Services.GetRequiredService<GameStore>();
        store.Heroes[aliceHero.Id].Level = 20; // Alice reliably wins the deterministic fight

        // A ~1/256 keep roll leaves Alice's hero intact, so retry with a fresh loser until it mints.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var bobWallet = await NewWalletAsync();
            var bob = await RegisterAsync($"DMA-Bob{attempt}", bobWallet);
            // The defender owes his own per-character fee too — a fresh wallet has nothing.
            await RegtestHelper.ArkSend(bobWallet.Address, 50_000);
            await bobWallet.WaitForBalanceAsync(50_000, TimeSpan.FromSeconds(60));
            var bobHero = (await bob.Heroes.ClaimStartersAsync()).Heroes[0];
            await bobWallet.WaitForAssetAsync(bobHero.AssetId!, TimeSpan.FromSeconds(30));
            store.Heroes[bobHero.Id] = WithTrait(store.Heroes[bobHero.Id], TraitCategory.Aura, 255); // Bob has a Legendary Aura to absorb

            // Both stake their hero into the 6-leaf absorb escrow.
            await _alice.WaitForAssetAsync(aliceHero.AssetId!, TimeSpan.FromSeconds(60)); // (returned to Alice after a prior keep)
            var open = await alice.DeathMatch.OpenAsync(new DeathMatchOpenRequest(aliceHero.Id, bobHero.Id, Absorb: true));
            await _alice.SendAssetAsync(open.EscrowAddress, aliceHero.AssetId!, 1);
            if (open.FeeInvoice is { } challengerFee)
                await _alice.SendAsync(challengerFee.PayToAddress, challengerFee.AmountSats);
            var accept = await bob.DeathMatch.AcceptAsync(open.DeathMatchId);
            await bobWallet.SendAssetAsync(accept.EscrowAddress, bobHero.AssetId!, 1);
            if (accept.FeeInvoice is { } defenderFee)
                await bobWallet.SendAsync(defenderFee.PayToAddress, defenderFee.AmountSats);

            var settle = await SettleWithRetryAsync(alice, open.DeathMatchId, "e2e-absorb");
            if (!settle.Minted) continue; // rare keep roll — Alice's hero returns; try a fresh loser

            // ── Minting settle: the absorbed hero is minted to Alice; BOTH old heroes are burned.
            Assert.Equal(aliceHero.Id, settle.WinnerHeroId);
            Assert.NotNull(settle.NewHero);
            Assert.True(settle.TraitsAbsorbed >= 1);

            // The NEW absorbed hero lands in the winner's wallet, live.
            await _alice.WaitForAssetAsync(settle.NewHero!.Id, TimeSpan.FromSeconds(60));

            // BOTH input heroes are BURNED — gone from EVERY wallet (destroyed, not transferred).
            var burnDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(45);
            while (true)
            {
                var aliceHeld = (await _alice.GetAssetsAsync()).Select(a => a.AssetId).ToHashSet();
                var bobHeld = (await bobWallet.GetAssetsAsync()).Select(a => a.AssetId).ToHashSet();
                var gone = !aliceHeld.Contains(aliceHero.AssetId!) && !bobHeld.Contains(aliceHero.AssetId!)
                        && !aliceHeld.Contains(bobHero.AssetId!) && !bobHeld.Contains(bobHero.AssetId!);
                if (gone) break;
                Assert.True(DateTime.UtcNow < burnDeadline, "an input hero was not burned (still in a wallet)");
                await Task.Delay(1500);
            }

            // Client-verifiable: recompute Absorb.Resolve from the revealed seed + published odds.
            var verify = FairnessAudit.VerifyAbsorb(open.DeathMatchId, settle.ChallengerSnapshot, settle.DefenderSnapshot,
                challengerWon: settle.WinnerHeroId == aliceHero.Id, "e2e-absorb", open.CommitmentHex,
                new AbsorbOdds(255, 90), settle.Minted, settle.NewGenomeHex, settle.ServerSeedHex, settle.EntropyHex);
            Assert.True(verify.Ok, verify.Detail);
            Assert.Equal("absorb", settle.Receipt!.Type);
            return;
        }
        Assert.Fail("no absorb mint within 4 attempts at AbsorbChance=255");
    }
}
