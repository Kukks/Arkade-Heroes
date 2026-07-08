using ArkadeHeroes.Chain.Covenants;
using ArkadeHeroes.Chain.NArk;
using ArkadeHeroes.Client.Sdk;
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
        await RegtestHelper.ArkSend(_bob.Address, 50_000);   // bob stakes his own hero in the fully-funded reclaim E2E
        await _alice.WaitForBalanceAsync(50_000, TimeSpan.FromSeconds(60));
        await _bob.WaitForBalanceAsync(50_000, TimeSpan.FromSeconds(60));
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

    private async Task<ArkadeHeroesClient> RegisterAsync(string name, SelfCustodyWallet wallet)
    {
        var client = new ArkadeHeroesClient(_factory.CreateClient());
        await client.Players.RegisterAsync(new RegisterPlayerRequest(name, wallet.Address));
        return client;
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
        var bootInfo = await alice.Chain.InfoAsync();
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

        var aliceHeroes = await alice.Heroes.ClaimStartersAsync();
        var bobHeroes = await bob.Heroes.ClaimStartersAsync();

        // Covenant match; alice stakes into HER escrow; bob never accepts.
        var open = await alice.Matches.OpenAsync(
            new OpenMatchRequest(aliceHeroes.Heroes[0].Id, bobHeroes.Heroes[0].Id, Wager, "covenant"));
        Assert.NotNull(open.EscrowAddress);
        await _alice.SendAsync(open.EscrowAddress!, Wager);

        // The client-side trustless rebuild: params from the server, contracts
        // reconstructed locally — the challenger address MUST equal what alice
        // just staked to, or the server lied about the covenant.
        var parameters = await alice.Matches.EscrowAsync(open.MatchId);
        Assert.Equal(_alice.Address, parameters.ChallengerAddress);
        var transport = _alice.GetService<global::NArk.Core.Transport.IClientTransport>();
        var serverInfo = await transport.GetServerInfoAsync();
        var emulatorInfo = await new EmulatorClient(EmulatorUri).GetInfoAsync();
        var (challengerContract, _) = WagerEscrowContracts.Build(
            parameters, serverInfo.SignerKey, emulatorInfo.SignerPubkey);
        Assert.Equal(open.EscrowAddress,
            challengerContract.GetArkAddress().ToString(serverInfo.Network == NBitcoin.Network.Main));

        // Chain info advertises the endpoints the console client would use.
        var chainInfo = await alice.Chain.InfoAsync();
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
        var heroes = await alice.Heroes.ClaimStartersAsync();
        var baseHero = heroes.Heroes[0];
        var sacHero = heroes.Heroes[1];

        // Covenant merge; Alice deposits base + sacrifice + fee, then abandons it.
        var commit = await alice.Merge.CommitAsync(
            new MergeCommitRequest(baseHero.Id, sacHero.Id, "covenant"));
        await _alice.SendAssetAsync(commit.EscrowAddress, baseHero.AssetId!, 1);
        await _alice.SendAssetAsync(commit.EscrowAddress, sacHero.AssetId!, 1);
        await _alice.SendAsync(commit.EscrowAddress, commit.FeeSats);

        // The trustless rebuild: params from the server, contract reconstructed locally.
        var parameters = await alice.Merge.EscrowAsync(commit.MergeId);
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
    public async Task AbandonedBreedDeposit_PlayerReclaimsBothParentsViaTheClientFlow()
    {
        var alice = await RegisterAsync("Breed-Reclaim-Alice", _alice);
        await EnsureTreasuryFundedAsync(alice);
        var heroes = await alice.Heroes.ClaimStartersAsync();
        var parentA = heroes.Heroes[0];
        var parentB = heroes.Heroes[1];

        // Covenant breed; Alice deposits both parents + fee, then abandons it (never reveals).
        var commit = await alice.Breeding.CommitAsync(
            new BreedCommitRequest(parentA.Id, parentB.Id, "covenant"));
        Assert.NotNull(commit.EscrowAddress);
        await _alice.SendAssetAsync(commit.EscrowAddress!, parentA.AssetId!, 1);
        await _alice.SendAssetAsync(commit.EscrowAddress!, parentB.AssetId!, 1);
        await _alice.SendAsync(commit.EscrowAddress!, commit.EscrowFeeSats);

        // The trustless rebuild: params from the server, contract reconstructed locally.
        var parameters = await alice.Breeding.EscrowAsync(commit.BreedingId);
        Assert.Equal(_alice.Address, parameters.PlayerAddress);
        using var esploraHttp = new HttpClient();

        // Pre-expiry the flow's own gate refuses WITHOUT submitting anything.
        await Assert.ThrowsAsync<RefundNotYetDueException>(() => BreedEscrowRefundFlow.ReclaimAsync(
            _alice, EmulatorUri, parameters,
            ct => EsploraChainTime.GetMedianTimeAsync(esploraHttp, EsploraApi, ct)));

        await RegtestHelper.WaitForChainTimeAsync(parameters.RefundAfterUnixSeconds, TimeSpan.FromSeconds(120));
        var refund = await BreedEscrowRefundFlow.ReclaimAsync(
            _alice, EmulatorUri, parameters,
            ct => EsploraChainTime.GetMedianTimeAsync(esploraHttp, EsploraApi, ct));
        Assert.False(string.IsNullOrEmpty(refund.SignedArkTx));

        // Both parents land back in Alice's wallet.
        await _alice.WaitForAssetAsync(parentA.AssetId!, TimeSpan.FromSeconds(90));
        await _alice.WaitForAssetAsync(parentB.AssetId!, TimeSpan.FromSeconds(90));
    }

    [Fact]
    public async Task HalfFundedDeathMatch_ChallengerReclaimsAfterExpiry()
    {
        var alice = await RegisterAsync("DM-Reclaim-Alice", _alice);
        var bob = await RegisterAsync("DM-Reclaim-Bob", _bob);
        await EnsureTreasuryFundedAsync(alice);
        var aliceHeroes = await alice.Heroes.ClaimStartersAsync();
        var bobHeroes = await bob.Heroes.ClaimStartersAsync();
        var myHero = aliceHeroes.Heroes[0];

        // Alice opens + stakes her hero; Bob never accepts → half-funded, Alice is stranded.
        var open = await alice.DeathMatch.OpenAsync(
            new DeathMatchOpenRequest(myHero.Id, bobHeroes.Heroes[0].Id));
        await _alice.SendAssetAsync(open.EscrowAddress, myHero.AssetId!, 1);

        var parameters = await alice.DeathMatch.EscrowAsync(open.DeathMatchId);
        Assert.Equal(_alice.Address, parameters.ChallengerAddress);
        using var esploraHttp = new HttpClient();

        // Pre-expiry the flow's own gate refuses without submitting.
        await Assert.ThrowsAsync<RefundNotYetDueException>(() => DeathMatchRefundFlow.ReclaimAsync(
            _alice, EmulatorUri, parameters,
            ct => EsploraChainTime.GetMedianTimeAsync(esploraHttp, EsploraApi, ct)));

        // The trustless timelocked reclaim: after expiry, Alice's reclaim leaf brings her hero home.
        await RegtestHelper.WaitForChainTimeAsync(parameters.RefundAfterUnixSeconds, TimeSpan.FromSeconds(120));
        var reclaim = await DeathMatchRefundFlow.ReclaimAsync(
            _alice, EmulatorUri, parameters,
            ct => EsploraChainTime.GetMedianTimeAsync(esploraHttp, EsploraApi, ct));
        Assert.False(string.IsNullOrEmpty(reclaim.SignedArkTx));
        await _alice.WaitForAssetAsync(myHero.AssetId!, TimeSpan.FromSeconds(90));
    }

    [Fact]
    public async Task FullyFundedDeathMatch_EachSideReclaimsAfterExpiry()
    {
        var alice = await RegisterAsync("DM-Full-Alice", _alice);
        var bob = await RegisterAsync("DM-Full-Bob", _bob);
        await EnsureTreasuryFundedAsync(alice);
        var aliceHeroes = await alice.Heroes.ClaimStartersAsync();
        var bobHeroes = await bob.Heroes.ClaimStartersAsync();
        var aHero = aliceHeroes.Heroes[0];
        var bHero = bobHeroes.Heroes[0];

        var open = await alice.DeathMatch.OpenAsync(
            new DeathMatchOpenRequest(aHero.Id, bHero.Id));
        await _alice.SendAssetAsync(open.EscrowAddress, aHero.AssetId!, 1);
        await bob.DeathMatch.AcceptAsync(open.DeathMatchId);
        await _bob.SendAssetAsync(open.EscrowAddress, bHero.AssetId!, 1);

        var parameters = await alice.DeathMatch.EscrowAsync(open.DeathMatchId);
        using var esploraHttp = new HttpClient();
        await RegtestHelper.WaitForChainTimeAsync(parameters.RefundAfterUnixSeconds, TimeSpan.FromSeconds(120));

        // Each side reclaims their OWN hero (two independent txs, either order).
        var aReclaim = await DeathMatchRefundFlow.ReclaimAsync(
            _alice, EmulatorUri, parameters, ct => EsploraChainTime.GetMedianTimeAsync(esploraHttp, EsploraApi, ct));
        Assert.False(string.IsNullOrEmpty(aReclaim.SignedArkTx));
        var bReclaim = await DeathMatchRefundFlow.ReclaimAsync(
            _bob, EmulatorUri, parameters, ct => EsploraChainTime.GetMedianTimeAsync(esploraHttp, EsploraApi, ct));
        Assert.False(string.IsNullOrEmpty(bReclaim.SignedArkTx));

        await _alice.WaitForAssetAsync(aHero.AssetId!, TimeSpan.FromSeconds(90));
        await _bob.WaitForAssetAsync(bHero.AssetId!, TimeSpan.FromSeconds(90));
    }

    [Fact]
    public async Task Starter_SucceedsRightAfterTreasuryFunding_ServerAwaitsItsOwnSync()
    {
        // Regression for the treasury-sync race: fund the treasury, then mint IMMEDIATELY —
        // deliberately WITHOUT the test-side EnsureTreasuryFundedAsync poll. The server's own
        // mint path (EnsureSpeciesAssetAsync) must poll-and-wait for THIS instance's treasury
        // view; a single stale read of cached VTXO storage used to throw "Treasury wallet has
        // no funds" and flake the suite.
        var alice = await RegisterAsync("Treasury-Race-Alice", _alice);
        var info = await alice.Chain.InfoAsync();
        await RegtestHelper.ArkSend(info.TreasuryAddress, 200_000);

        var heroes = await alice.Heroes.ClaimStartersAsync();
        Assert.Equal(2, heroes.Heroes.Count);
    }

    /// <summary>Funds the fresh server's treasury and waits until the funding is indexer-visible — required before the first starter mint (as the wager fact does).</summary>
    private async Task EnsureTreasuryFundedAsync(ArkadeHeroesClient client)
    {
        var bootInfo = await client.Chain.InfoAsync();
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
