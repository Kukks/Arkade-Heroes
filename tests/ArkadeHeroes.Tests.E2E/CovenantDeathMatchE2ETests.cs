using ArkadeHeroes.Chain.NArk;
using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ArkadeHeroes.Tests.E2E;

/// <summary>
/// Covenant death-match end-to-end against the real stack: two players each stake
/// their hero into their own per-party death-match escrow; the emulator-enforced
/// settle (oracle-signed winning branch, revealed seed) BURNS the loser's hero and
/// RETURNS the winner's hero to the winner's wallet — no sats pot, the heroes are
/// the stakes. The winner is client-verifiable (replay the deterministic fight).
/// </summary>
public class CovenantDeathMatchE2ETests : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private string _serverDbPath = null!;
    private SelfCustodyWallet _alice = null!;
    private SelfCustodyWallet _bob = null!;
    private readonly List<string> _dbPaths = [];

    public async Task InitializeAsync()
    {
        await RegtestHelper.WaitForArkdReadyAsync(TimeSpan.FromSeconds(30));
        _serverDbPath = Path.Combine(Path.GetTempPath(), $"ah-dm-e2e-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("Chain__Mode", "NArk");
        Environment.SetEnvironmentVariable("Chain__NArk__ArkUri", "http://localhost:7070");
        Environment.SetEnvironmentVariable("Chain__NArk__DbPath", _serverDbPath);
        _factory = new WebApplicationFactory<Program>();
        _alice = await NewWalletAsync();
        _bob = await NewWalletAsync();
    }

    public async Task DisposeAsync()
    {
        await _alice.DisposeAsync();
        await _bob.DisposeAsync();
        _factory.Dispose();
        foreach (var p in _dbPaths.Append(_serverDbPath))
            try { if (File.Exists(p)) File.Delete(p); } catch { /* windows lock */ }
    }

    private async Task<SelfCustodyWallet> NewWalletAsync()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"ah-dm-wallet-{Guid.NewGuid():N}.db");
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
        // Treasury spends serialize with a 70s contention backoff; a first item claim
        // (issuance + delivery) can exceed HttpClient's 100s default right after the
        // starter mints — give the test client generous headroom.
        http.Timeout = TimeSpan.FromMinutes(4);
        var client = new ArkadeHeroesClient(http);
        await client.Players.RegisterAsync(new RegisterPlayerRequest(name, wallet.Address));
        return client;
    }

    [Fact]
    public async Task CovenantDeathMatch_LoserHeroBurned_WinnerRetained()
    {
        var alice = await RegisterAsync("DM-Alice", _alice);
        var bob = await RegisterAsync("DM-Bob", _bob);

        // Fund the treasury (fresh server DB), wait for indexer visibility.
        var boot = await alice.Chain.InfoAsync();
        await RegtestHelper.ArkSend(boot.TreasuryAddress, 300_000);
        var probe = _alice.GetService<global::NArk.Core.Transport.IClientTransport>();
        var treasuryHex = global::NArk.Abstractions.ArkAddress.Parse(boot.TreasuryAddress).ScriptPubKey.ToHex();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (true)
        {
            var seen = 0L;
            await foreach (var v in probe.GetVtxoByScriptsAsSnapshot(new HashSet<string> { treasuryHex }))
                if (!v.IsSpent()) seen += (long)v.Amount;
            if (seen >= 300_000) break;
            Assert.True(DateTime.UtcNow < deadline, "treasury funding never appeared");
            await Task.Delay(1500);
        }

        // Each player claims a starter hero into their own wallet.
        var aliceHero = (await alice.Heroes.ClaimStartersAsync()).Heroes[0];
        var bobHero = (await bob.Heroes.ClaimStartersAsync()).Heroes[0];
        await _alice.WaitForAssetAsync(aliceHero.AssetId!, TimeSpan.FromSeconds(30));
        await _bob.WaitForAssetAsync(bobHero.AssetId!, TimeSpan.FromSeconds(30));

        // Alice opens the death-match; both players stake their hero into their escrow.
        var open = await alice.DeathMatch.OpenAsync(
            new DeathMatchOpenRequest(aliceHero.Id, bobHero.Id));
        await _alice.SendAssetAsync(open.EscrowAddress, aliceHero.AssetId!, 1);

        var accept = await bob.DeathMatch.AcceptAsync(open.DeathMatchId);
        await _bob.SendAssetAsync(accept.EscrowAddress, bobHero.AssetId!, 1);

        // Settle: once both heroes are staked, the covenant burns the loser + returns the winner.
        DeathMatchSettleResponse? settle = null;
        var revealDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
        while (settle is null)
        {
            try
            {
                settle = await alice.DeathMatch.SettleAsync(open.DeathMatchId, new DeathMatchSettleRequest("e2e-dm"));
            }
            catch (ArkadeHeroesApiException ex)
            {
                Assert.True(DateTime.UtcNow < revealDeadline, $"covenant death-match never settled: {ex.Message}");
                await Task.Delay(2000);
            }
        }

        // The winner is client-verifiable (replay the deterministic fight).
        var entropy = ArkadeHeroes.Core.Fairness.CommitReveal.DeriveEntropy(
            Convert.FromHexString(settle!.ServerSeedHex), open.DeathMatchId, aliceHero.Id, bobHero.Id, "e2e-dm");
        var replay = ArkadeHeroes.Core.Combat.BattleEngine.Fight(
            FairnessAudit.RebuildHero(settle.ChallengerSnapshot), FairnessAudit.RebuildHero(settle.DefenderSnapshot), entropy);
        Assert.Equal(settle.WinnerHeroId, replay.WinnerId);
        Assert.NotNull(settle.Receipt);
        Assert.Equal("deathmatch", settle.Receipt!.Type);

        // Hero id == asset id (BuildAndStoreHero). The winner's hero returns to the winner's wallet.
        var winnerWallet = settle.WinnerHeroId == aliceHero.Id ? _alice : _bob;
        var loserWallet = settle.LoserHeroId == aliceHero.Id ? _alice : _bob;
        await winnerWallet.WaitForAssetAsync(settle.WinnerHeroId, TimeSpan.FromSeconds(60));

        // The loser's hero is BURNED — gone from BOTH wallets (destroyed, not transferred).
        var mineDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(45);
        while (true)
        {
            var winnerHeld = (await winnerWallet.GetAssetsAsync()).Select(a => a.AssetId).ToHashSet();
            var loserHeld = (await loserWallet.GetAssetsAsync()).Select(a => a.AssetId).ToHashSet();
            if (!winnerHeld.Contains(settle.LoserHeroId) && !loserHeld.Contains(settle.LoserHeroId)) break;
            Assert.True(DateTime.UtcNow < mineDeadline, "loser's hero was not burned (still in a wallet)");
            await Task.Delay(1500);
        }
    }

    /// <summary>
    /// The GEARED death-match live: the defender's equipped gear (bought + delivered to his
    /// REAL wallet, then staked into the joint escrow alongside his hero) transfers to the
    /// WINNER on-chain — covenant-enforced, not server bookkeeping. The gear-less fact above
    /// is the zero-regression gate; this one proves the spoil.
    /// </summary>
    [Fact]
    public async Task CovenantDeathMatch_StakedGearTransfersToTheWinner()
    {
        var alice = await RegisterAsync("DMG-Alice", _alice);
        var bob = await RegisterAsync("DMG-Bob", _bob);

        // Fund the treasury (fresh server DB): starters + the item issuance/delivery.
        var boot = await alice.Chain.InfoAsync();
        await RegtestHelper.ArkSend(boot.TreasuryAddress, 400_000);
        var probe = _alice.GetService<global::NArk.Core.Transport.IClientTransport>();
        var treasuryHex = global::NArk.Abstractions.ArkAddress.Parse(boot.TreasuryAddress).ScriptPubKey.ToHex();
        var fundDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (true)
        {
            var seen = 0L;
            await foreach (var v in probe.GetVtxoByScriptsAsSnapshot(new HashSet<string> { treasuryHex }))
                if (!v.IsSpent()) seen += (long)v.Amount;
            if (seen >= 400_000) break;
            Assert.True(DateTime.UtcNow < fundDeadline, "treasury funding never appeared");
            await Task.Delay(1500);
        }

        var aliceHero = (await alice.Heroes.ClaimStartersAsync()).Heroes[0];
        var bobHero = (await bob.Heroes.ClaimStartersAsync()).Heroes[0];
        await _alice.WaitForAssetAsync(aliceHero.AssetId!, TimeSpan.FromSeconds(30));
        await _bob.WaitForAssetAsync(bobHero.AssetId!, TimeSpan.FromSeconds(30));

        // Bob buys + equips gear through the REAL purchase flow (invoice → wallet pays → claim).
        await RegtestHelper.ArkSend(_bob.Address, 50_000);
        await _bob.WaitForBalanceAsync(50_000, TimeSpan.FromSeconds(60));
        var invoice = (await bob.Items.BuyAsync("rusty-blade")).Invoice;
        await _bob.SendAsync(invoice.PayToAddress, invoice.AmountSats);
        ClaimItemResponse? claim = null;
        var claimDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(150);
        while (claim is null)
        {
            try
            {
                claim = await bob.Items.ClaimAsync(new ClaimItemRequest(invoice.InvoiceId));
            }
            catch (ArkadeHeroesApiException ex)
            {
                Assert.True(DateTime.UtcNow < claimDeadline, $"item claim never delivered: {ex.Message}");
                await Task.Delay(2000);
            }
        }
        await _bob.WaitForAssetAsync(claim!.ItemAssetId, TimeSpan.FromSeconds(30));
        await bob.Heroes.EquipAsync(bobHero.Id, new EquipRequest("rusty-blade"));

        // Open: Bob's loadout-at-open is baked as his required stake.
        var open = await alice.DeathMatch.OpenAsync(
            new DeathMatchOpenRequest(aliceHero.Id, bobHero.Id));
        var stake = Assert.Single(open.DefenderGear);
        Assert.Equal("rusty-blade", stake.ItemId);
        Assert.Equal(claim.ItemAssetId, stake.AssetId);
        Assert.Empty(open.ChallengerGear);

        // Stakes: Alice her hero; Bob his hero + the gear unit.
        await _alice.SendAssetAsync(open.EscrowAddress, aliceHero.AssetId!, 1);
        var accept = await bob.DeathMatch.AcceptAsync(open.DeathMatchId);
        await _bob.SendAssetAsync(accept.EscrowAddress, bobHero.AssetId!, 1);
        await _bob.SendAssetAsync(accept.EscrowAddress, stake.AssetId, (ulong)stake.Amount);

        DeathMatchSettleResponse? settle = null;
        var revealDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
        while (settle is null)
        {
            try
            {
                settle = await alice.DeathMatch.SettleAsync(open.DeathMatchId, new DeathMatchSettleRequest("e2e-dm-gear"));
            }
            catch (ArkadeHeroesApiException ex)
            {
                Assert.True(DateTime.UtcNow < revealDeadline, $"geared covenant death-match never settled: {ex.Message}");
                await Task.Delay(2000);
            }
        }

        // The WINNER's wallet holds the winner hero AND the staked gear unit — routed by
        // the covenant, whoever won; the loser holds neither (their hero is burned).
        var winnerWallet = settle!.WinnerHeroId == aliceHero.Id ? _alice : _bob;
        var loserWallet = settle.LoserHeroId == aliceHero.Id ? _alice : _bob;
        await winnerWallet.WaitForAssetAsync(settle.WinnerHeroId, TimeSpan.FromSeconds(60));
        await winnerWallet.WaitForAssetAsync(stake.AssetId, TimeSpan.FromSeconds(60));
        var gearDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(45);
        while (true)
        {
            var loserHeld = (await loserWallet.GetAssetsAsync()).Select(a => a.AssetId).ToHashSet();
            if (!loserHeld.Contains(settle.LoserHeroId) && !loserHeld.Contains(stake.AssetId)) break;
            Assert.True(DateTime.UtcNow < gearDeadline, "the loser still holds the burned hero or the forfeited gear");
            await Task.Delay(1500);
        }
    }
}
