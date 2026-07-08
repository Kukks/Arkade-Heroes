using ArkadeHeroes.Chain.NArk;
using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ArkadeHeroes.Tests.E2E;

/// <summary>
/// "Sign in with your wallet" against the real stack, closing the recovery loop:
/// a player registers with their wallet's login key; later, a wallet RESTORED
/// from the same mnemonic signs the server's challenge and resumes the SAME
/// player — non-custodial auth, no password, the server never holds a key. A
/// replayed (already-used) challenge is refused.
/// </summary>
public class WalletLoginE2ETests : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private string _serverDbPath = null!;
    private readonly List<string> _walletDbPaths = [];
    private readonly List<SelfCustodyWallet> _wallets = [];

    public async Task InitializeAsync()
    {
        await RegtestHelper.WaitForArkdReadyAsync(TimeSpan.FromSeconds(30));
        _serverDbPath = Path.Combine(Path.GetTempPath(), $"ah-login-e2e-{Guid.NewGuid():N}.db");
        Environment.SetEnvironmentVariable("Chain__Mode", "NArk");
        Environment.SetEnvironmentVariable("Chain__NArk__ArkUri", "http://localhost:7070");
        Environment.SetEnvironmentVariable("Chain__NArk__DbPath", _serverDbPath);
        _factory = new WebApplicationFactory<Program>();
    }

    public async Task DisposeAsync()
    {
        foreach (var w in _wallets) await w.DisposeAsync();
        _factory.Dispose();
        foreach (var p in _walletDbPaths.Append(_serverDbPath))
            try { if (File.Exists(p)) File.Delete(p); } catch { /* windows lock */ }
    }

    private async Task<SelfCustodyWallet> WalletAsync(string? mnemonic = null)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"ah-login-wallet-{Guid.NewGuid():N}.db");
        _walletDbPaths.Add(dbPath);
        var w = await SelfCustodyWallet.CreateAsync(new SelfCustodyWalletOptions
        {
            ArkUri = "http://localhost:7070", DbPath = dbPath, Mnemonic = mnemonic,
        });
        _wallets.Add(w);
        return w;
    }

    [Fact]
    public async Task RestoredWallet_SignsIn_ResumesTheSamePlayer_ReplayRefused()
    {
        // Register a player with their wallet's login key (proving possession by
        // signing a fresh challenge).
        var wallet = await WalletAsync();
        var reg = new ArkadeHeroesClient(_factory.CreateClient());
        var regChallenge = await reg.Players.LoginChallengeAsync();
        var (_, regSig) = wallet.SignLoginDigest(LoginChallenge.Digest(regChallenge.NonceHex));
        var player = await reg.Players.RegisterAsync(
            new RegisterPlayerRequest("Login-Alice", wallet.Address, wallet.LoginPubKeyHex, regChallenge.NonceHex, regSig));

        // The machine is lost; restore the wallet from the mnemonic — same login key.
        var restored = await WalletAsync(wallet.Mnemonic);
        Assert.Equal(wallet.LoginPubKeyHex, restored.LoginPubKeyHex);

        // Sign in with the restored wallet from a FRESH (token-less) client.
        var fresh = new ArkadeHeroesClient(_factory.CreateClient());
        var challenge = await fresh.Players.LoginChallengeAsync();
        var (pubKey, signature) = restored.SignLoginDigest(LoginChallenge.Digest(challenge.NonceHex));
        var resumed = await fresh.Players.LoginAsync(
            new LoginRequest(pubKey, challenge.NonceHex, signature));

        // Same player, same session — resumed with the wallet alone.
        Assert.Equal(player.PlayerId, resumed.PlayerId);
        Assert.Equal(player.Token, resumed.Token);

        // Replay: the same nonce is single-use, so re-presenting it is refused.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => fresh.Players.LoginAsync(
            new LoginRequest(pubKey, challenge.NonceHex, signature)));
    }

    [Fact]
    public async Task Login_WithUnregisteredKey_IsRefused()
    {
        // A wallet whose login key was never registered can't sign in.
        var stranger = await WalletAsync();
        var client = new ArkadeHeroesClient(_factory.CreateClient());
        var challenge = await client.Players.LoginChallengeAsync();
        var (pubKey, signature) = stranger.SignLoginDigest(LoginChallenge.Digest(challenge.NonceHex));

        await Assert.ThrowsAsync<ArkadeHeroesApiException>(() => client.Players.LoginAsync(
            new LoginRequest(pubKey, challenge.NonceHex, signature)));
    }
}
