using ArkadeHeroes.Chain.NArk;
using NArk.Abstractions;
using NArk.Abstractions.Assets;
using NArk.Abstractions.VTXOs;
using NArk.Core.Assets;
using NBitcoin;

namespace ArkadeHeroes.Chain.Covenants;

/// <summary>
/// The CLIENT side of death-match reclaim. Two paths, chosen from the on-chain
/// funded state (never the server's word):
///  • BOTH heroes staked, abandoned → the trustless timelocked <c>refund</c> leaf
///    routes each side home (challenger → output 0, defender → output 1); gated on
///    the chain clock, submitted once. No oracle, no server.
///  • ONLY my hero staked (the opponent never showed) → my oracle-gated
///    <c>abort{Side}</c> leaf routes my hero + my gear home to output 0, immediately.
///    <paramref name="requestAbortSig"/> fetches my side's oracle signature (the
///    server signs only for a not-fully-funded escrow — see the abort endpoint).
/// The flow only ever aborts over carriers whose assets are MINE, so it never
/// constructs a spend that touches a counterparty carrier.
/// </summary>
public static class DeathMatchRefundFlow
{
    public static async Task<EmulatorSubmitResponse> ReclaimAsync(
        SelfCustodyWallet wallet,
        Uri emulatorUri,
        DeathMatchJointEscrowParams parameters,
        Func<CancellationToken, Task<long>> chainMedianTime,
        Func<CancellationToken, Task<byte[]>> requestAbortSig,
        TimeSpan? vtxoTimeout = null,
        CancellationToken ct = default)
    {
        var transport = wallet.GetService<global::NArk.Core.Transport.IClientTransport>();
        var serverInfo = await transport.GetServerInfoAsync(ct);
        var emulatorInfo = await new EmulatorClient(emulatorUri).GetInfoAsync(ct);
        var contract = DeathMatchEscrowContracts.BuildJoint(parameters, serverInfo.SignerKey, emulatorInfo.SignerPubkey);

        var isChallenger = wallet.Address == parameters.ChallengerAddress;
        var isDefender = wallet.Address == parameters.DefenderAddress;
        if (!isChallenger && !isDefender)
            throw new InvalidOperationException(
                $"This wallet ({wallet.Address}) is not a party to death-match {parameters.DeathMatchId}.");

        IReadOnlyList<ArkVtxo> vtxos;
        try
        {
            vtxos = await CovenantSpender.WaitForVtxosAsync(
                wallet, contract, 1, vtxoTimeout ?? TimeSpan.FromSeconds(20), ct);
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException(
                $"No VTXO at the death-match escrow for {parameters.DeathMatchId} — nothing staked, already settled, or already reclaimed.");
        }

        var bothHeroesPresent =
            vtxos.Any(v => v.Assets?.Any(a => a.AssetId == parameters.ChallengerHeroAssetId) == true) &&
            vtxos.Any(v => v.Assets?.Any(a => a.AssetId == parameters.DefenderHeroAssetId) == true);

        return bothHeroesPresent
            ? await AtomicRefundAsync(wallet, emulatorUri, parameters, contract, vtxos, chainMedianTime, ct)
            : await AbortAsync(wallet, emulatorUri, parameters, contract, vtxos, isChallenger, requestAbortSig, ct);
    }

    // Both heroes staked, abandoned: the timelocked refund routes each side home.
    private static async Task<EmulatorSubmitResponse> AtomicRefundAsync(
        SelfCustodyWallet wallet, Uri emulatorUri, DeathMatchJointEscrowParams p,
        ArkadeArtifactContract contract, IReadOnlyList<ArkVtxo> vtxos,
        Func<CancellationToken, Task<long>> chainMedianTime, CancellationToken ct)
    {
        var chainNow = await chainMedianTime(ct);
        if (chainNow < p.RefundAfterUnixSeconds)
            throw new RefundNotYetDueException(p.RefundAfterUnixSeconds, chainNow);

        var challengerScript = ArkAddress.Parse(p.ChallengerAddress).ScriptPubKey;
        var defenderScript = ArkAddress.Parse(p.DefenderAddress).ScriptPubKey;
        var challengerAssets = new HashSet<string>(
            new[] { p.ChallengerHeroAssetId }.Concat((p.ChallengerGear ?? []).Select(g => g.AssetId)));

        // Challenger's assets → home output 0, defender's → output 1.
        var lockTime = new LockTime((uint)p.RefundAfterUnixSeconds);
        var inputs = vtxos
            .Select(v => new CovenantSpender.CovenantInput(contract, "refund", [], v, LockTime: lockTime))
            .ToList();
        var packet = BuildRoutingPacket(vtxos, assetId => challengerAssets.Contains(assetId) ? 0 : 1);

        long challengerSats = 0, defenderSats = 0;
        foreach (var v in vtxos)
        {
            var mine = v.Assets?.All(a => challengerAssets.Contains(a.AssetId)) == true;
            if (mine) challengerSats += (long)v.Amount; else defenderSats += (long)v.Amount;
        }
        return await CovenantSpender.SpendManyAsync(
            wallet, emulatorUri, inputs,
            [new TxOut(Money.Satoshis(challengerSats), challengerScript),
             new TxOut(Money.Satoshis(defenderSats), defenderScript)],
            extraPackets: [packet], ct: ct);
    }

