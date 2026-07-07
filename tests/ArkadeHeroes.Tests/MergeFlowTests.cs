using System.Net;
using System.Net.Http.Json;
using ArkadeHeroes.Core.Fairness;
using ArkadeHeroes.Core.Genetics;
using ArkadeHeroes.Server;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ArkadeHeroes.Tests;

/// <summary>
/// Escrow/treasury-mode merge over the real HTTP surface (in-memory chain):
/// deposit base + sacrifice + fee → reveal fuses them into ONE hero, retires
/// both inputs, and mints the fused hero. The fused genome is client-recomputable
/// (Fusion.Fuse) and the fused hero inherits the base's level (receipt-attested).
/// </summary>
public class MergeFlowTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public MergeFlowTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task Merge_ConsumesBothInputs_MintsAFusedHero_InheritsBaseLevel()
    {
        var (alice, _) = await _factory.RegisterAsync("Mrg-Alice");
        var heroes = await alice.ClaimStartersAsync();
        var store = _factory.Services.GetRequiredService<GameStore>();
        // Give the base a known level so we can assert inheritance.
        store.Heroes[heroes[0].Id].Level = 9;
        var baseId = heroes[0].Id; var sacId = heroes[1].Id;
        // Capture the input genomes BEFORE reveal deletes the records — needed to recompute the fusion.
        var baseGenomeHex = store.Heroes[baseId].Genome.ToHex();
        var sacGenomeHex = store.Heroes[sacId].Genome.ToHex();

        var commit = (await (await alice.PostAsJsonAsync("/api/merge/commit",
                new MergeCommitRequest(baseId, sacId)))
            .Content.ReadFromJsonAsync<MergeCommitResponse>())!;
        await alice.PostAsJsonAsync("/api/dev/fund-merge-escrow", new { MergeId = commit.MergeId });
        var reveal = (await (await alice.PostAsJsonAsync($"/api/merge/{commit.MergeId}/reveal",
                new MergeRevealRequest("merge-nonce")))
            .Content.ReadFromJsonAsync<MergeRevealResponse>())!;

        // A new fused hero exists, inherits the base's level + a bumped generation, and both inputs are gone.
        Assert.NotEqual(baseId, reveal.Hero.Id);
        Assert.NotEqual(sacId, reveal.Hero.Id);
        Assert.Equal(9, reveal.Hero.Level);
        Assert.Equal(Math.Max(heroes[0].Generation, heroes[1].Generation) + 1, reveal.Hero.Generation);
        var mine = (await alice.GetFromJsonAsync<List<HeroDto>>("/api/heroes/mine"))!;
        Assert.DoesNotContain(mine, h => h.Id == baseId || h.Id == sacId);
        Assert.Contains(mine, h => h.Id == reveal.Hero.Id);

        // The revealed entropy is the documented derivation, and the fused genome is
        // exactly Fusion.Fuse(base, sacrifice, entropy) — the client-verifiable recompute.
        var entropy = CommitReveal.DeriveEntropy(
            Convert.FromHexString(reveal.ServerSeedHex), commit.MergeId, baseId, sacId, "merge-nonce");
        Assert.Equal(Convert.ToHexString(entropy).ToLowerInvariant(), reveal.EntropyHex);
        var expected = Fusion.Fuse(Genome.FromHex(baseGenomeHex), Genome.FromHex(sacGenomeHex), entropy);
        Assert.Equal(expected.ToHex(), reveal.Hero.GenomeHex);

        // The receipt verifies (signature + commit–reveal) and its merge-genesis level
        // replays to the inherited level — inheritance stays trustlessly consistent.
        Assert.NotNull(reveal.Receipt);
        Assert.Equal("merge", reveal.Receipt!.Type);
        Assert.True(ReceiptVerifier.Verify(reveal.Receipt).Ok);
        Assert.Equal(9, ReceiptVerifier.ReplayLevel(reveal.Hero.Id, [reveal.Receipt]));
    }

    [Fact]
    public async Task Merge_RejectsSelf_Unowned_AndRevealBeforeFunding()
    {
        var (alice, _) = await _factory.RegisterAsync("Mrg-Self");
        var (bob, _) = await _factory.RegisterAsync("Mrg-Bob");
        var a = await alice.ClaimStartersAsync();
        var b = await bob.ClaimStartersAsync();

        // Base == sacrifice is refused.
        var self = await alice.PostAsJsonAsync("/api/merge/commit", new MergeCommitRequest(a[0].Id, a[0].Id));
        Assert.Equal(HttpStatusCode.BadRequest, self.StatusCode);

        // Merging someone else's hero is refused.
        var steal = await alice.PostAsJsonAsync("/api/merge/commit", new MergeCommitRequest(a[0].Id, b[0].Id));
        Assert.Equal(HttpStatusCode.BadRequest, steal.StatusCode);

        // Reveal before the escrow is funded is refused (deposit-gated).
        var commit = (await (await alice.PostAsJsonAsync("/api/merge/commit",
                new MergeCommitRequest(a[0].Id, a[1].Id)))
            .Content.ReadFromJsonAsync<MergeCommitResponse>())!;
        var early = await alice.PostAsJsonAsync($"/api/merge/{commit.MergeId}/reveal", new MergeRevealRequest("n"));
        Assert.Equal(HttpStatusCode.BadRequest, early.StatusCode);
    }

    [Fact]
    public async Task Merge_EscrowParamsAreServedForTrustlessRebuild()
    {
        var (alice, _) = await _factory.RegisterAsync("Mrg-Escrow");
        var heroes = await alice.ClaimStartersAsync();

        var commit = (await (await alice.PostAsJsonAsync("/api/merge/commit",
                new MergeCommitRequest(heroes[0].Id, heroes[1].Id)))
            .Content.ReadFromJsonAsync<MergeCommitResponse>())!;

        // The escrow params are publicly rebuildable (base + sacrifice + mergeId), so a
        // player can reconstruct the contract and reclaim an abandoned deposit trustlessly.
        var escrow = await alice.GetAsync($"/api/merges/{commit.MergeId}/escrow");
        Assert.Equal(HttpStatusCode.OK, escrow.StatusCode);
        var body = await escrow.Content.ReadAsStringAsync();
        Assert.Contains(commit.MergeId, body);
        Assert.Contains(heroes[0].AssetId!, body);
        Assert.Contains(heroes[1].AssetId!, body);

        // Unknown merge → 404.
        var missing = await alice.GetAsync("/api/merges/does-not-exist/escrow");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Merge_AbandonedDeposit_RefundReturnsFee_ThenNothingToRefund()
    {
        // Zero refund window so the abandoned deposit is immediately reclaimable in-test.
        using var factory = _factory.WithWebHostBuilder(b => b.UseSetting("Game:WagerEscrowRefundAfter", "00:00:00"));
        var (alice, _) = await factory.RegisterAsync("Mrg-Refund");
        var heroes = await alice.ClaimStartersAsync();

        var meStart = (await alice.GetFromJsonAsync<PlayerDto>("/api/players/me"))!;
        var commit = (await (await alice.PostAsJsonAsync("/api/merge/commit",
                new MergeCommitRequest(heroes[0].Id, heroes[1].Id)))
            .Content.ReadFromJsonAsync<MergeCommitResponse>())!;
        await alice.PostAsJsonAsync("/api/dev/fund-merge-escrow", new { MergeId = commit.MergeId });

        // Abandon → refund returns the fee (window is zero, so it's immediately due).
        var refund = await alice.PostAsJsonAsync("/api/dev/refund-merge", new { MergeId = commit.MergeId });
        Assert.True(refund.IsSuccessStatusCode, await refund.Content.ReadAsStringAsync());
        var meEnd = (await alice.GetFromJsonAsync<PlayerDto>("/api/players/me"))!;
        Assert.Equal(meStart.BalanceSats, meEnd.BalanceSats); // fund − fee then refund + fee = net zero

        // A second refund finds nothing to reclaim.
        var again = await alice.PostAsJsonAsync("/api/dev/refund-merge", new { MergeId = commit.MergeId });
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
    }
}
