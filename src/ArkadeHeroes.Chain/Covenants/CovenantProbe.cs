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

    /// <summary>One covenant input of a multi-input spend: which contract/function/witness spends which VTXO.</summary>
    public sealed record CovenantInput(
        ArkadeArtifactContract Contract,
        string FunctionName,
        IReadOnlyList<byte[]> Witness,
        ArkVtxo Vtxo);

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
            [new CovenantInput(contract, functionName, functionWitness, vtxo)], gameOutputs, ct);

    /// <summary>
    /// Spends MULTIPLE covenant VTXOs in one Arkade transaction (e.g. the two
    /// wager stakes swept atomically). Packet entry vins follow input order.
    /// </summary>
    public static Task<EmulatorSubmitResponse> SpendManyAsync(
        SelfCustodyWallet actor,
        Uri emulatorUri,
        IReadOnlyList<CovenantInput> inputs,
        TxOut[] gameOutputs,
        CancellationToken ct = default)
        => SpendManyCoreAsync(
            actor.GetService<global::NArk.Core.Transport.IClientTransport>(),
            actor.GetService<ISafetyService>(),
            actor.GetService<IWalletProvider>(),
            actor.GetService<IIntentStorage>(),
            actor.WalletId, emulatorUri, inputs, gameOutputs, ct);

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
        CancellationToken ct = default)
    {
        var serverInfo = await transport.GetServerInfoAsync(ct);

        var coins = new List<ArkCoin>();
        var entries = new List<EmulatorEntry>();
        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            coins.Add(new ArkCoin(
                walletId, input.Contract, input.Vtxo.CreatedAt, input.Vtxo.ExpiresAt, input.Vtxo.ExpiresAtHeight,
                input.Vtxo.OutPoint, input.Vtxo.TxOut,
                signerDescriptor: null,
                spendingScriptBuilder: input.Contract.LeafFor(input.FunctionName),
                spendingConditionWitness: null,
                lockTime: null, sequence: null,
                input.Vtxo.Swept, input.Vtxo.Unrolled, input.Vtxo.Assets));
            entries.Add(new EmulatorEntry((ushort)i, input.Contract.ScriptFor(input.FunctionName), input.Witness));
        }

        var extension = new Extension([new EmulatorPacket(entries)]);
        TxOut[] outputs =
        [
            .. gameOutputs,
            new TxOut(Money.Zero, Script.FromBytesUnsafe(extension.Serialize())),
        ];

        var builder = new TransactionHelpers.ArkTransactionBuilder(
            transport, safetyService, walletProvider, intentStorage);
        var (arkTx, checkpoints) = await builder.ConstructArkTransaction(coins, outputs, serverInfo, ct);

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
