using System.Security.Cryptography;
using System.Text;

namespace ArkadeHeroes.Chain.NArk;

/// <summary>
/// Passphrase-based encryption for a wallet's secret (the BIP-39 mnemonic) so it
/// is never stored in cleartext at rest. A random salt + PBKDF2-SHA256 derives a
/// 256-bit key from the passphrase; AES-256-GCM encrypts the mnemonic and
/// authenticates it, so a wrong passphrase fails loudly (tag mismatch) rather
/// than returning garbage. The token is self-describing (<c>enc:v1:</c> prefix +
/// Base64 of <c>salt || nonce || tag || ciphertext</c>), so a plaintext secret
/// from an unencrypted wallet is distinguishable and passed through untouched.
/// </summary>
public static class WalletSecretCipher
{
    private const string Prefix = "enc:v1:";
    private const int SaltLen = 16;
    private const int NonceLen = 12; // AES-GCM standard nonce
    private const int TagLen = 16;   // AES-GCM standard tag
    private const int KeyLen = 32;   // AES-256
    // OWASP 2023 PBKDF2-SHA256 floor; a wallet unlock happens once per session,
    // so a high count is affordable and raises the brute-force cost of a stolen DB.
    private const int Iterations = 210_000;

    /// <summary>True if <paramref name="secret"/> is an encrypted token this cipher produced.</summary>
    public static bool IsEncrypted(string? secret) => secret is not null && secret.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>Encrypts a plaintext secret under the passphrase. Already-encrypted input is returned unchanged.</summary>
    public static string Encrypt(string plaintext, string passphrase)
    {
        if (IsEncrypted(plaintext)) return plaintext; // never double-wrap
        if (string.IsNullOrEmpty(passphrase)) throw new ArgumentException("A passphrase is required to encrypt.", nameof(passphrase));

        var salt = RandomNumberGenerator.GetBytes(SaltLen);
        var nonce = RandomNumberGenerator.GetBytes(NonceLen);
        var key = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(passphrase), salt, Iterations, HashAlgorithmName.SHA256, KeyLen);

        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipherBytes = new byte[plainBytes.Length];
        var tag = new byte[TagLen];
        using (var aes = new AesGcm(key, TagLen))
            aes.Encrypt(nonce, plainBytes, cipherBytes, tag);
        CryptographicOperations.ZeroMemory(key);

        var blob = new byte[SaltLen + NonceLen + TagLen + cipherBytes.Length];
        salt.CopyTo(blob, 0);
        nonce.CopyTo(blob, SaltLen);
        tag.CopyTo(blob, SaltLen + NonceLen);
        cipherBytes.CopyTo(blob, SaltLen + NonceLen + TagLen);
        return Prefix + Convert.ToBase64String(blob);
    }

    /// <summary>
    /// Decrypts a token produced by <see cref="Encrypt"/>. A plaintext (non-token)
    /// input is returned unchanged, so mixed encrypted/plaintext stores just work.
    /// Throws <see cref="CryptographicException"/> on a wrong passphrase or tampering.
    /// </summary>
    public static string Decrypt(string secret, string passphrase)
    {
        if (!IsEncrypted(secret)) return secret; // plaintext wallet — pass through
        if (string.IsNullOrEmpty(passphrase))
            throw new InvalidOperationException("This wallet is encrypted — a passphrase is required to open it.");

        byte[] blob;
        try { blob = Convert.FromBase64String(secret[Prefix.Length..]); }
        catch (FormatException ex) { throw new CryptographicException("Corrupt encrypted wallet secret.", ex); }
        if (blob.Length < SaltLen + NonceLen + TagLen)
            throw new CryptographicException("Corrupt encrypted wallet secret.");

        var salt = blob.AsSpan(0, SaltLen);
        var nonce = blob.AsSpan(SaltLen, NonceLen);
        var tag = blob.AsSpan(SaltLen + NonceLen, TagLen);
        var cipherBytes = blob.AsSpan(SaltLen + NonceLen + TagLen);

        var key = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(passphrase), salt.ToArray(), Iterations, HashAlgorithmName.SHA256, KeyLen);
        var plainBytes = new byte[cipherBytes.Length];
        try
        {
            using var aes = new AesGcm(key, TagLen);
            aes.Decrypt(nonce, cipherBytes, tag, plainBytes);
        }
        catch (CryptographicException)
        {
            throw new CryptographicException("Wrong passphrase, or the encrypted wallet is corrupt.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
        return Encoding.UTF8.GetString(plainBytes);
    }
}