    // Only my hero staked: my oracle-gated abort routes my hero + gear home to output 0.
    private static async Task<EmulatorSubmitResponse> AbortAsync(
        SelfCustodyWallet wallet, Uri emulatorUri, DeathMatchJointEscrowParams p,
        ArkadeArtifactContract contract, IReadOnlyList<ArkVtxo> vtxos, bool isChallenger,
        Func<CancellationToken, Task<byte[]>> requestAbortSig, CancellationToken ct)
    {
        var myScript = ArkAddress.Parse(isChallenger ? p.ChallengerAddress : p.DefenderAddress).ScriptPubKey;
        var myHeroId = isChallenger ? p.ChallengerHeroAssetId : p.DefenderHeroAssetId;
        var myGear = (isChallenger ? p.ChallengerGear : p.DefenderGear) ?? [];
        var myAssetIds = new HashSet<string>(new[] { myHeroId }.Concat(myGear.Select(g => g.AssetId)));

        // Spend ONLY carriers whose assets are entirely mine — never a counterparty carrier.
        var mine = vtxos.Where(v => v.Assets?.All(a => myAssetIds.Contains(a.AssetId)) == true).ToList();
        if (mine.Count == 0)
            throw new InvalidOperationException(
                $"No stake of yours is at the death-match escrow for {p.DeathMatchId}.");

        var oracleSig = await requestAbortSig(ct);
        var leaf = isChallenger ? "abortChallenger" : "abortDefender";
        var inputs = mine
            .Select(v => new CovenantSpender.CovenantInput(contract, leaf, [oracleSig], v))
            .ToList();
        var packet = BuildRoutingPacket(mine, _ => 0);   // everything → my output 0
        var total = mine.Sum(v => (long)v.Amount);
        return await CovenantSpender.SpendManyAsync(
            wallet, emulatorUri, inputs,
            [new TxOut(Money.Satoshis(total), myScript)],
            extraPackets: [packet], ct: ct);
    }

    // One AssetGroup per asset id, its inputs at their carrier vins, its outputs one per
    // home (summed amount) — vins follow the input (coin) order passed to SpendManyAsync.
    private static Packet BuildRoutingPacket(IReadOnlyList<ArkVtxo> vtxos, Func<string, int> homeOf)
    {
        var byAsset = new Dictionary<string, (List<(ushort vin, ulong amt)> ins, SortedDictionary<int, ulong> outByHome)>(StringComparer.Ordinal);
        for (ushort vin = 0; vin < vtxos.Count; vin++)
            foreach (var a in vtxos[vin].Assets ?? [])
            {
                if (!byAsset.TryGetValue(a.AssetId, out var g))
                {
                    g = (new List<(ushort, ulong)>(), new SortedDictionary<int, ulong>());
                    byAsset[a.AssetId] = g;
                }
                g.ins.Add((vin, a.Amount));
                var home = homeOf(a.AssetId);
                g.outByHome[home] = g.outByHome.GetValueOrDefault(home) + a.Amount;
            }
        return Packet.Create(byAsset.Select(kv => AssetGroup.Create(
            AssetId.FromString(kv.Key), null,
            kv.Value.ins.Select(i => AssetInput.Create(i.vin, i.amt)).ToArray(),
            kv.Value.outByHome.Select(o => AssetOutput.Create((ushort)o.Key, o.Value)).ToArray(),
            [])).ToArray());
    }
}
