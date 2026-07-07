using System.Net;
using System.Net.Http.Json;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ArkadeHeroes.Tests;

/// <summary>
/// Covenant-mode breeding over the InMemory escrow simulation: the player
/// deposits BOTH parents plus the fee into a breed escrow (no fee invoice),
/// reveal is gated on the escrow being funded, and the child is minted via the
/// covenant path (oracle sig over the child's metadata root) — the same
/// lifecycle NArk mode enforces via the emulator on regtest.
/// </summary>
public class CovenantBreedFlowTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public CovenantBreedFlowTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task CovenantBreed_EscrowGatesTheReveal_ChildMinted()
    {
        var (alice, _) = await _factory.RegisterAsync("CB-Alice");
        var heroes = await alice.ClaimStartersAsync();

        // Commit in covenant mode: an escrow address, no fee invoice.
        var commit = (await (await alice.PostAsJsonAsync("/api/breeding/commit",
                new BreedCommitRequest(heroes[0].Id, heroes[1].Id, "covenant")))
            .Content.ReadFromJsonAsync<BreedCommitResponse>())!;
        Assert.Null(commit.Invoice);
        Assert.NotNull(commit.EscrowAddress);
        Assert.True(commit.EscrowFeeSats > 0);

        // Reveal is blocked until the parents + fee are deposited.
        var early = await alice.PostAsJsonAsync($"/api/breeding/{commit.BreedingId}/reveal",
            new BreedRevealRequest("n0nce"));
        Assert.Equal(HttpStatusCode.BadRequest, early.StatusCode);

        // The escrow params are publicly rebuildable (trustless refund basis).
        var parameters = (await alice.GetFromJsonAsync<Chain.Covenants.BreedEscrowParams>(
            $"/api/breedings/{commit.BreedingId}/escrow"))!;
        Assert.Equal(commit.BreedingId, parameters.BreedingId);
        Assert.Equal(heroes[0].AssetId, parameters.ParentAId);
        Assert.Equal(heroes[1].AssetId, parameters.ParentBId);
        Assert.Equal(64, parameters.OraclePkHex.Length);

        // Deposit both parents + the fee, then reveal succeeds via the covenant.
        await alice.PostAsJsonAsync("/api/dev/fund-breed-escrow", new { BreedingId = commit.BreedingId });
        var reveal = (await (await alice.PostAsJsonAsync($"/api/breeding/{commit.BreedingId}/reveal",
                new BreedRevealRequest("n0nce")))
            .Content.ReadFromJsonAsync<BreedRevealResponse>())!;
        Assert.NotNull(reveal.Hero);
        Assert.Equal(1, reveal.Hero.Generation);

        // The child is auditable + carries a signed receipt, same as invoice mode.
        var (ok, detail) = FairnessAudit.VerifyBreeding(
            heroes[0], heroes[1], "n0nce", commit.CommitmentHex, reveal);
        Assert.True(ok, detail);
        Assert.NotNull(reveal.Receipt);
    }

    [Fact]
    public async Task CovenantBreed_EscrowParams404ForInvoiceMode()
    {
        var (alice, _) = await _factory.RegisterAsync("CB-Invoice");
        var heroes = await alice.ClaimStartersAsync();
        var commit = (await (await alice.PostAsJsonAsync("/api/breeding/commit",
                new BreedCommitRequest(heroes[0].Id, heroes[1].Id, "invoice")))
            .Content.ReadFromJsonAsync<BreedCommitResponse>())!;
        Assert.NotNull(commit.Invoice);

        var response = await alice.GetAsync($"/api/breedings/{commit.BreedingId}/escrow");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CovenantBreed_AbandonedDeposit_RefundReturnsFee_ThenNothingToRefund()
    {
        // Zero refund window so the abandoned deposit is immediately reclaimable in-test.
        using var factory = _factory.WithWebHostBuilder(b => b.UseSetting("Game:WagerEscrowRefundAfter", "00:00:00"));
        var (alice, _) = await factory.RegisterAsync("CB-Refund");
        var heroes = await alice.ClaimStartersAsync();

        var meStart = (await alice.GetFromJsonAsync<PlayerDto>("/api/players/me"))!;
        var commit = (await (await alice.PostAsJsonAsync("/api/breeding/commit",
                new BreedCommitRequest(heroes[0].Id, heroes[1].Id, "covenant")))
            .Content.ReadFromJsonAsync<BreedCommitResponse>())!;
        await alice.PostAsJsonAsync("/api/dev/fund-breed-escrow", new { BreedingId = commit.BreedingId });

        // Abandon → refund returns the fee (window is zero, so it's immediately due).
        var refund = await alice.PostAsJsonAsync("/api/dev/refund-breed", new { BreedingId = commit.BreedingId });
        Assert.True(refund.IsSuccessStatusCode, await refund.Content.ReadAsStringAsync());
        var meEnd = (await alice.GetFromJsonAsync<PlayerDto>("/api/players/me"))!;
        Assert.Equal(meStart.BalanceSats, meEnd.BalanceSats); // fund − fee then refund + fee = net zero

        // A second refund finds nothing to reclaim.
        var again = await alice.PostAsJsonAsync("/api/dev/refund-breed", new { BreedingId = commit.BreedingId });
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
    }
}
