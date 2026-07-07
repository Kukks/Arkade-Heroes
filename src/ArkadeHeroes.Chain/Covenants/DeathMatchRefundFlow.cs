using ArkadeHeroes.Chain.NArk;
using NArk.Abstractions;
using NArk.Abstractions.Assets;
using NArk.Abstractions.VTXOs;
using NArk.Core.Assets;
using NBitcoin;

namespace ArkadeHeroes.Chain.Covenants;

/// <summary>
/// The CLIENT side of death-match reclaim: after expiry, MY hero + MY gear come home via
/// my timelocked <c>reclaim{Side}</c> leaf — fully structural (num-groups + input-sum bound),
/// no oracle, no server. Handles BOTH the half-funded case (opponent never showed) and a
/// fully-funded abandoned match (each side reclaims their own). Gated on the chain clock;
/// submitted exactly once. Mirrors <see cref="EscrowRefundFlow"/> / <see cref="MergeEscrowRefundFlow"/>.
/// </summary>
public static class DeathMatchRefundFlow
{
    public static async Task<EmulatorSubmitResponse> ReclaimAsync(
        SelfCustodyWallet wallet,
        Uri emulatorUri,
        DeathMatchJointEscrowParams parameters,
        Func<CancellationToken, Task<long>> chainMedianTime,
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

        var myScript = ArkAddress.Parse(isChallenger ? parameters.ChallengerAddress : parameters.DefenderAddress).ScriptPubKey;
        var myHeroId = isChallenger ? parameters.ChallengerHeroAssetId : parameters.DefenderHeroAssetId;
        var myGear = isChallenger ? parameters.ChallengerGear : parameters.DefenderGear;
        // My staked amount per asset (hero = 1). A SHARED fungible-gear asset appears at BOTH
        // sides' carriers, so I must include only up to MY amount — the reclaim leaf's
        // AssetInputSumIs(asset, myAmount) refuses any over-inclusion.
        var myAmounts = new Dictionary<string, long>(StringComparer.Ordinal) { [myHeroId] = 1 };
        foreach (var g in myGear ?? []) myAmounts[g.AssetId] = myAmounts.GetValueOrDefault(g.AssetId) + g.Amount;

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

        // Take carriers whose assets are ALL mine, capping each asset at my staked amount so a
        // shared-gear group never over-includes the counterparty's units (which the leaf refuses).
        var taken = new Dictionary<string, long>(StringComparer.Ordinal);
        var mine = new List<ArkVtxo>();
        foreach (var v in vtxos)
        {
            var assets = v.Assets;
            if (assets is null || assets.Count == 0) continue;
            if (!assets.All(a => myAmounts.ContainsKey(a.AssetId))) continue;
            if (assets.Any(a => taken.GetValueOrDefault(a.AssetId) + (long)a.Amount > myAmounts[a.AssetId])) continue;
            mine.Add(v);
            foreach (var a in assets) taken[a.AssetId] = taken.GetValueOrDefault(a.AssetId) + (long)a.Amount;
        }
        if (mine.Count == 0)
            throw new InvalidOperationException(
                $"No stake of yours is at the death-match escrow for {parameters.DeathMatchId}.");

        var chainNow = await chainMedianTime(ct);
        if (chainNow < parameters.RefundAfterUnixSeconds)
            throw new RefundNotYetDueException(parameters.RefundAfterUnixSeconds, chainNow);

        var leaf = isChallenger ? "reclaimChallenger" : "reclaimDefender";
        var lockTime = new LockTime((uint)parameters.RefundAfterUnixSeconds);
        var inputs = mine
            .Select(v => new CovenantSpender.CovenantInput(contract, leaf, [], v, LockTime: lockTime))
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
