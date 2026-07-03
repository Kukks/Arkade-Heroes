using System.Net.Http.Headers;
using System.Net.Http.Json;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ArkadeHeroes.Tests;

/// <summary>
/// Helpers for the non-custodial flows over the InMemory chain: players
/// register a simulated self-custody address, and the dev endpoints stand in
/// for the player's own wallet (paying invoices, moving assets).
/// </summary>
internal static class NonCustodialTestHelpers
{
    public static async Task<(HttpClient Client, PlayerDto Player)> RegisterAsync(
        this WebApplicationFactory<Program> factory, string name)
    {
        var client = factory.CreateClient();
        var address = $"sim-wallet-{Guid.NewGuid():N}";
        var response = await client.PostAsJsonAsync("/api/players", new RegisterPlayerRequest(name, address));
        response.EnsureSuccessStatusCode();
        var player = (await response.Content.ReadFromJsonAsync<PlayerDto>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", player.Token);
        return (client, player);
    }

    public static async Task<List<HeroDto>> ClaimStartersAsync(this HttpClient client)
    {
        var response = await client.PostAsync("/api/heroes/starter", null);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<StarterResponse>())!.Heroes.ToList();
    }

    /// <summary>Simulated client-wallet payment of a fee invoice.</summary>
    public static async Task PayInvoiceAsync(this HttpClient client, string invoiceId)
    {
        var response = await client.PostAsJsonAsync("/api/dev/pay-invoice", new { InvoiceId = invoiceId });
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Simulated client-wallet asset move (hero transfer).</summary>
    public static async Task TransferAssetAsync(this HttpClient client, string assetId, string toPlayerId)
    {
        var response = await client.PostAsJsonAsync("/api/dev/transfer-asset",
            new { AssetId = assetId, ToPlayerId = toPlayerId });
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Full breed flow: commit → pay invoice → reveal.</summary>
    public static async Task<(BreedCommitResponse Commit, BreedRevealResponse Reveal)> BreedAsync(
        this HttpClient client, string parentAId, string parentBId, string nonce)
    {
        var commitResponse = await client.PostAsJsonAsync("/api/breeding/commit",
            new BreedCommitRequest(parentAId, parentBId));
        commitResponse.EnsureSuccessStatusCode();
        var commit = (await commitResponse.Content.ReadFromJsonAsync<BreedCommitResponse>())!;

        await client.PayInvoiceAsync(commit.Invoice.InvoiceId);

        var revealResponse = await client.PostAsJsonAsync($"/api/breeding/{commit.BreedingId}/reveal",
            new BreedRevealRequest(nonce));
        revealResponse.EnsureSuccessStatusCode();
        var reveal = (await revealResponse.Content.ReadFromJsonAsync<BreedRevealResponse>())!;
        return (commit, reveal);
    }

    /// <summary>Full item purchase: invoice → pay → claim.</summary>
    public static async Task<ClaimItemResponse> BuyItemAsync(this HttpClient client, string itemId)
    {
        var invoiceResponse = await client.PostAsync($"/api/items/{itemId}/buy", null);
        invoiceResponse.EnsureSuccessStatusCode();
        var invoice = (await invoiceResponse.Content.ReadFromJsonAsync<ItemInvoiceResponse>())!.Invoice;

        await client.PayInvoiceAsync(invoice.InvoiceId);

        var claimResponse = await client.PostAsJsonAsync("/api/items/claim", new ClaimItemRequest(invoice.InvoiceId));
        claimResponse.EnsureSuccessStatusCode();
        return (await claimResponse.Content.ReadFromJsonAsync<ClaimItemResponse>())!;
    }
}
