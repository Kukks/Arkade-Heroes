using System.Security.Cryptography;
using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Mvc.Testing;
using NBitcoin.Secp256k1;

namespace ArkadeHeroes.Tests;

/// <summary>
/// The security properties of "sign in with your wallet", exercised with raw
/// keypairs against the server: a registered key resumes its player; a signature
/// that doesn't match the key, isn't over the presented nonce, or uses an
/// unknown/already-used nonce is refused. These are the checks that stop a public
/// address (or a replayed challenge) from being used to take over an account.
/// </summary>
public class WalletLoginTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public WalletLoginTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private static (ECPrivKey Key, string PubHex) NewKey()
    {
        var key = ECPrivKey.Create(RandomNumberGenerator.GetBytes(32));
        Span<byte> pub = stackalloc byte[32];
        key.CreateXOnlyPubKey().WriteToSpan(pub);
        return (key, Convert.ToHexString(pub).ToLowerInvariant());
    }

    private static string Sign(ECPrivKey key, string nonceHex)
    {
        var sig = key.SignBIP340(LoginChallenge.Digest(nonceHex));
        var bytes = new byte[64];
        sig.WriteToSpan(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private async Task<string> ChallengeAsync(ArkadeHeroesClient c) =>
        (await c.Players.LoginChallengeAsync()).NonceHex;

    private async Task<(PlayerDto Player, ECPrivKey Key, string PubHex)> RegisterWithKeyAsync(string name)
    {
        var (key, pubHex) = NewKey();
        var client = new ArkadeHeroesClient(_factory.CreateClient());
        var nonce = await ChallengeAsync(client); // proof-of-possession
        var player = await client.Players.RegisterAsync(
            new RegisterPlayerRequest(name, $"sim-login-{Guid.NewGuid():N}", pubHex, nonce, Sign(key, nonce)));
        return (player, key, pubHex);
    }

    [Fact]
    public async Task Login_WithRegisteredKey_ResumesSamePlayer()
    {
        var (player, key, pubHex) = await RegisterWithKeyAsync("WL-Resume");
        var fresh = new ArkadeHeroesClient(_factory.CreateClient()); // no token
        var nonce = await ChallengeAsync(fresh);

        var resumed = await fresh.Players.LoginAsync(new LoginRequest(pubHex, nonce, Sign(key, nonce)));
        Assert.Equal(player.PlayerId, resumed.PlayerId);
        Assert.Equal(player.Token, resumed.Token);
    }

    [Fact]
    public async Task Login_WithWrongSignature_Refused()
    {
        var (_, _, pubHex) = await RegisterWithKeyAsync("WL-WrongSig");
        var (impostor, _) = NewKey(); // a different key signs
        var client = new ArkadeHeroesClient(_factory.CreateClient());
        var nonce = await ChallengeAsync(client);

        // Claim the registered pubkey but sign with the impostor's key.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => client.Players.LoginAsync(new LoginRequest(pubHex, nonce, Sign(impostor, nonce))));
    }

    [Fact]
    public async Task Login_SignatureOverADifferentNonce_Refused()
    {
        var (_, key, pubHex) = await RegisterWithKeyAsync("WL-Bind");
        var client = new ArkadeHeroesClient(_factory.CreateClient());
        var nonceA = await ChallengeAsync(client);
        var nonceB = await ChallengeAsync(client);

        // Sign nonce A but present nonce B — the signature isn't over B's digest.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => client.Players.LoginAsync(new LoginRequest(pubHex, nonceB, Sign(key, nonceA))));
    }

    [Fact]
    public async Task Login_WithUnknownNonce_Refused()
    {
        var (_, key, pubHex) = await RegisterWithKeyAsync("WL-Unknown");
        var client = new ArkadeHeroesClient(_factory.CreateClient());
        var madeUpNonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => client.Players.LoginAsync(new LoginRequest(pubHex, madeUpNonce, Sign(key, madeUpNonce))));
    }

    // ── Registration hardening (the flagged account-confusion fix) ──────

    [Fact]
    public async Task Register_LoginKeyWithoutProofOfPossession_Refused()
    {
        var (_, pubHex) = NewKey();
        var client = new ArkadeHeroesClient(_factory.CreateClient());
        // Claims a login key but supplies no signed challenge.
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => client.Players.RegisterAsync(new RegisterPlayerRequest("WL-NoPoP", $"sim-login-{Guid.NewGuid():N}", pubHex)));
    }

    [Fact]
    public async Task Register_ClaimingAKeyYouDoNotControl_Refused()
    {
        // THE core exploit the fix closes: an attacker binds a VICTIM's login
        // pubkey to their own player. They can't — proof-of-possession fails
        // because they can't sign for a key they don't hold.
        var (_, victimPub) = NewKey();
        var (attackerKey, _) = NewKey();
        var client = new ArkadeHeroesClient(_factory.CreateClient());
        var nonce = await ChallengeAsync(client);

        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => client.Players.RegisterAsync(new RegisterPlayerRequest("WL-Impostor", $"sim-login-{Guid.NewGuid():N}",
                victimPub, nonce, Sign(attackerKey, nonce))));
    }

    [Fact]
    public async Task Register_DuplicateLoginKey_Refused()
    {
        var (_, key, pubHex) = await RegisterWithKeyAsync("WL-Uniq1");
        // A second registration with the SAME (proven) login key, different address.
        var client = new ArkadeHeroesClient(_factory.CreateClient());
        var nonce = await ChallengeAsync(client);
        await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => client.Players.RegisterAsync(new RegisterPlayerRequest("WL-Uniq2", $"sim-login-{Guid.NewGuid():N}", pubHex, nonce, Sign(key, nonce))));
    }
}
