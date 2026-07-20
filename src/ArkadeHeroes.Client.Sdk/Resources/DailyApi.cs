using ArkadeHeroes.Shared;

namespace ArkadeHeroes.Client.Sdk;

/// <summary>The daily engagement loop: today's quests + streak, and the once-per-day sats claim.</summary>
public sealed class DailyApi(ArkadeHeroesClient client)
{
    public Task<DailyStatusDto> StatusAsync() => client.GetAsync<DailyStatusDto>("/api/daily");
    public Task<DailyClaimResultDto> ClaimAsync() => client.PostAsync<DailyClaimResultDto>("/api/daily/claim");
}
