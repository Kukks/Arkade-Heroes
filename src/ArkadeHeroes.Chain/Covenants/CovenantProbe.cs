using ArkadeHeroes.Chain.NArk;
using NArk.Abstractions;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Safety;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Arkade.Emulator;
using NArk.Core.Assets;
using NArk.Core.Helpers;
using NArk.Core.Services;
using NBitcoin;

namespace ArkadeHeroes.Chain.Covenants;

/// <summary>
/// Generalized covenant spend pipeline over <see cref="ArkadeArtifactContract"/>:
/// observe a covenant VTXO, build the Arkade tx (checkpoints included, nothing
/// signed locally — the emulator and operator sign), attach the
/// <see cref="EmulatorPacket"/> revealing the function's script + witness, and
/// submit to the emulator for covenant-validated co-signing.
/// </summary>
public static class CovenantSpender
{
    /// <summary>Waits for a VTXO to appear at the given contract's address (script-addressed).</summary>
    public static async Task<ArkVtxo> WaitForVtxoAsync(
        SelfCustodyWallet observer, ArkadeArtifactContract contract, TimeSpan timeout, CancellationToken ct = default)
        => (await WaitForVtxosAsync(observer, contract, 1, timeout, ct))[0];

    /// <summary>Waits for at least <paramref name="count"/> unspent VTXOs at the contract's address (e.g. both wager stakes).</summary>
    public static Task<IReadOnlyList<ArkVtxo>> WaitForVtxosAsync(
        SelfCustodyWallet observer, ArkadeArtifactContract contract, int count, TimeSpan timeout, CancellationToken ct = default)
        => WaitForVtxosCoreAsync(observer.GetService<VtxoSynchronizationService>(),
            observer.GetService<IVtxoStorage>(), contract, count, timeout, ct);

