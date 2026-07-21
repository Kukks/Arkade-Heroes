using ArkadeHeroes.Shared;

namespace ArkadeHeroes.Client.Sdk;

/// <summary>Endless solo Trials (cold-start leaderboard): open (commit, FREE — no fee) → run the endless
/// ghost ladder. Awards only a score + flavor title, so it's replayable as often as you like.</summary>
public sealed class TrialsApi(ArkadeHeroesClient client)
{
    public Task<TrialsOpenResponse> OpenAsync(string heroId) =>
        client.PostAsync<TrialsOpenResponse>("/api/trials/open", new TrialsOpenRequest(heroId));

    public Task<TrialsRunResponse> RunAsync(string trialsId, string nonce) =>
        client.PostAsync<TrialsRunResponse>($"/api/trials/{trialsId}/run", new TrialsRunRequest(nonce));

    /// <summary>The public ladder — every hero's BEST run, recomputed from the signed trials receipts.</summary>
    public Task<List<TrialsBoardEntryDto>> BoardAsync() =>
        client.GetAsync<List<TrialsBoardEntryDto>>("/api/trials/board");
}
