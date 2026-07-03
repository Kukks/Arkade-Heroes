using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace ArkadeHeroes.Chain.Covenants;

public record EmulatorInfo(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("signerPubkey")] string SignerPubkey,
    [property: JsonPropertyName("deprecatedSignerPubkeys")] string[]? DeprecatedSignerPubkeys);

public record EmulatorSubmitRequest(
    [property: JsonPropertyName("arkTx")] string ArkTx,
    [property: JsonPropertyName("checkpointTxs")] string[] CheckpointTxs);

public record EmulatorSubmitResponse(
    [property: JsonPropertyName("signedArkTx")] string SignedArkTx,
    [property: JsonPropertyName("signedCheckpointTxs")] string[] SignedCheckpointTxs);

/// <summary>
/// REST client for the Arkade Script emulator (the covenant co-signing
/// service; regtest default <c>http://localhost:7073</c>). The emulator
/// executes the Arkade Script revealed in a transaction's Emulator Packet and
/// signs with the script-tweaked key only when the predicate holds — its
/// signature IS the covenant enforcement. Message shapes follow
/// emulator/api-spec (camelCase JSON gateway, base64 PSBTs), the same
/// endpoints coinflip's server drives.
/// </summary>
public class EmulatorClient(HttpClient http)
{
    public EmulatorClient(Uri baseAddress) : this(new HttpClient { BaseAddress = baseAddress }) { }

    /// <summary>GET /v1/info — emulator version and covenant signer key (pre-tweak).</summary>
    public async Task<EmulatorInfo> GetInfoAsync(CancellationToken ct = default)
        => await http.GetFromJsonAsync<EmulatorInfo>("v1/info", ct)
           ?? throw new InvalidOperationException("Emulator /v1/info returned an empty body.");

    /// <summary>
    /// POST /v1/tx — submit an Arkade transaction whose inputs carry Emulator
    /// Packets; returns the same PSBTs with the emulator's covenant signatures
    /// added (only if every script evaluated true).
    /// </summary>
    public async Task<EmulatorSubmitResponse> SubmitTxAsync(EmulatorSubmitRequest request, CancellationToken ct = default)
    {
        var response = await http.PostAsJsonAsync("v1/tx", request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Emulator rejected the transaction ({(int)response.StatusCode}): {body}");
        }
        return (await response.Content.ReadFromJsonAsync<EmulatorSubmitResponse>(cancellationToken: ct))!;
    }
}
