using ArkadeHeroes.Chain.NArk;

namespace ArkadeHeroes.Tests;

/// <summary>
/// BIP39 recovery-phrase validation behind the client's `restore` / `import`: a
/// typo'd phrase is rejected up front with a clear reason instead of failing deep
/// in wallet creation.
/// </summary>
public class WalletMnemonicTests
{
    // The canonical zero-entropy BIP39 vector (11× abandon + about) is checksum-valid.
    private const string Valid =
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    [Fact]
    public void ValidPhrase_PassesValidation() =>
        Assert.Null(SelfCustodyWallet.ValidateMnemonic(Valid));

    [Fact]
    public void WrongWordCount_IsRejected()
    {
        var reason = SelfCustodyWallet.ValidateMnemonic("abandon abandon about");
        Assert.NotNull(reason);
        Assert.Contains("12 or 24", reason);
    }

    [Fact]
    public void UnknownWord_IsRejected() =>
        // 'abandonn' is not in the BIP39 wordlist.
        Assert.NotNull(SelfCustodyWallet.ValidateMnemonic(
            "abandonn abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about"));

    [Fact]
    public void ValidWordsBadChecksum_IsRejected()
    {
        // 12× 'abandon' (ends in abandon, not about): all valid words, invalid checksum.
        var reason = SelfCustodyWallet.ValidateMnemonic(
            "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon");
        Assert.NotNull(reason);
        Assert.Contains("checksum", reason);
    }
}
