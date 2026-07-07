using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ArkadeHeroes.Chain.Covenants;
using ArkadeHeroes.Chain.NArk;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ArkadeHeroes.Tests.E2E;

/// <summary>
/// The PLAYER-facing refund path against the real stack: a covenant match is
/// opened through the game server, the challenger stakes from their own
/// wallet, the defender vanishes — and the challenger reclaims via
/// <see cref="EscrowRefundFlow"/> exactly as the console client's `refund`
/// command does: escrow params fetched from the server, contracts REBUILT
/// locally, chain time gated via esplora MTP, one canonical submission.
/// </summary>
public class ClientRefundFlowTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly Uri EmulatorUri = new("http://localhost:7073/");
    private const string EsploraApi = "http://localhost:8999/api/v1";
    private const long Wager = 4_000;

    private WebApplicationFactory<Program> _factory = null!;
    private string _serverDbPath = null!;
    private SelfCustodyWallet _alice = null!;
    private SelfCustodyWallet _bob = null!;
    private readonly List<string> _dbPaths = [];

    public async Task InitializeAsync()
    {
        await RegtestHelper.WaitForArkdReadyAsync(TimeSpan.FromSeconds(30));

        _serverDbPath = Path.Combine(Path.GetTempPath(), $"ah-refund-e2e-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("Chain__Mode", "NArk");
        Environment.SetEnvironmentVariable("Chain__NArk__ArkUri", "http://localhost:7070");
        Environment.SetEnvironmentVariable("Chain__NArk__DbPath", _serverDbPath);
        // Short refund window so the abandoned match becomes reclaimable in-test.
        Environment.SetEnvironmentVariable("Game__WagerEscrowRefundAfter", "00:00:08");
        _factory = new WebApplicationFactory<Program>();

        _alice = await NewWalletAsync();
        _bob = await NewWalletAsync();
        await RegtestHelper.ArkSend(_alice.Address, 50_000);
        await _alice.WaitForBalanceAsync(50_000, TimeSpan.FromSeconds(60));
    }

    public async Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("Game__WagerEscrowRefundAfter", null);
        await _alice.DisposeAsync();
        await _bob.DisposeAsync();
        _factory.Dispose();
        foreach (var path in _dbPaths.Append(_serverDbPath))
            try { if (File.Exists(path)) File.Delete(path); } catch { /* windows lock */ }
    }

    private async Task<SelfCustodyWallet> NewWalletAsync()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"ah-refund-wallet-{Guid.NewGuid():N}.db");
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
        var response = payload is null
            ? await client.PostAsync(path, null)
            : await client.PostAsJsonAsync(path, payload);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{path} failed: {body}");
        return JsonSerializer.Deserialize<T>(body, Web)!;
    }

    [Fact]
    public async Task AbandonedCovenantMatch_ChallengerReclaimsViaTheClientFlow()
    {
        var alice = await RegisterAsync("Refund-Alice", _alice);
        var bob = await RegisterAsync("Refund-Bob", _bob);

        // Fresh server DB → fresh treasury; fund it and wait until the funding
        // is indexer-visible BEFORE the first mint (the claim flag is not
        // rolled back on a failed mint, so a premature attempt would strand
        // the player hero-less).
        var bootInfo = (await alice.GetFromJsonAsync<ChainInfoDto>("/api/chain/info", Web))!;
        await RegtestHelper.ArkSend(bootInfo.TreasuryAddress, 200_000);
        var treasuryScript = global::NArk.Abstractions.ArkAddress.Parse(bootInfo.TreasuryAddress).ScriptPubKey.ToHex();
        var probeTransport = _alice.GetService<global::NArk.Core.Transport.IClientTransport>();
        var fundingDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (true)
        {
            var seen = 0L;
            await foreach (var v in probeTransport.GetVtxoByScriptsAsSnapshot(new HashSet<string> { treasuryScript }))
                if (!v.IsSpent()) seen += (long)v.Amount;
            if (seen >= 200_000) break;
            Assert.True(DateTime.UtcNow < fundingDeadline, $"treasury funding never appeared (saw {seen} sats)");
            await Task.Delay(1500);
        }

        var aliceHeroes = await PostOkAsync<StarterResponse>(alice, "/api/heroes/starter");
        var bobHeroes = await PostOkAsync<StarterResponse>(bob, "/api/heroes/starter");

        // Covenant match; alice stakes into HER escrow; bob never accepts.
        var open = await PostOkAsync<OpenMatchResponse>(alice, "/api/matches/open",
            new OpenMatchRequest(aliceHeroes.Heroes[0].Id, bobHeroes.Heroes[0].Id, Wager, "covenant"));
        Assert.NotNull(open.EscrowAddress);
        await _alice.SendAsync(open.EscrowAddress!, Wager);

        // The client-side trustless rebuild: params from the server, contracts
        // reconstructed locally — the challenger address MUST equal what alice
        // just staked to, or the server lied about the covenant.
        var parameters = (await alice.GetFromJsonAsync<WagerEscrowParams>(
            $"/api/matches/{open.MatchId}/escrow", Web))!;
        Assert.Equal(_alice.Address, parameters.ChallengerAddress);
        var transport = _alice.GetService<global::NArk.Core.Transport.IClientTransport>();
        var serverInfo = await transport.GetServerInfoAsync();
        var emulatorInfo = await new EmulatorClient(EmulatorUri).GetInfoAsync();
        var (challengerContract, _) = WagerEscrowContracts.Build(
            parameters, serverInfo.SignerKey, emulatorInfo.SignerPubkey);
        Assert.Equal(open.EscrowAddress,
            challengerContract.GetArkAddress().ToString(serverInfo.Network == NBitcoin.Network.Main));

        // Chain info advertises the endpoints the console client would use.
        var chainInfo = (await alice.GetFromJsonAsync<ChainInfoDto>("/api/chain/info", Web))!;
        Assert.NotNull(chainInfo.EmulatorUri);
        Assert.NotNull(chainInfo.EsploraApiUri);

        using var esploraHttp = new HttpClient();

        // Pre-expiry the flow's own gate refuses WITHOUT submitting anything —
        // the canonical refund txid must never see a refused submission.
        var balanceBefore = await _alice.GetBalanceSatsAsync();
        await Assert.ThrowsAsync<RefundNotYetDueException>(() => EscrowRefundFlow.RefundAsync(
            _alice, EmulatorUri, parameters,
            ct => EsploraChainTime.GetMedianTimeAsync(esploraHttp, EsploraApi, ct)));

        // Let the CHAIN's clock pass expiry, then reclaim through the flow.
        await RegtestHelper.WaitForChainTimeAsync(parameters.RefundAfterUnixSeconds, TimeSpan.FromSeconds(120));
        var refund = await EscrowRefundFlow.RefundAsync(
            _alice, EmulatorUri, parameters,
            ct => EsploraChainTime.GetMedianTimeAsync(esploraHttp, EsploraApi, ct));
        Assert.False(string.IsNullOrEmpty(refund.SignedArkTx));

        await _alice.WaitForBalanceAsync(balanceBefore + Wager, TimeSpan.FromSeconds(90));
    }

    [Fact]
    public async Task AbandonedMergeDeposit_PlayerReclaimsBothHeroesViaTheClientFlow()
    {
        var alice = await RegisterAsync("Merge-Reclaim-Alice", _alice);
        await EnsureTreasuryFundedAsync(alice);
        var heroes = await PostOkAsync<StarterResponse>(alice, "/api/heroes/starter");
        var baseHero = heroes.Heroes[0];
        var sacHero = heroes.Heroes[1];

        // Covenant merge; Alice deposits base + sacrifice + fee, then abandons it.
        var commit = await PostOkAsync<MergeCommitResponse>(alice, "/api/merge/commit",
            new MergeCommitRequest(baseHero.Id, sacHero.Id, "covenant"));
        await _alice.SendAssetAsync(commit.EscrowAddress, baseHero.AssetId!, 1);
        await _alice.SendAssetAsync(commit.EscrowAddress, sacHero.AssetId!, 1);
        await _alice.SendAsync(commit.EscrowAddress, commit.FeeSats);

        // The trustless rebuild: params from the server, contract reconstructed locally.
        var parameters = (await alice.GetFromJsonAsync<MergeEscrowParams>(
            $"/api/merges/{commit.MergeId}/escrow", Web))!;
        Assert.Equal(_alice.Address, parameters.PlayerAddress);
        using var esploraHttp = new HttpClient();

        // Pre-expiry the flow's own gate refuses WITHOUT submitting anything.
        await Assert.ThrowsAsync<RefundNotYetDueException>(() => MergeEscrowRefundFlow.ReclaimAsync(
            _alice, EmulatorUri, parameters,
            ct => EsploraChainTime.GetMedianTimeAsync(esploraHttp, EsploraApi, ct)));

        await RegtestHelper.WaitForChainTimeAsync(parameters.RefundAfterUnixSeconds, TimeSpan.FromSeconds(120));
        var refund = await MergeEscrowRefundFlow.ReclaimAsync(
            _alice, EmulatorUri, parameters,
            ct => EsploraChainTime.GetMedianTimeAsync(esploraHttp, EsploraApi, ct));
        Assert.False(string.IsNullOrEmpty(refund.SignedArkTx));

        // Both heroes land back in Alice's wallet.
        await _alice.WaitForAssetAsync(baseHero.AssetId!, TimeSpan.FromSeconds(90));
        await _alice.WaitForAssetAsync(sacHero.AssetId!, TimeSpan.FromSeconds(90));
    }

    [Fact]
    public async Task HalfFundedDeathMatch_ChallengerAbortsViaTheClientFlow()
    {
        var alice = await RegisterAsync("DM-Abort-Alice", _alice);
        var bob = await RegisterAsync("DM-Abort-Bob", _bob);
        await EnsureTreasuryFundedAsync(alice);
        var aliceHeroes = await PostOkAsync<StarterResponse>(alice, "/api/heroes/starter");
        var bobHeroes = await PostOkAsync<StarterResponse>(bob, "/api/heroes/starter");
        var myHero = aliceHeroes.Heroes[0];

        // Alice opens + stakes her hero; Bob never accepts → half-funded, Alice is stranded.
        var open = await PostOkAsync<DeathMatchOpenResponse>(alice, "/api/deathmatch/open",
            new DeathMatchOpenRequest(myHero.Id, bobHeroes.Heroes[0].Id));
        await _alice.SendAssetAsync(open.EscrowAddress, myHero.AssetId!, 1);

        var parameters = (await alice.GetFromJsonAsync<DeathMatchJointEscrowParams>(
            $"/api/deathmatch/{open.DeathMatchId}/escrow", Web))!;
        Assert.Equal(_alice.Address, parameters.ChallengerAddress);
        using var esploraHttp = new HttpClient();

        // The oracle-gated abort: the flow requests Alice's side signature, then spends her
        // abort leaf — immediate (no expiry wait; the opponent never showed).
        var reclaim = await DeathMatchRefundFlow.ReclaimAsync(
            _alice, EmulatorUri, parameters,
            ct => EsploraChainTime.GetMedianTimeAsync(esploraHttp, EsploraApi, ct),
            async ct =>
            {
                var resp = await PostOkAsync<AbortDeathMatchResponse>(alice, $"/api/deathmatch/{open.DeathMatchId}/abort");
                return Convert.FromHexString(resp.SideSignatureHex);
            });
        Assert.False(string.IsNullOrEmpty(reclaim.SignedArkTx));

        // Alice's hero is back in her wallet.
        await _alice.WaitForAssetAsync(myHero.AssetId!, TimeSpan.FromSeconds(90));
    }

    /// <summary>Funds the fresh server's treasury and waits until the funding is indexer-visible — required before the first starter mint (as the wager fact does).</summary>
    private async Task EnsureTreasuryFundedAsync(HttpClient client)
    {
        var bootInfo = (await client.GetFromJsonAsync<ChainInfoDto>("/api/chain/info", Web))!;
        await RegtestHelper.ArkSend(bootInfo.TreasuryAddress, 200_000);
        var treasuryScript = global::NArk.Abstractions.ArkAddress.Parse(bootInfo.TreasuryAddress).ScriptPubKey.ToHex();
        var probeTransport = _alice.GetService<global::NArk.Core.Transport.IClientTransport>();
        var fundingDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (true)
        {
            var seen = 0L;
            await foreach (var v in probeTransport.GetVtxoByScriptsAsSnapshot(new HashSet<string> { treasuryScript }))
                if (!v.IsSpent()) seen += (long)v.Amount;
            if (seen >= 200_000) break;
            Assert.True(DateTime.UtcNow < fundingDeadline, $"treasury funding never appeared (saw {seen} sats)");
            await Task.Delay(1500);
        }
    }
}
