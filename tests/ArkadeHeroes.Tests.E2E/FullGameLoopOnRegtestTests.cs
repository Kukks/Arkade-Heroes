using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ArkadeHeroes.Chain.NArk;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Mvc.Testing;
using NArk.Transport.GrpcClient;

namespace ArkadeHeroes.Tests.E2E;

/// <summary>
/// The whole game loop against a REAL Arkade operator (regtest arkd) under the
/// non-custodial mandate: each player runs a real <see cref="SelfCustodyWallet"/>
/// (keys generated locally, never shared), registers only their address, pays
/// fee/stake invoices from their own wallet, receives hero/item assets
/// directly, and signs hero transfers themselves. The server holds only its
/// treasury. Requires the regtest stack:
///   node regtest/regtest.mjs start --profile ark --profile emulator
/// </summary>
public class FullGameLoopOnRegtestTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private WebApplicationFactory<Program> _factory = null!;
    private string _serverDbPath = null!;
    private readonly List<string> _walletDbPaths = [];
    private readonly List<SelfCustodyWallet> _wallets = [];

    public async Task InitializeAsync()
    {
        await RegtestHelper.WaitForArkdReadyAsync(TimeSpan.FromSeconds(30));

        _serverDbPath = Path.Combine(Path.GetTempPath(), $"arkade-heroes-e2e-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("Chain__Mode", "NArk");
        Environment.SetEnvironmentVariable("Chain__NArk__ArkUri", "http://localhost:7070");
        Environment.SetEnvironmentVariable("Chain__NArk__EsploraUri", "http://localhost:3000/api");
        Environment.SetEnvironmentVariable("Chain__NArk__DbPath", _serverDbPath);
        Environment.SetEnvironmentVariable("Game__BreedingCooldownBaseUnit", "00:00:02");

        _factory = new WebApplicationFactory<Program>();
    }

    public async Task DisposeAsync()
    {
        foreach (var wallet in _wallets)
            await wallet.DisposeAsync();
        _factory.Dispose();
        foreach (var path in _walletDbPaths.Append(_serverDbPath))
            try { if (File.Exists(path)) File.Delete(path); } catch { /* locked on Windows is fine */ }
    }

    private async Task<SelfCustodyWallet> NewWalletAsync()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"ah-wallet-{Guid.NewGuid():N}.db");
        _walletDbPaths.Add(dbPath);
        var wallet = await SelfCustodyWallet.CreateAsync(new SelfCustodyWalletOptions
        {
            ArkUri = "http://localhost:7070",
            DbPath = dbPath,
        });
        _wallets.Add(wallet);
        return wallet;
    }

    private async Task<(HttpClient Client, PlayerDto Player)> RegisterAsync(string name, SelfCustodyWallet wallet)
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/players",
            new RegisterPlayerRequest(name, wallet.Address));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"register failed: {body}");
        var player = JsonSerializer.Deserialize<PlayerDto>(body, Web)!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", player.Token);
        return (client, player);
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

    private static async Task PollUntilAsync(Func<Task<bool>> probe, TimeSpan timeout, string what)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await probe()) return;
            await Task.Delay(1500);
        }
        throw new TimeoutException($"Timed out waiting for {what}.");
    }

    /// <summary>Poll variant that reports the last HTTP response body on timeout.</summary>
    private static async Task<string> PollHttpUntilOkAsync(
        Func<Task<HttpResponseMessage>> request, TimeSpan timeout, string what)
    {
        var deadline = DateTime.UtcNow + timeout;
        var lastBody = "(no attempt)";
        while (DateTime.UtcNow < deadline)
        {
            var response = await request();
            lastBody = await response.Content.ReadAsStringAsync();
            if (response.IsSuccessStatusCode) return lastBody;
            await Task.Delay(1500);
        }
        throw new TimeoutException($"Timed out waiting for {what}. Last response: {lastBody}");
    }

    [Fact]
    public async Task FullGameLoop_SelfCustody_OnRegtest()
    {
        var anonymous = _factory.CreateClient();

        // ── Treasury boots and gets funded ─────────────────────────────
        var chainInfo = (await anonymous.GetFromJsonAsync<ChainInfoDto>("/api/chain/info"))!;
        Assert.Equal("NArk", chainInfo.Mode);
        await RegtestHelper.ArkSend(chainInfo.TreasuryAddress, 200_000);

        // Covenant co-signer present.
        Assert.False(string.IsNullOrEmpty(chainInfo.EmulatorSignerKey),
            "emulator signer key missing — is the emulator container running at :7073?");

        // ── Players: REAL self-custody wallets, server sees only addresses ─
        var aliceWallet = await NewWalletAsync();
        var bobWallet = await NewWalletAsync();
        Assert.StartsWith("t", aliceWallet.Address); // tark1... on regtest

        await RegtestHelper.ArkSend(aliceWallet.Address, 100_000);
        await RegtestHelper.ArkSend(bobWallet.Address, 100_000);
        await aliceWallet.WaitForBalanceAsync(100_000, TimeSpan.FromSeconds(60));
        await bobWallet.WaitForBalanceAsync(100_000, TimeSpan.FromSeconds(60));

        var (alice, _) = await RegisterAsync("Alice", aliceWallet);
        var (bob, bobPlayer) = await RegisterAsync("Bob", bobWallet);

        // ── Starters: minted by the treasury straight into player wallets ─
        var aliceHeroes = (await PostOkAsync<StarterResponse>(alice, "/api/heroes/starter")).Heroes.ToList();
        var bobHeroes = (await PostOkAsync<StarterResponse>(bob, "/api/heroes/starter")).Heroes.ToList();
        Assert.Equal(2, aliceHeroes.Count);

        // The hero assets are IN Alice's own wallet — she truly holds them.
        await aliceWallet.WaitForAssetAsync(aliceHeroes[0].AssetId!, TimeSpan.FromSeconds(30));
        await aliceWallet.WaitForAssetAsync(aliceHeroes[1].AssetId!, TimeSpan.FromSeconds(30));

        // On-chain proof via arkd directly.
        var transport = new GrpcClientTransport("http://localhost:7070");
        var details = await transport.GetAssetDetailsAsync(aliceHeroes[0].AssetId!);
        Assert.Equal(1UL, details.Supply);

        // ── Breed: invoice paid from Alice's own wallet ────────────────
        var commit = await PostOkAsync<BreedCommitResponse>(alice, "/api/breeding/commit",
            new BreedCommitRequest(aliceHeroes[0].Id, aliceHeroes[1].Id));

        // Reveal before paying is refused.
        var unpaid = await alice.PostAsJsonAsync($"/api/breeding/{commit.BreedingId}/reveal",
            new BreedRevealRequest("nope"));
        Assert.Equal(HttpStatusCode.BadRequest, unpaid.StatusCode);

        await aliceWallet.SendAsync(commit.Invoice.PayToAddress, commit.Invoice.AmountSats);
        await PollUntilAsync(async () =>
        {
            var probe = await alice.PostAsJsonAsync($"/api/breeding/{commit.BreedingId}/reveal",
                new BreedRevealRequest("e2e-nonce-1"));
            return probe.IsSuccessStatusCode || await ProbeCompleted(probe);
        }, TimeSpan.FromSeconds(45), "breeding fee to be observed and reveal to succeed");

        // The reveal poll above may have succeeded inside the loop; fetch the child.
        var aliceMine = (await alice.GetFromJsonAsync<List<HeroDto>>("/api/heroes/mine"))!;
        var child = aliceMine.Single(h => h.Generation == 1);
        Assert.Equal(aliceHeroes[0].Id, child.ParentAId);

        // Fairness audit from the child's provenance (seed/nonce/entropy are public).
        Assert.NotNull(child.Provenance?.EntropyHex);
        await aliceWallet.WaitForAssetAsync(child.AssetId!, TimeSpan.FromSeconds(30));

        // ── Transfer: ALICE signs the asset move; the server only verifies ─
        await aliceWallet.SendAssetAsync(bobWallet.Address, child.AssetId!, 1);
        await bobWallet.WaitForAssetAsync(child.AssetId!, TimeSpan.FromSeconds(30));

        await PollUntilAsync(async () =>
        {
            var confirm = await alice.PostAsJsonAsync($"/api/heroes/{child.Id}/transfer",
                new TransferRequest(bobPlayer.PlayerId));
            return confirm.IsSuccessStatusCode;
        }, TimeSpan.FromSeconds(45), "server to verify the client-signed transfer");

        var bobMine = (await bob.GetFromJsonAsync<List<HeroDto>>("/api/heroes/mine"))!;
        Assert.Contains(bobMine, h => h.Id == child.Id);

        // ── Wagered match: stakes paid by each player's own wallet ─────
        const long wager = 2_000;
        var open = await PostOkAsync<OpenMatchResponse>(alice, "/api/matches/open",
            new OpenMatchRequest(aliceHeroes[0].Id, bobHeroes[0].Id, wager));
        Assert.NotNull(open.StakeInvoice);
        await aliceWallet.SendAsync(open.StakeInvoice!.PayToAddress, open.StakeInvoice.AmountSats);
        // Each fighter also pays their per-character match fee from their own wallet.
        await aliceWallet.SendAsync(open.MatchFeeInvoice!.PayToAddress, open.MatchFeeInvoice.AmountSats);

        var accept = await PostOkAsync<AcceptMatchResponse>(bob, $"/api/matches/{open.MatchId}/accept");
        await bobWallet.SendAsync(accept.StakeInvoice.PayToAddress, accept.StakeInvoice.AmountSats);
        await bobWallet.SendAsync(accept.MatchFeeInvoice!.PayToAddress, accept.MatchFeeInvoice.AmountSats);

        FightResponse? duel = null;
        await PollUntilAsync(async () =>
        {
            var response = await alice.PostAsJsonAsync($"/api/matches/{open.MatchId}/fight",
                new FightRequest("e2e-duel-nonce"));
            if (!response.IsSuccessStatusCode) return false;
            duel = JsonSerializer.Deserialize<FightResponse>(
                await response.Content.ReadAsStringAsync(), Web);
            return true;
        }, TimeSpan.FromSeconds(45), "both stakes to be observed and the duel to resolve");

        Assert.Equal(wager * 2, duel!.WinnerPayoutSats);
        var (duelOk, duelDetail) = FairnessAudit.VerifyMatch(
            open.MatchId, "e2e-duel-nonce", open.CommitmentHex, duel);
        Assert.True(duelOk, duelDetail);

        // Portable progression: the duel comes with a signed receipt that
        // verifies against the game key advertised in chain info.
        Assert.NotNull(duel.Receipt);
        var (receiptOk, receiptDetail) = ReceiptVerifier.Verify(duel.Receipt!);
        Assert.True(receiptOk, receiptDetail);
        var infoNow = (await anonymous.GetFromJsonAsync<ChainInfoDto>("/api/chain/info"))!;
        Assert.Equal(infoNow.GameSignerKey, duel.Receipt!.GameSignerKeyHex);

        // ── Covenant-mode wagered match: emulator-enforced escrow ──────
        const long covenantWager = 3_000;
        var covenantOpen = await PostOkAsync<OpenMatchResponse>(alice, "/api/matches/open",
            new OpenMatchRequest(aliceHeroes[0].Id, bobHeroes[0].Id, covenantWager, "covenant"));
        Assert.NotNull(covenantOpen.EscrowAddress);
        Assert.Null(covenantOpen.StakeInvoice);

        // Both players stake into the escrow from their OWN wallets.
        await aliceWallet.SendAsync(covenantOpen.EscrowAddress!, covenantWager);
        // …plus their per-character match fee (a treasury invoice, separate from the escrow).
        await aliceWallet.SendAsync(covenantOpen.MatchFeeInvoice!.PayToAddress, covenantOpen.MatchFeeInvoice.AmountSats);
        var covenantAccept = await PostOkAsync<AcceptMatchResponse>(bob, $"/api/matches/{covenantOpen.MatchId}/accept");
        // Per-party escrows: the defender stakes into their OWN address.
        Assert.NotNull(covenantAccept.EscrowAddress);
        Assert.NotEqual(covenantOpen.EscrowAddress, covenantAccept.EscrowAddress);
        await bobWallet.SendAsync(covenantAccept.EscrowAddress!, covenantWager);
        await bobWallet.SendAsync(covenantAccept.MatchFeeInvoice!.PayToAddress, covenantAccept.MatchFeeInvoice.AmountSats);

        // Snapshot AFTER both stakes and both fees have left each wallet, so the
        // settle assertion below measures only the pot arriving at the winner.
        var aliceBeforeSettle = await aliceWallet.GetBalanceSatsAsync();
        var bobBeforeSettle = await bobWallet.GetBalanceSatsAsync();

        FightResponse? covenantDuel = null;
        await PollUntilAsync(async () =>
        {
            var response = await alice.PostAsJsonAsync($"/api/matches/{covenantOpen.MatchId}/fight",
                new FightRequest("e2e-covenant-duel"));
            if (!response.IsSuccessStatusCode) return false;
            covenantDuel = JsonSerializer.Deserialize<FightResponse>(
                await response.Content.ReadAsStringAsync(), Web);
            return true;
        }, TimeSpan.FromSeconds(60), "escrow funding to be observed and the covenant duel to settle");

        Assert.Equal(covenantWager * 2, covenantDuel!.WinnerPayoutSats);
        var (covOk, covDetail) = FairnessAudit.VerifyMatch(
            covenantOpen.MatchId, "e2e-covenant-duel", covenantOpen.CommitmentHex, covenantDuel);
        Assert.True(covOk, covDetail);

        // The pot arrived at the WINNER'S own wallet, swept from the escrow by
        // the emulator-co-signed covenant transaction.
        var covenantChallengerWon = covenantDuel.Result.WinnerId == aliceHeroes[0].Id;
        var winnerWallet = covenantChallengerWon ? aliceWallet : bobWallet;
        var winnerBefore = covenantChallengerWon ? aliceBeforeSettle : bobBeforeSettle;
        await winnerWallet.WaitForBalanceAsync(winnerBefore + covenantWager * 2, TimeSpan.FromSeconds(45));

        // ── Shop: invoice → Alice pays → claim → the unit is in HER wallet ─
        var itemInvoice = (await PostOkAsync<ItemInvoiceResponse>(alice, "/api/items/rusty-blade/buy")).Invoice;
        await aliceWallet.SendAsync(itemInvoice.PayToAddress, itemInvoice.AmountSats);

        var claimBody = await PollHttpUntilOkAsync(
            () => alice.PostAsJsonAsync("/api/items/claim", new ClaimItemRequest(itemInvoice.InvoiceId)),
            TimeSpan.FromSeconds(150), "item payment to be observed and the claim to deliver");
        var claim = JsonSerializer.Deserialize<ClaimItemResponse>(claimBody, Web);

        Assert.Equal(1UL, claim!.UnitsHeld);
        await aliceWallet.WaitForAssetAsync(claim.ItemAssetId, TimeSpan.FromSeconds(30));

        var itemDetails = await transport.GetAssetDetailsAsync(claim.ItemAssetId);
        Assert.Equal(1000UL, itemDetails.Supply);

        // Equip the held unit on Alice's remaining hero.
        var equip = await alice.PostAsJsonAsync($"/api/heroes/{aliceHeroes[0].Id}/equip",
            new EquipRequest("rusty-blade"));
        var equipBody = await equip.Content.ReadAsStringAsync();
        Assert.True(equip.IsSuccessStatusCode, $"equip failed: {equipBody}");

        // Unequip frees the slot (server-side bookkeeping; the item asset stays in Alice's wallet).
        var unequip = await alice.PostAsJsonAsync($"/api/heroes/{aliceHeroes[0].Id}/unequip",
            new UnequipRequest("Weapon"));
        Assert.True(unequip.IsSuccessStatusCode, $"unequip failed: {await unequip.Content.ReadAsStringAsync()}");
        var afterUnequip = (await alice.GetFromJsonAsync<HeroDto>($"/api/heroes/{aliceHeroes[0].Id}", Web))!;
        Assert.DoesNotContain("rusty-blade", afterUnequip.Equipment.Values);

        // Friendly fight (no stakes): resolves immediately and carries a verifiable receipt.
        var friendlyOpen = await PostOkAsync<OpenMatchResponse>(alice, "/api/matches/open",
            new OpenMatchRequest(aliceHeroes[0].Id, bobHeroes[0].Id));
        var friendly = await PostOkAsync<FightResponse>(alice, $"/api/matches/{friendlyOpen.MatchId}/fight",
            new FightRequest("e2e-friendly"));
        Assert.False(string.IsNullOrEmpty(friendly.Result.WinnerId));
        var (friendlyOk, friendlyDetail) = FairnessAudit.VerifyMatch(
            friendlyOpen.MatchId, "e2e-friendly", friendlyOpen.CommitmentHex, friendly);
        Assert.True(friendlyOk, friendlyDetail);
    }

    /// <summary>Treats "already completed" as success for the reveal poll (a prior iteration won the race).</summary>
    private static async Task<bool> ProbeCompleted(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return body.Contains("already completed");
    }
}
