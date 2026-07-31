using ArkadeHeroes.Web.Wallet;
using NArk.Abstractions.VTXOs;
using NArk.Core.Transport;
using NBitcoin;
using NSubstitute;

namespace ArkadeHeroes.Tests.Web;

/// <summary>
/// The guard that keeps the wallet's indexer queries answerable.
///
/// <para>arkd rejects a whole <c>GET /v1/indexer/vtxos</c> — HTTP 400, <c>invalid script, must be
/// P2TR</c> — if a single script in the batch is not P2TR, and the SDK's post-spend poll takes its
/// scripts straight off the transaction's outputs. Anything that moves a hero carries an extra
/// OP_RETURN output holding the asset packet, so one unindexable script silently killed the poll that
/// writes a spend's results back into local storage.</para>
/// </summary>
public class P2trScriptFilteringTransportTests
{
    // OP_1 + a 32-byte push — what every Arkade contract address is.
    private const string P2tr = "512079be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798";
    private const string P2trOther = "5120c6047f9441ed7d6d3045406e95c07cd85c778e4b8cef3ca7abac09b95c709ee5";
    // The asset packet the SDK appends whenever a spend moves an asset: OP_RETURN + a push.
    private const string AssetPacket = "6a4cbb41524b0001";
    // The P2A anchor the SDK already knew to drop by hand.
    private const string Anchor = "51024e73";

    [Fact]
    public async Task TheAssetPacketsOpReturnNeverReachesTheIndexer()
    {
        var inner = Inner();
        var transport = new P2trScriptFilteringTransport(inner);

        await Drain(transport.GetVtxoByScriptsAsSnapshot(Set(P2tr, AssetPacket)));

        inner.Received(1).GetVtxoByScriptsAsSnapshot(
            Arg.Is<IReadOnlySet<string>>(s => s.Count == 1 && s.Contains(P2tr)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheTimeFilteredOverloadIsGuardedToo_ItIsTheOneThePostSpendPollUses()
    {
        var inner = Inner();
        var transport = new P2trScriptFilteringTransport(inner);
        var after = DateTimeOffset.UtcNow.AddMinutes(-5);

        await Drain(transport.GetVtxoByScriptsAsSnapshot(Set(P2tr, AssetPacket, Anchor), after, null));

        inner.Received(1).GetVtxoByScriptsAsSnapshot(
            Arg.Is<IReadOnlySet<string>>(s => s.Count == 1 && s.Contains(P2tr)),
            after, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnOrdinaryPollIsPassedThroughUntouched()
    {
        var inner = Inner();
        var transport = new P2trScriptFilteringTransport(inner);
        var scripts = Set(P2tr, P2trOther);

        await Drain(transport.GetVtxoByScriptsAsSnapshot(scripts));

        inner.Received(1).GetVtxoByScriptsAsSnapshot(scripts, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NothingIndexableLeft_IsAnsweredWithoutCallingArkd()
    {
        // Not merely an optimisation: arkd reads an empty script list as "every script", so forwarding
        // it would turn a poll for one OP_RETURN into a full scan of the indexer.
        var inner = Inner();
        var transport = new P2trScriptFilteringTransport(inner);

        var found = await Drain(transport.GetVtxoByScriptsAsSnapshot(Set(AssetPacket)));

        Assert.Empty(found);
        inner.DidNotReceiveWithAnyArgs().GetVtxoByScriptsAsSnapshot(default!, default);
    }

    [Fact]
    public async Task TheSubscriptionIsFilteredAsWell_ItTakesScriptsFromTheSameStorage()
    {
        var inner = Inner();
        var transport = new P2trScriptFilteringTransport(inner);

        await transport.UpdateSubscriptionScriptsAsync("sub-1", Set(P2tr, AssetPacket), Set(P2trOther, Anchor));

        await inner.Received(1).UpdateSubscriptionScriptsAsync("sub-1",
            Arg.Is<IReadOnlySet<string>?>(s => s!.Count == 1 && s.Contains(P2tr)),
            Arg.Is<IReadOnlySet<string>?>(s => s!.Count == 1 && s.Contains(P2trOther)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void OnlyRealP2trPasses()
    {
        Assert.True(P2trScriptFilteringTransport.IsP2tr(P2tr));
        Assert.False(P2trScriptFilteringTransport.IsP2tr(AssetPacket));
        Assert.False(P2trScriptFilteringTransport.IsP2tr(Anchor));
        Assert.False(P2trScriptFilteringTransport.IsP2tr(""));
        Assert.False(P2trScriptFilteringTransport.IsP2tr("not hex at all"));
        // A P2WPKH output — well-formed, indexable by an Esplora, and still not something arkd answers for.
        Assert.False(P2trScriptFilteringTransport.IsP2tr("0014751e76e8199196d454941c45d1b3a323f1433bd6"));
    }

    [Fact]
    public async Task CallsThatCarryNoScriptsAreLeftAlone()
    {
        var inner = Inner();
        var transport = new P2trScriptFilteringTransport(inner);
        var outpoints = new[] { new OutPoint(uint256.One, 0) };

        await Drain(transport.GetVtxosByOutpoints(outpoints, spentOnly: true));

        inner.Received(1).GetVtxosByOutpoints(outpoints, true, Arg.Any<CancellationToken>());
    }

    private static IReadOnlySet<string> Set(params string[] scripts) => new HashSet<string>(scripts);

    private static IClientTransport Inner()
    {
        var inner = Substitute.For<IClientTransport>();
        inner.GetVtxoByScriptsAsSnapshot(Arg.Any<IReadOnlySet<string>>(), Arg.Any<CancellationToken>())
            .Returns(AsyncEnumerable.Empty<ArkVtxo>());
        inner.GetVtxoByScriptsAsSnapshot(Arg.Any<IReadOnlySet<string>>(), Arg.Any<DateTimeOffset?>(),
            Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>()).Returns(AsyncEnumerable.Empty<ArkVtxo>());
        inner.GetVtxosByOutpoints(Arg.Any<IReadOnlyCollection<OutPoint>>(), Arg.Any<bool>(),
            Arg.Any<CancellationToken>()).Returns(AsyncEnumerable.Empty<ArkVtxo>());
        return inner;
    }

    private static async Task<List<ArkVtxo>> Drain(IAsyncEnumerable<ArkVtxo> source)
    {
        var found = new List<ArkVtxo>();
        await foreach (var vtxo in source) found.Add(vtxo);
        return found;
    }
}
