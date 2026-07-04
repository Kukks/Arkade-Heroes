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
    {
        var script = contract.GetArkAddress().ScriptPubKey.ToHex();
        var vtxoSync = observer.GetService<VtxoSynchronizationService>();
        var vtxoStorage = observer.GetService<IVtxoStorage>();
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await vtxoSync.PollScriptsForVtxos(new HashSet<string> { script });
            var vtxo = (await vtxoStorage.GetVtxos(scripts: [script], cancellationToken: ct)).FirstOrDefault();
            if (vtxo is not null) return vtxo;
            await Task.Delay(500, ct);
        }
        throw new TimeoutException($"No VTXO observed at covenant address within {timeout}.");
    }

    /// <summary>
    /// Spends a covenant VTXO through the named function. <paramref name="gameOutputs"/>
    /// are the value outputs the covenant constrains; the packet output is appended here.
    /// </summary>
    public static async Task<EmulatorSubmitResponse> SpendAsync(
        SelfCustodyWallet actor,
        Uri emulatorUri,
        ArkadeArtifactContract contract,
        string functionName,
        IReadOnlyList<byte[]> functionWitness,
        ArkVtxo vtxo,
        TxOut[] gameOutputs,
        CancellationToken ct = default)
    {
        var transport = actor.GetService<global::NArk.Core.Transport.IClientTransport>();
        var serverInfo = await transport.GetServerInfoAsync(ct);

        var coin = new ArkCoin(
            actor.WalletId, contract, vtxo.CreatedAt, vtxo.ExpiresAt, vtxo.ExpiresAtHeight,
            vtxo.OutPoint, vtxo.TxOut,
            signerDescriptor: null,
            spendingScriptBuilder: contract.LeafFor(functionName),
            spendingConditionWitness: null,
            lockTime: null, sequence: null,
            vtxo.Swept, vtxo.Unrolled, vtxo.Assets);

        var packet = new EmulatorPacket([new EmulatorEntry(0, contract.ScriptFor(functionName), functionWitness)]);
        var extension = new Extension([packet]);
        TxOut[] outputs =
        [
            .. gameOutputs,
            new TxOut(Money.Zero, Script.FromBytesUnsafe(extension.Serialize())),
        ];

        var builder = new TransactionHelpers.ArkTransactionBuilder(
            transport,
            actor.GetService<ISafetyService>(),
            actor.GetService<IWalletProvider>(),
            actor.GetService<IIntentStorage>());
        var (arkTx, checkpoints) = await builder.ConstructArkTransaction([coin], outputs, serverInfo, ct);

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
