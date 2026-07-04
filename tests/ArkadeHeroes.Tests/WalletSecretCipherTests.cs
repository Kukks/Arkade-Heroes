using System.Security.Cryptography;
using ArkadeHeroes.Chain.NArk;

namespace ArkadeHeroes.Tests;

/// <summary>
/// The passphrase-based mnemonic cipher: round-trips under the right passphrase,
/// fails loudly under the wrong one (authenticated encryption — no silent
/// garbage), and passes plaintext through so encrypted and unencrypted wallets
/// coexist.
/// </summary>
public class WalletSecretCipherTests
{
    private const string Mnemonic = "legal winner thank year wave sausage worth useful legal winner thank yellow";

    [Fact]
    public void Encrypt_then_Decrypt_round_trips()
    {
        var token = WalletSecretCipher.Encrypt(Mnemonic, "correct horse battery staple");
        Assert.True(WalletSecretCipher.IsEncrypted(token));
        Assert.DoesNotContain("sausage", token); // the plaintext is not visible in the token
        Assert.Equal(Mnemonic, WalletSecretCipher.Decrypt(token, "correct horse battery staple"));
    }

    [Fact]
    public void Wrong_passphrase_is_rejected()
    {
        var token = WalletSecretCipher.Encrypt(Mnemonic, "right-pass");
        Assert.Throws<CryptographicException>(() => WalletSecretCipher.Decrypt(token, "wrong-pass"));
    }

    [Fact]
    public void Encrypting_twice_yields_different_tokens_but_same_plaintext()
    {
        // Fresh salt + nonce each time → distinct ciphertext (no ECB-style leak).
        var a = WalletSecretCipher.Encrypt(Mnemonic, "pw");
        var b = WalletSecretCipher.Encrypt(Mnemonic, "pw");
        Assert.NotEqual(a, b);
        Assert.Equal(Mnemonic, WalletSecretCipher.Decrypt(a, "pw"));
        Assert.Equal(Mnemonic, WalletSecretCipher.Decrypt(b, "pw"));
    }

    [Fact]
    public void Plaintext_passes_through_untouched()
    {
        // A plaintext (non-token) secret is recognised and returned as-is, so a
        // legacy unencrypted wallet still opens (with or without a passphrase set).
        Assert.False(WalletSecretCipher.IsEncrypted(Mnemonic));
        Assert.Equal(Mnemonic, WalletSecretCipher.Decrypt(Mnemonic, "pw"));
        Assert.Equal(Mnemonic, WalletSecretCipher.Decrypt(Mnemonic, ""));
    }

    [Fact]
    public void Already_encrypted_is_not_double_wrapped()
    {
        var token = WalletSecretCipher.Encrypt(Mnemonic, "pw");
        Assert.Equal(token, WalletSecretCipher.Encrypt(token, "pw"));
    }

    [Fact]
    public void Encrypted_secret_without_passphrase_refuses_to_open()
    {
        var token = WalletSecretCipher.Encrypt(Mnemonic, "pw");
        Assert.Throws<InvalidOperationException>(() => WalletSecretCipher.Decrypt(token, ""));
    }
}
