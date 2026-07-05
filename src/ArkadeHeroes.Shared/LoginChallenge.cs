using System.Security.Cryptography;
using System.Text;

namespace ArkadeHeroes.Shared;

/// <summary>
/// The domain-separated 32-byte digest a wallet signs (BIP340) to prove control
/// of its login key for "sign in with your wallet". Computed IDENTICALLY on the
/// client (which signs it) and the server (which verifies), and bound to both
/// this app and the exact server-issued nonce — so a login signature can neither
/// be reused as some other signature nor replayed after the nonce is consumed.
/// </summary>
public static class LoginChallenge
{
    public static byte[] Digest(string nonceHex) =>
        SHA256.HashData(Encoding.UTF8.GetBytes($"arkade-heroes-login-v1|{nonceHex}"));
}