    /// <summary>
    /// Service-level wait — usable from any NArk service graph (a player wallet's isolated
    /// container or a browser's Blazor DI). Polls arkd for VTXOs at the contract address.
    /// </summary>
    public static async Task<IReadOnlyList<ArkVtxo>> WaitForVtxosCoreAsync(
        VtxoSynchronizationService vtxoSync, IVtxoStorage vtxoStorage,
        ArkadeArtifactContract contract, int count, TimeSpan timeout, CancellationToken ct = default)
    {
        var script = contract.GetArkAddress().ScriptPubKey.ToHex();
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await vtxoSync.PollScriptsForVtxos(new HashSet<string> { script });
            var vtxos = (await vtxoStorage.GetVtxos(scripts: [script], cancellationToken: ct))
                .DistinctBy(v => v.OutPoint).ToList();
            if (vtxos.Count >= count) return vtxos;
            await Task.Delay(500, ct);
        }
        throw new TimeoutException($"Expected {count} VTXO(s) at the covenant address within {timeout}.");
    }

    /// <summary>
    /// One covenant input of a multi-input spend: which contract/function/witness
    /// spends which VTXO. Timelocked functions set LockTime (tx-level) and a
    /// non-final Sequence.
    /// WARNING (timelocked spends): submit only when the chain's blocktime has
    /// passed the leaf's CLTV. A refused submission poisons the canonical txid's
    /// event stream on arkd (v0.9.9-rc.1) — a later accepted resubmission of the
    /// SAME txid finalizes but its VTXOs are never created. See
    /// <see cref="ArkadeCovenants.RefundTo"/>.
    /// </summary>
    public sealed record CovenantInput(
        ArkadeArtifactContract Contract,
        string FunctionName,
        IReadOnlyList<byte[]> Witness,
        ArkVtxo Vtxo,
        LockTime? LockTime = null,
        Sequence? Sequence = null);

    /// <summary>
    /// Spends a covenant VTXO through the named function. <paramref name="gameOutputs"/>
    /// are the value outputs the covenant constrains; the packet output is appended here.
    /// </summary>
    public static Task<EmulatorSubmitTxResult> SpendAsync(
        SelfCustodyWallet actor,
        Uri emulatorUri,
        ArkadeArtifactContract contract,
        string functionName,
        IReadOnlyList<byte[]> functionWitness,
        ArkVtxo vtxo,
        TxOut[] gameOutputs,
        CancellationToken ct = default)
        => SpendManyAsync(actor, emulatorUri,
            [new CovenantInput(contract, functionName, functionWitness, vtxo)], gameOutputs, ct: ct);

    /// <summary>
    /// Spends MULTIPLE covenant VTXOs in one Arkade transaction (e.g. the two
    /// wager stakes swept atomically). Packet entry vins follow input order.
    /// <paramref name="extraPackets"/> (e.g. an asset issuance packet for a
    /// covenant-gated mint) are composed into the SAME extension output as the
    /// EmulatorPacket — arkd requires one extension output, and the builder's
    /// asset-vin remap path rebuilds it from the asset packet alone, so input
    /// order must stay deterministic (it does: ShuffleInputs=false).
    /// </summary>
    public static Task<EmulatorSubmitTxResult> SpendManyAsync(
        SelfCustodyWallet actor,
        Uri emulatorUri,
        IReadOnlyList<CovenantInput> inputs,
        TxOut[] gameOutputs,
        IReadOnlyList<IExtensionPacket>? extraPackets = null,
        IReadOnlyList<ArkCoin>? fundingCoins = null,
        CancellationToken ct = default)
        => SpendManyCoreAsync(
            actor.GetService<global::NArk.Core.Transport.IClientTransport>(),
            actor.GetService<ISafetyService>(),
            actor.GetService<IWalletProvider>(),
            actor.GetService<IIntentStorage>(),
            actor.WalletId, emulatorUri, inputs, gameOutputs, extraPackets, fundingCoins, ct);

    /// <summary>
    /// Service-level core of the covenant spend — usable from any NArk service
    /// graph (a player wallet or the game server's own DI container).
    /// </summary>
    public static async Task<EmulatorSubmitTxResult> SpendManyCoreAsync(
        global::NArk.Core.Transport.IClientTransport transport,
        ISafetyService safetyService,
        IWalletProvider walletProvider,
        IIntentStorage intentStorage,
        string walletId,
        Uri emulatorUri,
        IReadOnlyList<CovenantInput> inputs,
        TxOut[] gameOutputs,
        IReadOnlyList<IExtensionPacket>? extraPackets = null,
        IReadOnlyList<ArkCoin>? fundingCoins = null,
        CancellationToken ct = default)
    {
        var serverInfo = await transport.GetServerInfoAsync(ct);

        var coins = new List<ArkCoin>();
        var entries = new List<EmulatorEntry>();
        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            // Timelocked leaves: LockTime/Sequence flow into the coin so the
            // CHECKPOINT transaction (which spends the leaf) carries them —
            // that is where the operator enforces the CLTV.
            var sequence = input.Sequence
                ?? (input.LockTime is { } lt && lt != LockTime.Zero ? new Sequence(0xFFFFFFFE) : null);
            coins.Add(new ArkCoin(
                walletId, input.Contract, input.Vtxo.CreatedAt, input.Vtxo.ExpiresAt, input.Vtxo.ExpiresAtHeight,
                input.Vtxo.OutPoint, input.Vtxo.TxOut,
                signerDescriptor: null,
                spendingScriptBuilder: input.Contract.LeafFor(input.FunctionName),
                spendingConditionWitness: null,
                lockTime: input.LockTime, sequence: sequence,
                input.Vtxo.Swept, input.Vtxo.Unrolled, input.Vtxo.Assets));
            entries.Add(new EmulatorEntry((ushort)i, input.Contract.ScriptFor(input.FunctionName), input.Witness));
        }

        // The actor's own funding coins (e.g. a buyer paying an offer's ask) are
        // appended after the covenant inputs. They carry real signer descriptors,
        // so ConstructArkTransaction signs them with the actor's key while the
        // emulator co-signs only the covenant inputs.
        if (fundingCoins is { Count: > 0 })
            coins.AddRange(fundingCoins);

        var packetList = new List<IExtensionPacket>(extraPackets ?? []);
        packetList.Add(new EmulatorPacket(entries));
        var extension = new Extension(packetList);
        TxOut[] outputs =
        [
            .. gameOutputs,
            new TxOut(Money.Zero, Script.FromBytesUnsafe(extension.Serialize())),
        ];

        var builder = new TransactionHelpers.ArkTransactionBuilder(
            transport, safetyService, walletProvider, intentStorage);
        // The ark tx's locktime and per-input sequences are the SDK's job now:
        // ConstructArkTransaction takes the max locktime across the coins and gives each one
        // 0xFFFFFFFE or 0xFFFFFFFF by whether it is actually timelocked. We used to re-apply
        // that here and force 0xFFFFFFFE on EVERY input, which is wrong for a spend that mixes
        // a timelocked leaf with plain funding coins.
        var (arkTx, checkpoints) = await builder.ConstructArkTransaction(coins, outputs, serverInfo, ct);

        // Mixed covenant + actor-funding spends: NBitcoin orders the ark tx's
        // inputs by outpoint (BIP69), not by our coin order, so NArk's asset-vin
        // remap fires and rebuilds the extension output from the ASSET packet
        // alone — silently dropping the EmulatorPacket. Its input order is only
        // known AFTER construction, so we correct the extension here (rebuild it
        // with the EmulatorPacket's vins pointing at each covenant input's ACTUAL
        // position) and then re-sign the funding inputs over the fixed outputs.
        if (fundingCoins is { Count: > 0 })
        {
            var gtx = arkTx.GetGlobalTransaction();

            // Each covenant input's actual vin: its checkpoint's ark-tx index.
            var actualVin = new ushort[inputs.Count];
            for (var i = 0; i < inputs.Count; i++)
            {
                var cp = checkpoints.First(c =>
                    c.Psbt.GetGlobalTransaction().Inputs[0].PrevOut == inputs[i].Vtxo.OutPoint);
                actualVin[i] = (ushort)cp.Index;
            }
            var fixedEntries = entries.Select((e, i) => new EmulatorEntry(actualVin[i], e.Script, e.Witness)).ToList();

            // Rebuild the extension: the asset packet NArk already remapped (read
            // it back from the tx) + the corrected EmulatorPacket.
            var currentExt = Extension.FromTransaction(gtx);
            var assetPacket = currentExt?.GetAssetPacket();
            var rebuilt = new List<IExtensionPacket>();
            if (assetPacket is not null) rebuilt.Add(assetPacket);
            rebuilt.Add(new EmulatorPacket(fixedEntries));
            var newExtScript = Script.FromBytesUnsafe(new Extension(rebuilt).Serialize());

            var extIdx = gtx.Outputs.FindIndex(o => Extension.IsExtension(o.ScriptPubKey));
            if (extIdx < 0) throw new InvalidOperationException("BUG: extension output vanished before correction.");
            gtx.Outputs[extIdx].ScriptPubKey = newExtScript;

            var rebuiltTx = PSBT.FromTransaction(gtx, serverInfo.Network, PSBTVersion.PSBTv0);
            rebuiltTx.UpdateFrom(arkTx);
            arkTx = rebuiltTx;

            // Re-sign the funding inputs over the corrected outputs, and sign
            // their checkpoints (the emulator verifies non-arkd sigs on ALL
            // checkpoints; the covenant checkpoints it signs itself).
            var signer = await walletProvider.GetSignerAsync(walletId, ct)
                ?? throw new InvalidOperationException($"Cannot sign offer funding input: wallet '{walletId}' has no signer.");
            var arkPrevouts = arkTx.Inputs.Select(inp => inp.GetTxOut()!).ToArray();
            var arkPrecomputed = arkTx.GetGlobalTransaction().PrecomputeTransactionData(arkPrevouts);
            foreach (var fundingCoin in fundingCoins)
            {
                var cp = checkpoints.First(c =>
                    c.Psbt.GetGlobalTransaction().Inputs[0].PrevOut == fundingCoin.Outpoint);
                var cpGtx = cp.Psbt.GetGlobalTransaction();

                // 1. Sign the funding coin's own checkpoint (input 0 = the VTXO).
                var cpPrecomputed = cpGtx.PrecomputeTransactionData([fundingCoin.TxOut]);
                await global::NArk.Abstractions.Helpers.PsbtHelpers.SignAndFillPsbt(
                    signer, fundingCoin, cp.Psbt, cpPrecomputed, cancellationToken: ct);

                // 2. Re-sign the ark-tx input that spends this funding coin's
                // CHECKPOINT output (the outputs changed when we fixed the
                // extension, invalidating ConstructArkTransaction's signature).
                // Reconstruct the checkpoint coin exactly as NArk does internally.
                var checkpointContract = new global::NArk.Core.Contracts.GenericArkContract(
                    fundingCoin.Contract.Server!,
                    [fundingCoin.SpendingScriptBuilder, serverInfo.CheckpointTapScript]);
                var cpScript = checkpointContract.GetArkAddress().ScriptPubKey;
                var cpOutIndex = cpGtx.Outputs.FindIndex(o => o.ScriptPubKey == cpScript);
                var checkpointCoin = new global::NArk.Abstractions.ArkCoin(
                    fundingCoin.WalletIdentifier, checkpointContract, fundingCoin.Birth,
                    fundingCoin.ExpiresAt, fundingCoin.ExpiresAtHeight,
                    new OutPoint(cpGtx, cpOutIndex), cpGtx.Outputs[cpOutIndex],
                    fundingCoin.SignerDescriptor, fundingCoin.SpendingScriptBuilder,
                    null, null, null, fundingCoin.Swept, fundingCoin.Unrolled);
                await global::NArk.Abstractions.Helpers.PsbtHelpers.SignAndFillPsbt(
                    signer, checkpointCoin, arkTx, arkPrecomputed, cancellationToken: ct);
            }
        }

        // Emulator v0.0.7+ refuses any input that does not carry the transaction which funded
        // it ("missing prevout tx for input N") — it needs those outputs to run introspection.
        // The field rides the PSBT's unknown map, which no signature covers, so it is attached
        // after signing and just before submitting.
        var checkpointPsbts = checkpoints.Select(c => c.Psbt).ToList();
        await arkTx.AttachPrevArkTxsAsync(checkpointPsbts, new PrevArkTxProvider(transport), ct);

        return await EmulatorEndpoint.Client(emulatorUri).SubmitTxAsync(
            arkTx.ToBase64(), [.. checkpointPsbts.Select(p => p.ToBase64())], ct);
    }
}

