using ArkadeHeroes.Chain.NArk;
using NArk.Abstractions;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Safety;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
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
    public static async Task<IReadOnlyList<ArkVtxo>> WaitForVtxosAsync(
        SelfCustodyWallet observer, ArkadeArtifactContract contract, int count, TimeSpan timeout, CancellationToken ct = default)
    {
        var script = contract.GetArkAddress().ScriptPubKey.ToHex();
        var vtxoSync = observer.GetService<VtxoSynchronizationService>();
        var vtxoStorage = observer.GetService<IVtxoStorage>();
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
    public static Task<EmulatorSubmitResponse> SpendAsync(
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
    public static Task<EmulatorSubmitResponse> SpendManyAsync(
        SelfCustodyWallet actor,
        Uri emulatorUri,
        IReadOnlyList<CovenantInput> inputs,
        TxOut[] gameOutputs,
        IReadOnlyList<IExtensionPacket>? extraPackets = null,
        CancellationToken ct = default)
        => SpendManyCoreAsync(
            actor.GetService<global::NArk.Core.Transport.IClientTransport>(),
            actor.GetService<ISafetyService>(),
            actor.GetService<IWalletProvider>(),
            actor.GetService<IIntentStorage>(),
            actor.WalletId, emulatorUri, inputs, gameOutputs, extraPackets, ct);

    /// <summary>
    /// Service-level core of the covenant spend — usable from any NArk service
    /// graph (a player wallet or the game server's own DI container).
    /// </summary>
    public static async Task<EmulatorSubmitResponse> SpendManyCoreAsync(
        global::NArk.Core.Transport.IClientTransport transport,
        ISafetyService safetyService,
        IWalletProvider walletProvider,
        IIntentStorage intentStorage,
        string walletId,
        Uri emulatorUri,
        IReadOnlyList<CovenantInput> inputs,
        TxOut[] gameOutputs,
        IReadOnlyList<IExtensionPacket>? extraPackets = null,
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

        var packets = new List<IExtensionPacket>(extraPackets ?? []);
        packets.Add(new EmulatorPacket(entries));
        var extension = new Extension(packets);
        TxOut[] outputs =
        [
            .. gameOutputs,
            new TxOut(Money.Zero, Script.FromBytesUnsafe(extension.Serialize())),
        ];

        var builder = new TransactionHelpers.ArkTransactionBuilder(
            transport, safetyService, walletProvider, intentStorage);
        var (arkTx, checkpoints) = await builder.ConstructArkTransaction(coins, outputs, serverInfo, ct);

        // Timelocked leaves: arkd requires the CHECKPOINT and the ARK tx to
        // carry the SAME locktime (each side's canonical form is derived from
        // the other — mismatches surface as CHECKPOINT_MISMATCH/ARK_TX_MISMATCH).
        // The checkpoint got it from coin.LockTime; apply it to the ark tx too.
        var lockTime = inputs.Max(i => i.LockTime?.Value ?? 0);
        if (lockTime > 0)
        {
            var gtx = arkTx.GetGlobalTransaction();
            gtx.LockTime = new LockTime(lockTime);
            foreach (var txin in gtx.Inputs)
                txin.Sequence = new Sequence(0xFFFFFFFE);
            var relocked = PSBT.FromTransaction(gtx, serverInfo.Network, PSBTVersion.PSBTv0);
            relocked.UpdateFrom(arkTx);
            arkTx = relocked;
        }

        var emulator = new EmulatorClient(new Uri(emulatorUri.ToString().TrimEnd('/') + "/"));
        return await emulator.SubmitTxAsync(new EmulatorSubmitRequest(
            arkTx.ToBase64(),
            checkpoints.Select(c => c.Psbt.ToBase64()).ToArray()), ct);
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
        var emulator = new EmulatorClient(new Uri(emulatorUri.ToString().TrimEnd('/') + "/"));
        var emulatorInfo = await emulator.GetInfoAsync(ct);

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

        return new ProbeResult(addressText, fundingTxId, response.SignedArkTx, response.SignedCheckpointTxs.Length);
    }
}
