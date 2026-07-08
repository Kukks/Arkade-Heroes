using ArkadeHeroes.Shared;

namespace ArkadeHeroes.Client.Sdk;

/// <summary>Chain/runtime info: mode, network, treasury address, species asset, emulator/esplora URIs.</summary>
public sealed class ChainApi(ArkadeHeroesClient client)
{
    public Task<ChainInfoDto> InfoAsync() => client.GetAsync<ChainInfoDto>("/api/chain/info");
}
