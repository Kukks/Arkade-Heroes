using NArk.Abstractions.Batches;
using NArk.Abstractions.Batches.ServerEvents;
using NArk.Abstractions.Intents;
using NArk.Abstractions.VTXOs;
using NArk.Core;
using NArk.Core.Transport;
using NArk.Core.Transport.Models;
using NBitcoin;

namespace ArkadeHeroes.Web.Wallet;

/// <summary>
/// Keeps non-P2TR scripts out of every arkd indexer call the wallet makes.
///
/// <para>arkd's indexer only knows about P2TR scripts and rejects the whole query — HTTP 400,
/// <c>invalid script, must be P2TR</c> — if a single one in the batch is something else. The wallet
/// sends one on every asset spend without meaning to: the SDK's post-spend poll takes its script set
/// straight off the transaction's outputs, and a transaction that moves a hero carries an extra
/// OP_RETURN output holding the asset packet. One unindexable script poisons the request, so the poll
/// that is supposed to write the spend's results into local storage fails outright — after EVERY spend
/// that moves an asset, which in this game is most of them.</para>
///
/// <para>Filtering here rather than at the one call site is deliberate: the constraint belongs to arkd,
/// so it is enforced at the boundary where every caller — the post-spend poll, the routine sync, the
/// subscription stream — passes through, instead of being re-remembered by each of them. Everything
/// that is not script-addressed is handed straight to the inner transport.</para>
/// </summary>
public sealed class P2trScriptFilteringTransport(IClientTransport inner) : IClientTransport
{
    /// <summary>
    /// A P2TR scriptPubKey is exactly <c>OP_1</c> + a 32-byte push: 34 bytes, so 68 hex characters
    /// beginning <c>5120</c>. Parsed rather than pattern-matched so a malformed script is simply
    /// dropped instead of being passed on to be rejected.
    /// </summary>
    internal static bool IsP2tr(string scriptHex)
    {
        try { return Script.FromHex(scriptHex).IsScriptType(ScriptType.Taproot); }
        catch { return false; }
    }

    /// <summary>The P2TR members of the set — the only ones arkd's indexer can answer for.</summary>
    internal static IReadOnlySet<string> OnlyP2tr(IReadOnlySet<string> scripts) =>
        scripts.All(IsP2tr) ? scripts : scripts.Where(IsP2tr).ToHashSet();

    private static IReadOnlySet<string>? OnlyP2trOrNull(IReadOnlySet<string>? scripts) =>
        scripts is null ? null : OnlyP2tr(scripts);

    // ── The script-addressed calls: filtered ────────────────────────────────────────────────────
    public IAsyncEnumerable<ArkVtxo> GetVtxoByScriptsAsSnapshot(IReadOnlySet<string> scripts,
        CancellationToken cancellationToken = default)
        => Empty(scripts, out var kept)
            ? AsyncEnumerable.Empty<ArkVtxo>()
            : inner.GetVtxoByScriptsAsSnapshot(kept, cancellationToken);

    public IAsyncEnumerable<ArkVtxo> GetVtxoByScriptsAsSnapshot(IReadOnlySet<string> scripts,
        DateTimeOffset? after, DateTimeOffset? before, CancellationToken cancellationToken = default)
        => Empty(scripts, out var kept)
            ? AsyncEnumerable.Empty<ArkVtxo>()
            : inner.GetVtxoByScriptsAsSnapshot(kept, after, before, cancellationToken);

    public IAsyncEnumerable<VtxoSubscriptionEvent> OpenSubscriptionStreamAsync(IReadOnlySet<string>? initialScripts,
        string? existingSubscriptionId, CancellationToken cancellationToken = default)
        => inner.OpenSubscriptionStreamAsync(OnlyP2trOrNull(initialScripts), existingSubscriptionId, cancellationToken);

    public Task UpdateSubscriptionScriptsAsync(string subscriptionId, IReadOnlySet<string>? add,
        IReadOnlySet<string>? remove, CancellationToken cancellationToken = default)
        => inner.UpdateSubscriptionScriptsAsync(subscriptionId, OnlyP2trOrNull(add), OnlyP2trOrNull(remove),
            cancellationToken);

