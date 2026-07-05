using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
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

    private async Task<string> ChallengeAsync(HttpClient c) =>
        (await c.GetFromJsonAsync<LoginChallengeResponse>("/api/players/login-challenge"))!.NonceHex;

    private async Task<(PlayerDto Player, ECPrivKey Key, string PubHex)> RegisterWithKeyAsync(string name)
    {
        var (key, pubHex) = NewKey();
        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/players",
            new RegisterPlayerRequest(name, $"sim-login-{Guid.NewGuid():N}", pubHex));
        resp.EnsureSuccessStatusCode();
        return ((await resp.Content.ReadFromJsonAsync<PlayerDto>())!, key, pubHex);
    }

    [Fact]
    public async Task Login_WithRegisteredKey_ResumesSamePlayer()
    {
        var (player, key, pubHex) = await RegisterWithKeyAsync("WL-Resume");
        var fresh = _factory.CreateClient(); // no token
        var nonce = await ChallengeAsync(fresh);

        var login = await fresh.PostAsJsonAsync("/api/players/login",
            new LoginRequest(pubHex, nonce, Sign(key, nonce)));
        login.EnsureSuccessStatusCode();

        var resumed = (await login.Content.ReadFromJsonAsync<PlayerDto>())!;
        Assert.Equal(player.PlayerId, resumed.PlayerId);
        Assert.Equal(player.Token, resumed.Token);
    }

    [Fact]
    public async Task Login_WithWrongSignature_Refused()
    {
        var (_, _, pubHex) = await RegisterWithKeyAsync("WL-WrongSig");
        var (impostor, _) = NewKey(); // a different key signs
        var client = _factory.CreateClient();
        var nonce = await ChallengeAsync(client);

        // Claim the registered pubkey but sign with the impostor's key.
        var resp = await client.PostAsJsonAsync("/api/players/login",
            new LoginRequest(pubHex, nonce, Sign(impostor, nonce)));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Login_SignatureOverADifferentNonce_Refused()
    {
        var (_, key, pubHex) = await RegisterWithKeyAsync("WL-Bind");
        var client = _factory.CreateClient();
        var nonceA = await ChallengeAsync(client);
        var nonceB = await ChallengeAsync(client);

        // Sign nonce A but present nonce B — the signature isn't over B's digest.
        var resp = await client.PostAsJsonAsync("/api/players/login",
            new LoginRequest(pubHex, nonceB, Sign(key, nonceA)));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Login_WithUnknownNonce_Refused()
    {
        var (_, key, pubHex) = await RegisterWithKeyAsync("WL-Unknown");
        var client = _factory.CreateClient();
        var madeUpNonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

        var resp = await client.PostAsJsonAsync("/api/players/login",
            new LoginRequest(pubHex, madeUpNonce, Sign(key, madeUpNonce)));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
