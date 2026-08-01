using ArkadeHeroes.Chain.NArk;
using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ArkadeHeroes.Tests.E2E;

/// <summary>
/// Covenant breeding end-to-end against the real stack: the player deposits
/// BOTH parent heroes plus the fee into a breed escrow, and the server
/// assembles the emulator-enforced mint — parents retained to the player, the
/// child issued under the species with an oracle-attested genome, the fee to
/// the treasury. The covenant makes any other shape unsignable.
/// </summary>
public class CovenantBreedFlowE2ETests : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private string _serverDbPath = null!;
    private SelfCustodyWallet _alice = null!;
    private readonly List<string> _dbPaths = [];

    public async Task InitializeAsync()
    {
        await RegtestHelper.WaitForArkdReadyAsync(TimeSpan.FromSeconds(30));
        _serverDbPath = Path.Combine(Path.GetTempPath(), $"ah-breed-e2e-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("Chain__Mode", "NArk");
        Environment.SetEnvironmentVariable("Chain__NArk__ArkUri", "http://localhost:7070");
        Environment.SetEnvironmentVariable("Chain__NArk__DbPath", _serverDbPath);
        // A throwaway database per run, so this genuinely IS a first install and a generated treasury
        // is what we want. The server will not generate one unless told — that refusal is what stops a
        // deployment that merely LOST its database from minting itself a new treasury.
        Environment.SetEnvironmentVariable("Chain__NArk__AllowTreasuryAutoCreate", "true");
        Environment.SetEnvironmentVariable("Game__BreedingCooldownBaseUnit", "00:00:02");
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
        var dbPath = Path.Combine(Path.GetTempPath(), $"ah-breed-wallet-{Guid.NewGuid():N}.db");
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
    public async Task CovenantBreed_ParentsRetained_ChildMintedUnderSpecies()
    {
        var alice = await RegisterAsync("Breed-Alice", _alice);

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

        // Fund Alice's wallet: she buys her own heroes, then pays the breed fee out of the same sats.
        await RegtestHelper.ArkSend(_alice.Address, 50_000);
        await _alice.WaitForBalanceAsync(50_000, TimeSpan.FromSeconds(60));

        // Starters: a recruit mints ONE hero, so a breedable pair is two purchases — quote, pay, claim,
        // twice — minted straight into Alice's wallet.
        var heroes = await alice.RecruitAsync(_alice, 2);
        Assert.Equal(2, heroes.Count);
        await _alice.WaitForAssetAsync(heroes[0].AssetId!, TimeSpan.FromSeconds(30));
        await _alice.WaitForAssetAsync(heroes[1].AssetId!, TimeSpan.FromSeconds(30));

        // Commit a covenant breed.
        var commit = await alice.Breeding.CommitAsync(
            new BreedCommitRequest(heroes[0].Id, heroes[1].Id, "covenant"));
        Assert.NotNull(commit.EscrowAddress);
        Assert.Null(commit.Invoice);

        // Reveal before depositing is refused.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => alice.Breeding.RevealAsync(
            commit.BreedingId, new BreedRevealRequest("e2e-breed")));

        // Deposit BOTH parents + the fee into the breed escrow.
        await _alice.SendAssetAsync(commit.EscrowAddress!, heroes[0].AssetId!, 1);
        await _alice.SendAssetAsync(commit.EscrowAddress!, heroes[1].AssetId!, 1);
        await _alice.SendAsync(commit.EscrowAddress!, commit.EscrowFeeSats);

        // Reveal: the server assembles the covenant mint once the escrow is funded.
        BreedRevealResponse? reveal = null;
        var revealDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
        while (reveal is null)
        {
            try
            {
                reveal = await alice.Breeding.RevealAsync(commit.BreedingId, new BreedRevealRequest("e2e-breed"));
            }
            catch (ArkadeHeroesApiException ex)
            {
                Assert.True(DateTime.UtcNow < revealDeadline, $"covenant breed never revealed: {ex.Message}");
                await Task.Delay(2000);
            }
        }

        // The child hero is auditable and carries a signed receipt.
        var (ok, detail) = FairnessAudit.VerifyBreeding(heroes[0], heroes[1], "e2e-breed", commit.CommitmentHex, reveal!);
        Assert.True(ok, detail);
        Assert.NotNull(reveal!.Receipt);

        // The child asset AND both retained parents land in Alice's wallet.
        await _alice.WaitForAssetAsync(reveal.Hero.AssetId!, TimeSpan.FromSeconds(60));
        await _alice.WaitForAssetAsync(heroes[0].AssetId!, TimeSpan.FromSeconds(30));
        await _alice.WaitForAssetAsync(heroes[1].AssetId!, TimeSpan.FromSeconds(30));
    }
}
