using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Server;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Hosting;
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

    /// <summary>
    /// Full starter claim: quote → pay → claim. Starter heroes carry a fee (they cost what breeding one
    /// costs), so every test that starts with a roster now walks the paid path — which is the point: the
    /// flow every player takes on their first minute is the one under test, not a free shortcut past it.
    /// </summary>
    public static async Task<List<HeroDto>> ClaimStartersAsync(this ArkadeHeroesClient client)
    {
        var quote = await client.Heroes.RequestStartersAsync();
        if (quote.Fee is { } fee) await client.PayInvoiceAsync(fee.InvoiceId);
        return (await client.Heroes.ClaimStartersAsync()).Heroes.ToList();
    }

    /// <summary>
    /// A server with the daily sats faucet open. It ships CLOSED
    /// (<see cref="GameOptions.DailyRewardEnabled"/>): on an open signup the faucet pays real bitcoin to
    /// anyone who can make a keypair, and a keypair is free. So a test that exercises the daily loop has
    /// to open it the way an operator would — through configuration — rather than by reaching past the
    /// guard. Dispose the returned factory; it is a derived host, not the shared fixture.
    /// </summary>
    public static WebApplicationFactory<Program> WithDailyFaucetOpen(
        this WebApplicationFactory<Program> factory) =>
        factory.WithWebHostBuilder(b => b.UseSetting("Game:DailyRewardEnabled", "true"));

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
