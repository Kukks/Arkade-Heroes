using ArkadeHeroes.Chain.NArk;
using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ArkadeHeroes.Tests.E2E;

/// <summary>
/// Covenant merge (fusion) end-to-end against the real stack: the player deposits
/// BOTH input heroes (base + sacrifice) plus the fee into a merge escrow, and the
/// server assembles the emulator-enforced mint — the two inputs BURNED (declared
/// in the asset packet with no output, so arkd destroys them — a true sink), the
/// fused hero issued under the species with an oracle-attested genome, the fee to
/// the treasury. The covenant makes any other shape unsignable; the fused genome
/// is client-recomputable (Fusion.Fuse).
/// </summary>
public class CovenantMergeFlowE2ETests : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private string _serverDbPath = null!;
    private SelfCustodyWallet _alice = null!;
    private readonly List<string> _dbPaths = [];

    public async Task InitializeAsync()
    {
        await RegtestHelper.WaitForArkdReadyAsync(TimeSpan.FromSeconds(30));
        _serverDbPath = Path.Combine(Path.GetTempPath(), $"ah-merge-e2e-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("Chain__Mode", "NArk");
        Environment.SetEnvironmentVariable("Chain__NArk__ArkUri", "http://localhost:7070");
        Environment.SetEnvironmentVariable("Chain__NArk__DbPath", _serverDbPath);
        _factory = new WebApplicationFactory<Program>();
        _alice = await NewWalletAsync();
    }

    public async Task DisposeAsync()
    {
        await _alice.DisposeAsync();
        _factory.Dispose();
        foreach (var p in _dbPaths.Append(_serverDbPath))
            try { if (File.Exists(p)) File.Delete(p); } catch { /* windows lock */ }
    }

    private async Task<SelfCustodyWallet> NewWalletAsync()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"ah-merge-wallet-{Guid.NewGuid():N}.db");
        _dbPaths.Add(dbPath);
        return await SelfCustodyWallet.CreateAsync(new SelfCustodyWalletOptions
        {
            ArkUri = "http://localhost:7070",
            DbPath = dbPath,
        });
    }

    private async Task<ArkadeHeroesClient> RegisterAsync(string name, SelfCustodyWallet wallet)
    {
        var client = new ArkadeHeroesClient(_factory.CreateClient());
        await client.Players.RegisterAsync(new RegisterPlayerRequest(name, wallet.Address));
        return client;
    }

    [Fact]
    public async Task CovenantMerge_InputsRetired_FusedMintedUnderSpecies()
    {
        var alice = await RegisterAsync("Merge-Alice", _alice);

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

        // Fund Alice's wallet: she buys her own heroes, then pays the merge fee out of the same sats.
        await RegtestHelper.ArkSend(_alice.Address, 50_000);
        await _alice.WaitForBalanceAsync(50_000, TimeSpan.FromSeconds(60));

        // Starters: a recruit mints ONE hero, so the base + the sacrifice is two purchases — quote, pay,
        // claim, twice — minted straight into Alice's wallet.
        var heroes = await alice.RecruitAsync(_alice, 2);
        Assert.Equal(2, heroes.Count);
        var baseHero = heroes[0];
        var sacrificeHero = heroes[1];
        await _alice.WaitForAssetAsync(baseHero.AssetId!, TimeSpan.FromSeconds(30));
        await _alice.WaitForAssetAsync(sacrificeHero.AssetId!, TimeSpan.FromSeconds(30));

        // Commit a covenant merge.
        var commit = await alice.Merge.CommitAsync(
            new MergeCommitRequest(baseHero.Id, sacrificeHero.Id, "covenant"));
        Assert.False(string.IsNullOrEmpty(commit.EscrowAddress));

        // Reveal before depositing is refused.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => alice.Merge.RevealAsync(
            commit.MergeId, new MergeRevealRequest("e2e-merge")));

        // Deposit BOTH inputs + the fee into the merge escrow (same shape as breeding).
        await _alice.SendAssetAsync(commit.EscrowAddress, baseHero.AssetId!, 1);
        await _alice.SendAssetAsync(commit.EscrowAddress, sacrificeHero.AssetId!, 1);
        await _alice.SendAsync(commit.EscrowAddress, commit.FeeSats);

        // Reveal: the server assembles the covenant mint once the escrow is funded.
        MergeRevealResponse? reveal = null;
        var revealDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
        while (reveal is null)
        {
            try
            {
                reveal = await alice.Merge.RevealAsync(commit.MergeId, new MergeRevealRequest("e2e-merge"));
            }
            catch (ArkadeHeroesApiException ex)
            {
                Assert.True(DateTime.UtcNow < revealDeadline, $"covenant merge never revealed: {ex.Message}");
                await Task.Delay(2000);
            }
        }

        // The fused hero is auditable (Fusion.Fuse recompute), carries a signed receipt,
        // and inherits the base's level (genesis-attested).
        var (ok, detail) = FairnessAudit.VerifyMerge(
            commit.MergeId, baseHero, sacrificeHero, "e2e-merge", commit.CommitmentHex, reveal!);
        Assert.True(ok, detail);
        Assert.NotNull(reveal!.Receipt);
        Assert.Equal("merge", reveal.Receipt!.Type);
        Assert.Equal(baseHero.Level, reveal.Hero.Level);

        // The fused hero asset lands in Alice's own wallet.
        await _alice.WaitForAssetAsync(reveal.Hero.AssetId!, TimeSpan.FromSeconds(60));

        // The sink: both inputs are GONE from Alice's wallet — BURNED (destroyed on-chain),
        // never returned to her (she gave up two heroes to mint one concentrated hero).
        var mineDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            var held = (await _alice.GetAssetsAsync()).Select(a => a.AssetId).ToHashSet();
            if (!held.Contains(baseHero.AssetId!) && !held.Contains(sacrificeHero.AssetId!)) break;
            Assert.True(DateTime.UtcNow < mineDeadline, "retired inputs still in Alice's wallet");
            await Task.Delay(1500);
        }
    }
}
