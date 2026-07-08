using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ArkadeHeroes.Tests;

/// <summary>
/// Helpers for the non-custodial flows over the InMemory chain, via the typed SDK:
/// players register a simulated self-custody address, and the dev facade stands in
/// for the player's own wallet (paying invoices, moving assets).
/// </summary>
internal static class NonCustodialTestHelpers
{
    public static async Task<(ArkadeHeroesClient Client, PlayerDto Player)> RegisterAsync(
        this WebApplicationFactory<Program> factory, string name)
    {
        var client = new ArkadeHeroesClient(factory.CreateClient());
        var address = $"sim-wallet-{Guid.NewGuid():N}";
        var player = await client.Players.RegisterAsync(new RegisterPlayerRequest(name, address));
        return (client, player);
    }

    public static async Task<List<HeroDto>> ClaimStartersAsync(this ArkadeHeroesClient client) =>
        (await client.Heroes.ClaimStartersAsync()).Heroes.ToList();

    /// <summary>Simulated client-wallet payment of a fee invoice.</summary>
    public static Task PayInvoiceAsync(this ArkadeHeroesClient client, string invoiceId) =>
        client.Dev.PayInvoiceAsync(new { InvoiceId = invoiceId });

    /// <summary>Simulated client-wallet asset move (hero transfer).</summary>
    public static Task TransferAssetAsync(this ArkadeHeroesClient client, string assetId, string toPlayerId) =>
        client.Dev.TransferAssetAsync(new { AssetId = assetId, ToPlayerId = toPlayerId });

    /// <summary>Full breed flow: commit → pay invoice → reveal.</summary>
    public static async Task<(BreedCommitResponse Commit, BreedRevealResponse Reveal)> BreedAsync(
        this ArkadeHeroesClient client, string parentAId, string parentBId, string nonce)
    {
        var commit = await client.Breeding.CommitAsync(new BreedCommitRequest(parentAId, parentBId));
        await client.PayInvoiceAsync(commit.Invoice!.InvoiceId);
        var reveal = await client.Breeding.RevealAsync(commit.BreedingId, new BreedRevealRequest(nonce));
        return (commit, reveal);
    }

    /// <summary>Full item purchase: invoice → pay → claim.</summary>
    public static async Task<ClaimItemResponse> BuyItemAsync(this ArkadeHeroesClient client, string itemId)
    {
        var invoice = (await client.Items.BuyAsync(itemId)).Invoice;
        await client.PayInvoiceAsync(invoice.InvoiceId);
        return await client.Items.ClaimAsync(new ClaimItemRequest(invoice.InvoiceId));
    }
}
