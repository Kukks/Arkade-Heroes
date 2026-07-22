using System.Text.Json;
using ArkadeHeroes.Client.Sdk;
using ArkadeHeroes.Shared;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ArkadeHeroes.Tests;

/// <summary>
/// The public trophy case — a name on the leaderboard now leads somewhere. These pin the two
/// properties that make it safe to publish: it shows the SAME standing a player sees of themselves
/// (no second rulebook to drift), and it carries nothing but bragging material.
/// </summary>
public class PlayerProfileTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public PlayerProfileTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task AnyoneCanReadAnotherPlayersProfile_WithoutSigningIn()
    {
        var (alice, alicePlayer) = await _factory.RegisterAsync("Alice");
        var heroes = await alice.ClaimStartersAsync();

        // A brand-new client holding no token at all: the profile is public by design.
        var stranger = new ArkadeHeroesClient(_factory.CreateClient());
        var profile = await stranger.Players.ProfileAsync(alicePlayer.PlayerId);

        Assert.Equal(alicePlayer.PlayerId, profile.PlayerId);
        Assert.Equal("Alice", profile.Name);
        Assert.Equal(heroes.Count, profile.Achievements.HeroesOwned);
        Assert.NotEmpty(profile.Notable);
        // Only ever this player's own heroes — a profile must not leak the global roster.
        Assert.All(profile.Notable, h => Assert.Contains(h.Id, heroes.Select(x => x.Id)));
    }

    [Fact]
    public async Task UnknownPlayer_IsNotFound_RatherThanAHollowProfile()
    {
        var stranger = new ArkadeHeroesClient(_factory.CreateClient());

        var ex = await Assert.ThrowsAsync<ArkadeHeroesApiException>(
            () => stranger.Players.ProfileAsync("no-such-player"));
        Assert.Contains("404", ex.Message);
    }

    // The public view is a projection of the player's own views, not a parallel computation.
    // If these ever disagree, one of the two is lying about the same player.
    [Fact]
    public async Task PublicProfileAgreesWithThePlayersOwnPrivateViews()
    {
        var (alice, alicePlayer) = await _factory.RegisterAsync("Alice");
        await alice.ClaimStartersAsync();

        var mine = await alice.Players.AchievementsAsync();
        var myPass = await alice.Players.SeasonPassAsync();
        var profile = await alice.Players.ProfileAsync(alicePlayer.PlayerId);

        Assert.Equal(mine.HeroesOwned, profile.Achievements.HeroesOwned);
        Assert.Equal(mine.Badges, profile.Achievements.Badges);
        Assert.Equal(mine.FancySetsOwned, profile.Achievements.FancySetsOwned);
        Assert.Equal(myPass.Tier, profile.SeasonPass.Tier);
        Assert.Equal(myPass.Points, profile.SeasonPass.Points);
        Assert.Equal(myPass.Title, profile.SeasonPass.Title);
    }

    // The whole point of a PUBLIC endpoint is that everyone can read it, so anything
    // wallet- or session-shaped must never reach the wire. Asserted on the serialized
    // payload rather than field-by-field, so a future added field is caught too.
    [Fact]
    public async Task ProfileCarriesNoWalletAddressOrSessionToken()
    {
        var (alice, alicePlayer) = await _factory.RegisterAsync("Alice");
        await alice.ClaimStartersAsync();

        var stranger = new ArkadeHeroesClient(_factory.CreateClient());
        var json = JsonSerializer.Serialize(await stranger.Players.ProfileAsync(alicePlayer.PlayerId));

        Assert.DoesNotContain(alicePlayer.ArkadeAddress, json);
        Assert.DoesNotContain("sim-wallet-", json);
        if (alicePlayer.Token is { Length: > 0 } token) Assert.DoesNotContain(token, json);
    }

    [Fact]
    public async Task NotableHeroes_AreRarestFirst_CappedAtThree_AndStableAcrossCalls()
    {
        var (alice, alicePlayer) = await _factory.RegisterAsync("Alice");
        await alice.ClaimStartersAsync();

        // Push the roster past the display cap so the ordering actually has to choose. Gifts
        // rather than breeding: the parents would hit the breeding cooldown on a second pairing.
        var (bob, _) = await _factory.RegisterAsync("Bob");
        foreach (var gift in await bob.ClaimStartersAsync())
        {
            await bob.TransferAssetAsync(gift.AssetId!, alicePlayer.PlayerId);
            await bob.Heroes.TransferAsync(gift.Id, new TransferRequest(alicePlayer.PlayerId));
        }

        var profile = await alice.Players.ProfileAsync(alicePlayer.PlayerId);
        Assert.Equal(4, profile.Achievements.HeroesOwned);
        Assert.Equal(3, profile.Notable.Count);

        var scores = profile.Notable
            .Select(h => Core.Progression.Rarity.Of(Core.Genetics.Genome.FromHex(h.GenomeHex)).Score)
            .ToList();
        Assert.Equal(scores.OrderByDescending(s => s), scores);

        // Deterministic: same roster, same order — the tiebreak is not dictionary luck.
        var again = await alice.Players.ProfileAsync(alicePlayer.PlayerId);
        Assert.Equal(profile.Notable.Select(h => h.Id), again.Notable.Select(h => h.Id));
    }
}
