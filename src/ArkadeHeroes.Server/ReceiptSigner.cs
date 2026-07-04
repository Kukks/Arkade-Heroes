using ArkadeHeroes.Shared;
using Microsoft.Extensions.Options;
using NBitcoin.Secp256k1;

namespace ArkadeHeroes.Server;

/// <summary>
/// Holds the game's receipt-signing key (BIP340). The public key is advertised
/// via <c>/api/chain/info</c> so any player can verify receipts offline.
/// Configure a fixed key with <c>Game:ReceiptKeyHex</c> (32-byte hex) so
/// receipts stay verifiable across server restarts; without it a fresh key is
/// generated per process (fine for dev, logged as a warning).
/// </summary>
public class ReceiptSigner
{
    private readonly ECPrivKey _key;

    public string PublicKeyHex { get; }

    public ReceiptSigner(IOptions<GameOptions> options, ILogger<ReceiptSigner> logger)
    {
        var configured = options.Value.ReceiptKeyHex;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            _key = ECPrivKey.Create(Convert.FromHexString(configured));
        }
        else
        {
            _key = ECPrivKey.Create(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            logger.LogWarning(
                "No Game:ReceiptKeyHex configured — using an ephemeral receipt key; receipts will not verify across restarts.");
        }
        Span<byte> pub = stackalloc byte[32];
        _key.CreateXOnlyPubKey().WriteToSpan(pub);
        PublicKeyHex = Convert.ToHexString(pub).ToLowerInvariant();
    }

    /// <summary>Signs and finalizes a receipt (fills the key + signature fields).</summary>
    public ProgressionReceiptDto Issue(ProgressionReceiptDto unsigned)
    {
        var withKey = unsigned with { GameSignerKeyHex = PublicKeyHex, SignatureHex = "" };
        return withKey with { SignatureHex = ReceiptVerifier.Sign(withKey, _key) };
    }

    /// <summary>
    /// BIP340 signature over an arbitrary 32-byte digest with the same game key
    /// — used as the on-chain oracle authorization in covenant settle branches.
    /// </summary>
    public byte[] SignDigest(byte[] digest32)
    {
        var signature = _key.SignBIP340(digest32);
        var bytes = new byte[64];
        signature.WriteToSpan(bytes);
        return bytes;
    }
}
