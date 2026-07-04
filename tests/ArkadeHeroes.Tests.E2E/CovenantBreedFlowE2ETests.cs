using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ArkadeHeroes.Chain.NArk;
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
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

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

    private async Task<HttpClient> RegisterAsync(string name, SelfCustodyWallet wallet)
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/players", new RegisterPlayerRequest(name, wallet.Address));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"register failed: {body}");
        var player = JsonSerializer.Deserialize<PlayerDto>(body, Web)!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", player.Token);
        return client;
    }

    private static async Task<T> PostOkAsync<T>(HttpClient client, string path, object? payload = null)
    {
        var response = payload is null ? await client.PostAsync(path, null) : await client.PostAsJsonAsync(path, payload);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{path} failed: {body}");
        return JsonSerializer.Deserialize<T>(body, Web)!;
    }

    [Fact]
    public async Task CovenantBreed_ParentsRetained_ChildMintedUnderSpecies()
    {
        var alice = await RegisterAsync("Breed-Alice", _alice);

        // Fund the treasury (fresh server DB), wait for indexer visibility.
        var boot = (await alice.GetFromJsonAsync<ChainInfoDto>("/api/chain/info", Web))!;
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

        // Starters: two parent heroes minted straight into Alice's wallet.
        var heroes = (await PostOkAsync<StarterResponse>(alice, "/api/heroes/starter")).Heroes.ToList();
        Assert.Equal(2, heroes.Count);
        await _alice.WaitForAssetAsync(heroes[0].AssetId!, TimeSpan.FromSeconds(30));
        await _alice.WaitForAssetAsync(heroes[1].AssetId!, TimeSpan.FromSeconds(30));

        // Fund Alice's wallet for the fee, and commit a covenant breed.
        await RegtestHelper.ArkSend(_alice.Address, 50_000);
        await _alice.WaitForBalanceAsync(50_000, TimeSpan.FromSeconds(60));
        var commit = await PostOkAsync<BreedCommitResponse>(alice, "/api/breeding/commit",
            new BreedCommitRequest(heroes[0].Id, heroes[1].Id, "covenant"));
        Assert.NotNull(commit.EscrowAddress);
        Assert.Null(commit.Invoice);

        // Reveal before depositing is refused.
        var early = await alice.PostAsJsonAsync($"/api/breeding/{commit.BreedingId}/reveal",
            new BreedRevealRequest("e2e-breed"));
        Assert.Equal(HttpStatusCode.BadRequest, early.StatusCode);

        // Deposit BOTH parents + the fee into the breed escrow.
        await _alice.SendAssetAsync(commit.EscrowAddress!, heroes[0].AssetId!, 1);
        await _alice.SendAssetAsync(commit.EscrowAddress!, heroes[1].AssetId!, 1);
        await _alice.SendAsync(commit.EscrowAddress!, commit.EscrowFeeSats);

        // Reveal: the server assembles the covenant mint once the escrow is funded.
        BreedRevealResponse? reveal = null;
        var revealDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
        while (reveal is null)
        {
            var response = await alice.PostAsJsonAsync($"/api/breeding/{commit.BreedingId}/reveal",
                new BreedRevealRequest("e2e-breed"));
            if (response.IsSuccessStatusCode)
            {
                reveal = JsonSerializer.Deserialize<BreedRevealResponse>(await response.Content.ReadAsStringAsync(), Web);
                break;
            }
            Assert.True(DateTime.UtcNow < revealDeadline,
                $"covenant breed never revealed: {await response.Content.ReadAsStringAsync()}");
            await Task.Delay(2000);
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