/// <summary>
/// Minimal end-to-end covenant probe used by the E2E suite: fund a VTXO whose
/// only leaf is bound to the given raw Arkade Script, then spend it through the
/// emulator. Proves co-signing for passing scripts and refusal for failing ones.
/// </summary>
public static class CovenantProbe
{
    public sealed record ProbeResult(
        string CovenantAddress,
        string FundingTxId,
        string SignedArkTx,
        int SignedCheckpointCount);

    public static async Task<ProbeResult> RunAsync(
        SelfCustodyWallet funder,
        Uri emulatorUri,
        byte[] arkadeScript,
        IReadOnlyList<byte[]> scriptWitness,
        long fundSats = 20_000,
        CancellationToken ct = default)
    {
        var transport = funder.GetService<global::NArk.Core.Transport.IClientTransport>();
        var serverInfo = await transport.GetServerInfoAsync(ct);
        var emulatorInfo = await EmulatorEndpoint.Client(emulatorUri).GetInfoAsync(ct);

        var contract = new ArkadeArtifactContract(
            "probe", serverInfo.SignerKey, emulatorInfo.SignerPubkey,
            [new ArkadeContractFunction("probe", arkadeScript)]);
        var address = contract.GetArkAddress();
        var addressText = address.ToString(serverInfo.Network == Network.Main);

        var fundingTxId = await funder.SendAsync(addressText, fundSats, ct);
        var vtxo = await CovenantSpender.WaitForVtxoAsync(funder, contract, TimeSpan.FromSeconds(30), ct);

        var response = await CovenantSpender.SpendAsync(
            funder, emulatorUri, contract, "probe", scriptWitness, vtxo,
            [new TxOut(Money.Satoshis((long)vtxo.Amount), ArkAddress.Parse(funder.Address))], ct);

        return new ProbeResult(addressText, fundingTxId, response.SignedArkTx, response.SignedCheckpointTxs.Count);
    }
}
