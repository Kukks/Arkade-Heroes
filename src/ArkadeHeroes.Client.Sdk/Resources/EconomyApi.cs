using ArkadeHeroes.Shared;

namespace ArkadeHeroes.Client.Sdk;

/// <summary>Treasury-health telemetry (economy control plane) — public, read-only: balance, outflow by category, season accrual.</summary>
public sealed class EconomyApi(ArkadeHeroesClient client)
{
    public Task<EconomyHealthDto> HealthAsync() => client.GetAsync<EconomyHealthDto>("/api/economy/health");
}
