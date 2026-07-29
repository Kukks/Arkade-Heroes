using System.Collections.Concurrent;
using ArkadeHeroes.Core;
using ArkadeHeroes.Shared;

namespace ArkadeHeroes.Client.Sdk;

/// <summary>
/// Resolves the STAMPED rules version on a replay to the rules themselves, so every verifier replays a
/// match under the config the server actually resolved it with instead of the client's compiled-in
/// <see cref="GameConfig.Default"/>.
///
/// The resolution is TRUSTLESS: whatever the server returns is fed back through
/// <see cref="GameConfigVersion.Compute"/> and rejected unless it reproduces the version that was asked
/// for — so a server cannot answer <c>/api/config/{v}</c> with rules other than the ones it stamped.
///
/// And it is LOUD: an unknown or unfetchable version yields an error, never a quiet substitution of
/// <see cref="GameConfig.Default"/>. Replaying under rules that merely LOOK right is exactly the failure
/// this whole mechanism exists to remove.
/// </summary>
public sealed class ConfigApi(ArkadeHeroesClient client)
{
    private readonly ConcurrentDictionary<string, GameConfig> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The raw endpoint: the rules for a version id, or an ArkadeHeroesApiException on 404.</summary>
    public Task<GameRulesDto> GetAsync(string version) =>
        client.GetAsync<GameRulesDto>($"/api/config/{version}");

    /// <summary>
    /// The config a replay stamped <paramref name="version"/> must be verified under.
    ///
    /// An EMPTY/absent stamp means the outcome predates stamping, so it was resolved under
    /// <see cref="GameConfig.Default"/> — that is a fact about those artifacts, not a fallback, and it is
    /// what keeps every historical replay verifying. A stamp equal to <see cref="GameConfigVersion.Default"/>
    /// short-circuits to the compiled-in constant (no round trip, and it still verifies offline). Anything
    /// else is fetched, re-hashed, and either matched or REFUSED.
    /// </summary>
    public async Task<(GameConfig? Config, string? Error)> ResolveAsync(string? version)
    {
        if (string.IsNullOrEmpty(version)) return (GameConfig.Default, null);
        if (version.Equals(GameConfigVersion.Default, StringComparison.OrdinalIgnoreCase))
            return (GameConfig.Default, null);
        if (_cache.TryGetValue(version, out var cached)) return (cached, null);

        GameRulesDto rules;
        try
        {
            rules = await GetAsync(version);
        }
        catch (Exception ex)
        {
            return (null, $"cannot verify: the rules this was resolved under (config {Short(version)}) " +
                          $"could not be fetched — {ex.Message}");
        }

        var config = rules.ToGameConfig();
        if (config is null)
            return (null, $"cannot verify: the served rules for config {Short(version)} use a move-selection " +
                          $"policy '{rules.SelectionPolicy}' this client does not know");

        // Trustless check: the served rules must HASH to the version that was asked for.
        var recomputed = GameConfigVersion.Compute(config);
        if (!recomputed.Equals(version, StringComparison.OrdinalIgnoreCase))
            return (null, $"cannot verify: the server served rules for config {Short(version)} that hash to " +
                          $"{Short(recomputed)} — they are not the rules it stamped");

        _cache[version] = config;
        return (config, null);
    }

    private static string Short(string version) =>
        version.Length <= 12 ? version : version[..12];
}
