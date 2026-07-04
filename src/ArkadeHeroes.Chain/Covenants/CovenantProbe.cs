using ArkadeHeroes.Chain.NArk;
using NArk.Abstractions;
using NArk.Abstractions.Extensions;
using NArk.Abstractions.Intents;
using NArk.Abstractions.Safety;
using NArk.Abstractions.VTXOs;
using NArk.Abstractions.Wallets;
using NArk.Core.Assets;
using NArk.Core.Contracts;
using NArk.Core.Helpers;
using NArk.Core.Scripts;
using NArk.Core.Services;
using NBitcoin;

namespace ArkadeHeroes.Chain.Covenants;

/// <summary>
/// End-to-end covenant pipeline: builds a VTXO whose only spend path is the
/// covenant leaf <c>&lt;tweakedEmulatorKey&gt; CHECKSIGVERIFY &lt;serverKey&gt; CHECKSIG</c>
/// (the emulator key tweaked by the Arkade Script via <see cref="ArkadeScriptTweak"/>),
/// funds it from a self-custody wallet, then spends it by revealing the script
/// in an <see cref="EmulatorPacket"/> and submitting to the emulator. The
/// emulator executes the script in its Arkade VM and co-signs ONLY if it
/// evaluates true — its signature is the covenant enforcement.
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

        // 1. Covenant leaf: the emulator key tweaked by THIS script + the operator key.
        var tweaked = ArkadeScriptTweak
            .ComputeCovenantPublicKey(emulatorInfo.SignerPubkey, arkadeScript)
            .ToXOnlyPubKey();
        var leaf = new CollaborativePathArkTapScript(
            serverInfo.SignerKey.ToXOnlyPubKey(),
            new NofNMultisigTapScript([tweaked]));
        var contract = new GenericArkContract(serverInfo.SignerKey, [leaf]);
        var address = contract.GetArkAddress();
        var addressText = address.ToString(serverInfo.Network == Network.Main);

        // 2. Fund the covenant VTXO from the player's own wallet.
        var fundingTxId = await funder.SendAsync(addressText, fundSats, ct);

        // 3. Observe it (script-addressed, via the funder's sync machinery).
        var script = address.ScriptPubKey.ToHex();
        var vtxoSync = funder.GetService<VtxoSynchronizationService>();
        var vtxoStorage = funder.GetService<IVtxoStorage>();
        ArkVtxo? vtxo = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            await vtxoSync.PollScriptsForVtxos(new HashSet<string> { script });
            vtxo = (await vtxoStorage.GetVtxos(scripts: [script], cancellationToken: ct)).FirstOrDefault();
            if (vtxo is not null) break;
            await Task.Delay(500, ct);
        }
        if (vtxo is null)
            throw new TimeoutException($"Covenant VTXO at {addressText} was not observed within 30s (funding tx {fundingTxId}).");

        // 4. The coin spends via the covenant leaf; nobody local signs it —
        //    the emulator and the operator each add their signature.
        var coin = new ArkCoin(
            funder.WalletId, contract, vtxo.CreatedAt, vtxo.ExpiresAt, vtxo.ExpiresAtHeight,
            vtxo.OutPoint, vtxo.TxOut,
            signerDescriptor: null,
            spendingScriptBuilder: leaf,
            spendingConditionWitness: null,
            lockTime: null, sequence: null,
            vtxo.Swept, vtxo.Unrolled, vtxo.Assets);

        // 5. Outputs: everything back to the funder, plus the ARK extension
        //    OP_RETURN carrying the Emulator Packet that reveals the script.
        var packet = new EmulatorPacket([new EmulatorEntry(0, arkadeScript, scriptWitness)]);
        var extension = new Extension([packet]);
        TxOut[] outputs =
        [
            new TxOut(Money.Satoshis((long)vtxo.Amount), ArkAddress.Parse(funder.Address)),
            new TxOut(Money.Zero, Script.FromBytesUnsafe(extension.Serialize())),
        ];

        // 6. Standard Arkade tx construction (checkpoints included), then
        //    submission to the EMULATOR rather than the operator — the emulator
        //    validates the covenant, signs with the tweaked key, and coordinates
        //    the operator's signatures.
        var builder = new TransactionHelpers.ArkTransactionBuilder(
            transport,
            funder.GetService<ISafetyService>(),
            funder.GetService<IWalletProvider>(),
            funder.GetService<IIntentStorage>());
        var (arkTx, checkpoints) = await builder.ConstructArkTransaction([coin], outputs, serverInfo, ct);

        var response = await emulator.SubmitTxAsync(new EmulatorSubmitRequest(
            arkTx.ToBase64(),
            checkpoints.Select(c => c.Psbt.ToBase64()).ToArray()), ct);

        return new ProbeResult(addressText, fundingTxId, response.SignedArkTx, response.SignedCheckpointTxs.Length);
    }
}
