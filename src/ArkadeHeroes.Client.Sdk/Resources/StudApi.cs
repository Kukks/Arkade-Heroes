using ArkadeHeroes.Shared;

namespace ArkadeHeroes.Client.Sdk;

/// <summary>Stud service — breeding with ANOTHER player's hero: propose → the stud's owner consents (which
/// is what bills the proposer) → pay → reveal. Without <see cref="AcceptAsync"/> nothing is billed and
/// nothing mints.</summary>
public sealed class StudApi(ArkadeHeroesClient client)
{
    /// <summary>Every stud proposal this arena has seen (newest first, capped) — the discovery path a
    /// browser needs to spot an incoming request for one of its own heroes.</summary>
    public Task<List<StudProposalDto>> ListAsync() => client.GetAsync<List<StudProposalDto>>("/api/stud");
    public Task<StudProposeResponse> ProposeAsync(StudProposeRequest req) => client.PostAsync<StudProposeResponse>("/api/stud/propose", req);
    /// <summary>The stud owner's consent — returns the invoices the PROPOSER must pay before revealing.</summary>
    public Task<StudAcceptResponse> AcceptAsync(string proposalId) => client.PostAsync<StudAcceptResponse>($"/api/stud/{proposalId}/accept");
    /// <summary>Re-reads what an accepted proposal bills — how the PROPOSER learns their invoices, since
    /// the accept response goes to the stud's owner.</summary>
    public Task<StudAcceptResponse> InvoicesAsync(string proposalId) => client.GetAsync<StudAcceptResponse>($"/api/stud/{proposalId}/invoices");
    public Task<StudProposalDto> DeclineAsync(string proposalId) => client.PostAsync<StudProposalDto>($"/api/stud/{proposalId}/decline");
    public Task<StudRevealResponse> RevealAsync(string proposalId, StudRevealRequest req) => client.PostAsync<StudRevealResponse>($"/api/stud/{proposalId}/reveal", req);
}
