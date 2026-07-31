using ArkadeHeroes.Chain.NArk;
using ArkadeHeroes.Client.Sdk;
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

    private async Task<(ArkadeHeroesClient Client, PlayerDto Player)> RegisterAsync(string name, SelfCustodyWallet wallet)
    {
        var client = new ArkadeHeroesClient(_factory.CreateClient());
        var player = await client.Players.RegisterAsync(new RegisterPlayerRequest(name, wallet.Address));
        return (client, player);
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

    [Fact]
    public async Task FullGameLoop_SelfCustody_OnRegtest()
    {
        var anonymous = new ArkadeHeroesClient(_factory.CreateClient());

        // ── Treasury boots and gets funded ─────────────────────────────
        var chainInfo = await anonymous.Chain.InfoAsync();
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

        // ── Starters: BOUGHT from the treasury, minted straight into player wallets ─
        // A recruit mints one hero, so Alice — who breeds below — buys the pair one purchase at a time.
        var aliceHeroes = await alice.RecruitAsync(aliceWallet, 2);
        var bobHeroes = await bob.RecruitAsync(bobWallet);
        Assert.Equal(2, aliceHeroes.Count);

        // The hero assets are IN Alice's own wallet — she truly holds them.
        await aliceWallet.WaitForAssetAsync(aliceHeroes[0].AssetId!, TimeSpan.FromSeconds(30));
        await aliceWallet.WaitForAssetAsync(aliceHeroes[1].AssetId!, TimeSpan.FromSeconds(30));

        // On-chain proof via arkd directly.
        var transport = new GrpcClientTransport("http://localhost:7070");
        var details = await transport.GetAssetDetailsAsync(aliceHeroes[0].AssetId!);
        Assert.Equal(1UL, details.Supply);

        // ── Breed: invoice paid from Alice's own wallet ────────────────
        var commit = await alice.Breeding.CommitAsync(
            new BreedCommitRequest(aliceHeroes[0].Id, aliceHeroes[1].Id));

        // Reveal before paying is refused.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => alice.Breeding.RevealAsync(
            commit.BreedingId, new BreedRevealRequest("nope")));

        await aliceWallet.SendAsync(commit.Invoice.PayToAddress, commit.Invoice.AmountSats);
        await PollUntilAsync(async () =>
        {
            try
            {
                await alice.Breeding.RevealAsync(commit.BreedingId, new BreedRevealRequest("e2e-nonce-1"));
                return true;
            }
            catch (ArkadeHeroesApiException ex)
            {
                return ex.Message.Contains("already completed");
            }
        }, TimeSpan.FromSeconds(45), "breeding fee to be observed and reveal to succeed");

        // The reveal poll above may have succeeded inside the loop; fetch the child.
        var aliceMine = await alice.Heroes.MineAsync();
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
            try
            {
                await alice.Heroes.TransferAsync(child.Id, new TransferRequest(bobPlayer.PlayerId));
                return true;
            }
            catch (ArkadeHeroesApiException)
            {
                return false;
            }
        }, TimeSpan.FromSeconds(45), "server to verify the client-signed transfer");

        var bobMine = await bob.Heroes.MineAsync();
        Assert.Contains(bobMine, h => h.Id == child.Id);

        // ── Wagered match: stakes paid by each player's own wallet ─────
        const long wager = 2_000;
        var open = await alice.Matches.OpenAsync(
            new OpenMatchRequest(aliceHeroes[0].Id, bobHeroes[0].Id, wager));
        Assert.NotNull(open.StakeInvoice);
        await aliceWallet.SendAsync(open.StakeInvoice!.PayToAddress, open.StakeInvoice.AmountSats);
        // Each fighter also pays their per-character match fee from their own wallet.
        await aliceWallet.SendAsync(open.MatchFeeInvoice!.PayToAddress, open.MatchFeeInvoice.AmountSats);

        var accept = await bob.Matches.AcceptAsync(open.MatchId);
        await bobWallet.SendAsync(accept.StakeInvoice.PayToAddress, accept.StakeInvoice.AmountSats);
        await bobWallet.SendAsync(accept.MatchFeeInvoice!.PayToAddress, accept.MatchFeeInvoice.AmountSats);

        FightResponse? duel = null;
        await PollUntilAsync(async () =>
        {
            try
            {
                duel = await alice.Matches.FightAsync(open.MatchId, new FightRequest("e2e-duel-nonce"));
                return true;
            }
            catch (ArkadeHeroesApiException)
            {
                return false;
            }
        }, TimeSpan.FromSeconds(45), "both stakes to be observed and the duel to resolve");

        Assert.Equal(wager * 2, duel!.WinnerPayoutSats);
        var (duelCfg, duelCfgError) = await alice.Config.ResolveAsync(duel.ConfigVersion);
        Assert.Null(duelCfgError);
        var (duelOk, duelDetail) = FairnessAudit.VerifyMatch(
            open.MatchId, "e2e-duel-nonce", open.CommitmentHex, duel, duelCfg);
        Assert.True(duelOk, duelDetail);

        // Portable progression: the duel comes with a signed receipt that
        // verifies against the game key advertised in chain info.
        Assert.NotNull(duel.Receipt);
        var (receiptOk, receiptDetail) = ReceiptVerifier.Verify(duel.Receipt!);
        Assert.True(receiptOk, receiptDetail);
        var infoNow = await anonymous.Chain.InfoAsync();
        Assert.Equal(infoNow.GameSignerKey, duel.Receipt!.GameSignerKeyHex);

        // ── Covenant-mode wagered match: emulator-enforced escrow ──────
        const long covenantWager = 3_000;
        var covenantOpen = await alice.Matches.OpenAsync(
            new OpenMatchRequest(aliceHeroes[0].Id, bobHeroes[0].Id, covenantWager, "covenant"));
        Assert.NotNull(covenantOpen.EscrowAddress);
        Assert.Null(covenantOpen.StakeInvoice);

        // Both players stake into the escrow from their OWN wallets.
        await aliceWallet.SendAsync(covenantOpen.EscrowAddress!, covenantWager);
        // …plus their per-character match fee (a treasury invoice, separate from the escrow).
        await aliceWallet.SendAsync(covenantOpen.MatchFeeInvoice!.PayToAddress, covenantOpen.MatchFeeInvoice.AmountSats);
        var covenantAccept = await bob.Matches.AcceptAsync(covenantOpen.MatchId);
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
            try
            {
                covenantDuel = await alice.Matches.FightAsync(covenantOpen.MatchId, new FightRequest("e2e-covenant-duel"));
                return true;
            }
            catch (ArkadeHeroesApiException)
            {
                return false;
            }
        }, TimeSpan.FromSeconds(60), "escrow funding to be observed and the covenant duel to settle");

        Assert.Equal(covenantWager * 2, covenantDuel!.WinnerPayoutSats);
        var (covCfg, covCfgError) = await alice.Config.ResolveAsync(covenantDuel.ConfigVersion);
        Assert.Null(covCfgError);
        var (covOk, covDetail) = FairnessAudit.VerifyMatch(
            covenantOpen.MatchId, "e2e-covenant-duel", covenantOpen.CommitmentHex, covenantDuel, covCfg);
        Assert.True(covOk, covDetail);

        // The pot arrived at the WINNER'S own wallet, swept from the escrow by
        // the emulator-co-signed covenant transaction.
        var covenantChallengerWon = covenantDuel.Result.WinnerId == aliceHeroes[0].Id;
        var winnerWallet = covenantChallengerWon ? aliceWallet : bobWallet;
        var winnerBefore = covenantChallengerWon ? aliceBeforeSettle : bobBeforeSettle;
        await winnerWallet.WaitForBalanceAsync(winnerBefore + covenantWager * 2, TimeSpan.FromSeconds(45));

        // ── Shop: invoice → Alice pays → claim → the unit is in HER wallet ─
        var itemInvoice = (await alice.Items.BuyAsync("rusty-blade")).Invoice;
        await aliceWallet.SendAsync(itemInvoice.PayToAddress, itemInvoice.AmountSats);

        ClaimItemResponse? claim = null;
        await PollUntilAsync(async () =>
        {
            try
            {
                claim = await alice.Items.ClaimAsync(new ClaimItemRequest(itemInvoice.InvoiceId));
                return true;
            }
            catch (ArkadeHeroesApiException)
            {
                return false;
            }
        }, TimeSpan.FromSeconds(150), "item payment to be observed and the claim to deliver");

        Assert.Equal(1UL, claim!.UnitsHeld);
        await aliceWallet.WaitForAssetAsync(claim.ItemAssetId, TimeSpan.FromSeconds(30));

        var itemDetails = await transport.GetAssetDetailsAsync(claim.ItemAssetId);
        Assert.Equal(1000UL, itemDetails.Supply);

        // Equip the held unit on Alice's remaining hero.
        await alice.Heroes.EquipAsync(aliceHeroes[0].Id, new EquipRequest("rusty-blade"));

        // Unequip frees the slot (server-side bookkeeping; the item asset stays in Alice's wallet).
        await alice.Heroes.UnequipAsync(aliceHeroes[0].Id, new UnequipRequest("Weapon"));
        var afterUnequip = await alice.Heroes.GetAsync(aliceHeroes[0].Id);
        Assert.DoesNotContain("rusty-blade", afterUnequip.Equipment.Values);

        // Friendly fight (no stakes): resolves immediately and carries a verifiable receipt.
        var friendlyOpen = await alice.Matches.OpenAsync(
            new OpenMatchRequest(aliceHeroes[0].Id, bobHeroes[0].Id));
        var friendly = await alice.Matches.FightAsync(friendlyOpen.MatchId, new FightRequest("e2e-friendly"));
        Assert.False(string.IsNullOrEmpty(friendly.Result.WinnerId));
        var (friendlyCfg, friendlyCfgError) = await alice.Config.ResolveAsync(friendly.ConfigVersion);
        Assert.Null(friendlyCfgError);
        var (friendlyOk, friendlyDetail) = FairnessAudit.VerifyMatch(
            friendlyOpen.MatchId, "e2e-friendly", friendlyOpen.CommitmentHex, friendly, friendlyCfg);
        Assert.True(friendlyOk, friendlyDetail);
    }
}
