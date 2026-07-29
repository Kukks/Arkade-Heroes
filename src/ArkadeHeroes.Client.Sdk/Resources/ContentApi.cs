using System.Collections.Concurrent;
using ArkadeHeroes.Core.Content;
using ArkadeHeroes.Shared;

namespace ArkadeHeroes.Client.Sdk;

/// <summary>
/// Resolves the STAMPED content version on an outcome to the gear and dungeons themselves, so a verifier
/// rebuilds a hero's loadout from the content the server actually resolved under instead of from its own
/// compiled-in <see cref="ContentPack.Default"/>.
///
/// This is the twin of <see cref="ConfigApi"/> and it exists for the same reason. Item stats are combat
/// inputs: <c>FairnessAudit.RebuildHero</c> turns each equipped item id back into stats before replaying,
/// so a client holding NEWER or OLDER content than the match ran on would replay a different fight and
/// render the literal string "SERVER CHEATED" over an honest result.
///
/// The resolution is TRUSTLESS: whatever the server returns is parsed with the same loader the server used
/// and rejected unless it recomputes to the version that was asked for — so a server cannot answer
/// <c>/api/content/{v}</c> with content other than the content it stamped. Parsing also re-runs the full
/// validator, so a server cannot push a client content that would not have been publishable.
///
/// And it is LOUD: an unknown or unfetchable version yields an error, never a quiet substitution of the
/// local pack. Replaying against content that merely LOOKS right is exactly the failure this removes.
/// </summary>
public sealed class ContentApi(ArkadeHeroesClient client)
{
    private readonly ConcurrentDictionary<string, ContentPack> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The raw endpoint: the authored content for a version id, or an ArkadeHeroesApiException on 404.</summary>
    public Task<ContentPackDto> GetAsync(string version) =>
        client.GetAsync<ContentPackDto>($"/api/content/{version}");

    /// <summary>
    /// The content an outcome stamped <paramref name="version"/> must be verified against.
    ///
    /// An EMPTY/absent stamp means the outcome predates content stamping, so it was resolved on the gear
    /// the binary compiled in — that is a fact about those artifacts, not a fallback, and it is what keeps
    /// every historical replay verifying. A stamp equal to <see cref="ContentPackVersion.Default"/>
    /// short-circuits to the local pack (no round trip, and it still verifies offline). Anything else is
    /// fetched, re-parsed, re-hashed, and either matched or REFUSED.
    /// </summary>
    public async Task<(ContentPack? Pack, string? Error)> ResolveAsync(string? version)
    {
        if (string.IsNullOrEmpty(version)) return (ContentPack.Default, null);
        if (version.Equals(ContentPackVersion.Default, StringComparison.OrdinalIgnoreCase))
            return (ContentPack.Default, null);
        if (_cache.TryGetValue(version, out var cached)) return (cached, null);

        ContentPackDto served;
        try
        {
            served = await GetAsync(version);
        }
        catch (Exception ex)
        {
            return (null, $"cannot verify: the content this was resolved under (content {Short(version)}) " +
                          $"could not be fetched — {ex.Message}");
        }

        ContentPack pack;
        try
        {
            pack = ContentPackLoader.Parse(served.ItemsJson, served.DungeonsJson);
        }
        catch (ContentValidationException ex)
        {
            return (null, $"cannot verify: the served content for {Short(version)} is not publishable — " +
                          $"{string.Join("; ", ex.Errors.Select(e => e.Code))}");
        }

        // Trustless check: the served content must HASH to the version that was asked for.
        var recomputed = ContentPackVersion.Compute(pack);
        if (!recomputed.Equals(version, StringComparison.OrdinalIgnoreCase))
            return (null, $"cannot verify: the server served content for {Short(version)} that hashes to " +
                          $"{Short(recomputed)} — it is not the content it stamped");

        _cache[version] = pack;
        return (pack, null);
    }

    private static string Short(string version) =>
        version.Length <= 12 ? version : version[..12];
}