    /// <summary>
    /// True when nothing indexable is left, so the call is answered locally. arkd treats an empty
    /// script list as "every script", which would turn a poll for one OP_RETURN into a full scan.
    /// </summary>
    private static bool Empty(IReadOnlySet<string> scripts, out IReadOnlySet<string> kept)
    {
        kept = OnlyP2tr(scripts);
        return kept.Count == 0;
    }

    // ── Everything else: straight through ───────────────────────────────────────────────────────
    public Task<ArkServerInfo> GetServerInfoAsync(CancellationToken cancellationToken = default)
        => inner.GetServerInfoAsync(cancellationToken);

    public IAsyncEnumerable<ArkVtxo> GetVtxosByOutpoints(IReadOnlyCollection<OutPoint> outpoints,
        bool spentOnly = false, CancellationToken cancellationToken = default)
        => inner.GetVtxosByOutpoints(outpoints, spentOnly, cancellationToken);

    public Task<string> RegisterIntent(ArkIntent intent, CancellationToken cancellationToken = default)
        => inner.RegisterIntent(intent, cancellationToken);

    public Task DeleteIntent(ArkIntent intent, CancellationToken cancellationToken = default)
        => inner.DeleteIntent(intent, cancellationToken);

    public Task<SubmitTxResponse> SubmitTx(string signedArkTx, string[] checkpointTxs,
        CancellationToken cancellationToken = default)
        => inner.SubmitTx(signedArkTx, checkpointTxs, cancellationToken);

    public Task FinalizeTx(string arkTxId, string[] finalCheckpointTxs, CancellationToken cancellationToken)
        => inner.FinalizeTx(arkTxId, finalCheckpointTxs, cancellationToken);

    public Task SubmitTreeNoncesAsync(SubmitTreeNoncesRequest treeNonces, CancellationToken cancellationToken)
        => inner.SubmitTreeNoncesAsync(treeNonces, cancellationToken);

    public Task SubmitTreeSignaturesRequest(SubmitTreeSignaturesRequest treeSigs, CancellationToken cancellationToken)
        => inner.SubmitTreeSignaturesRequest(treeSigs, cancellationToken);

    public Task SubmitSignedForfeitTxsAsync(SubmitSignedForfeitTxsRequest req, CancellationToken cancellationToken)
        => inner.SubmitSignedForfeitTxsAsync(req, cancellationToken);

    public Task ConfirmRegistrationAsync(string intentId, CancellationToken cancellationToken)
        => inner.ConfirmRegistrationAsync(intentId, cancellationToken);

    public IAsyncEnumerable<BatchEvent> GetEventStreamAsync(GetEventStreamRequest req,
        CancellationToken cancellationToken)
        => inner.GetEventStreamAsync(req, cancellationToken);

    public Task<ArkAssetDetails> GetAssetDetailsAsync(string assetId, CancellationToken cancellationToken = default)
        => inner.GetAssetDetailsAsync(assetId, cancellationToken);

    public Task UpdateStreamTopicsAsync(string streamId, string[]? addTopics, string[]? removeTopics,
        CancellationToken cancellationToken = default)
        => inner.UpdateStreamTopicsAsync(streamId, addTopics, removeTopics, cancellationToken);

    public Task<ArkIntent[]> GetIntentsByProofAsync(string proof, string message,
        CancellationToken cancellationToken = default)
        => inner.GetIntentsByProofAsync(proof, message, cancellationToken);

    public Task<PendingArkTransaction[]> GetPendingTxAsync(string proof, string message,
        CancellationToken cancellationToken = default)
        => inner.GetPendingTxAsync(proof, message, cancellationToken);

    public Task<IReadOnlyList<VtxoChainEntry>> GetVtxoChainAsync(OutPoint vtxoOutpoint,
        string? intentProof = null, string? intentMessage = null, CancellationToken cancellationToken = default)
        => inner.GetVtxoChainAsync(vtxoOutpoint, intentProof, intentMessage, cancellationToken);

    public Task<IReadOnlyList<string>> GetVirtualTxsAsync(IReadOnlyList<string> txids,
        CancellationToken cancellationToken = default)
        => inner.GetVirtualTxsAsync(txids, cancellationToken);

    public Task<IReadOnlyList<VtxoTreeNode>> GetVtxoTreeAsync(OutPoint batchOutpoint,
        CancellationToken cancellationToken = default)
        => inner.GetVtxoTreeAsync(batchOutpoint, cancellationToken);
}
