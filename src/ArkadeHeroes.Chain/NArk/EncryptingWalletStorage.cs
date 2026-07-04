using NArk.Abstractions.Wallets;

namespace ArkadeHeroes.Chain.NArk;

/// <summary>
/// An <see cref="IWalletStorage"/> decorator that keeps the wallet secret (the
/// BIP-39 mnemonic) encrypted AT REST: it encrypts the <see cref="ArkWalletInfo.Secret"/>
/// on every write and decrypts it on every read, so the inner store (the SQLite
/// wallet DB) only ever holds ciphertext while NArk's signer still receives the
/// plaintext mnemonic it needs at runtime — transparent to the rest of the SDK.
/// Only opt-in: it is wired in solely when a passphrase is supplied
/// (<see cref="SelfCustodyWalletOptions.Passphrase"/>); without one the plaintext
/// <see cref="EfCoreWalletStorage"/> registration stays in place unchanged.
/// </summary>
public sealed class EncryptingWalletStorage : IWalletStorage
{
    private readonly IWalletStorage _inner;
    private readonly string _passphrase;

    public EncryptingWalletStorage(IWalletStorage inner, string passphrase)
    {
        _inner = inner;
        _passphrase = passphrase;
        // Re-raise the inner store's events with the plaintext secret, so any
        // subscriber sees the same view a direct read would return.
        _inner.WalletSaved += (_, wallet) => WalletSaved?.Invoke(this, Decrypt(wallet));
        _inner.WalletDeleted += (_, id) => WalletDeleted?.Invoke(this, id);
    }

    public event EventHandler<ArkWalletInfo>? WalletSaved;
    public event EventHandler<string>? WalletDeleted;

    private ArkWalletInfo Encrypt(ArkWalletInfo w) =>
        string.IsNullOrEmpty(w.Secret) ? w : w with { Secret = WalletSecretCipher.Encrypt(w.Secret, _passphrase) };

    private ArkWalletInfo Decrypt(ArkWalletInfo w) =>
        string.IsNullOrEmpty(w.Secret) ? w : w with { Secret = WalletSecretCipher.Decrypt(w.Secret, _passphrase) };

    // ── Reads: decrypt the secret back to plaintext ────────────────────

    public async Task<ArkWalletInfo> LoadWallet(string walletIdentifierOrFingerprint, CancellationToken ct = default)
        => Decrypt(await _inner.LoadWallet(walletIdentifierOrFingerprint, ct));

    public async Task<IReadOnlySet<ArkWalletInfo>> LoadAllWallets(CancellationToken ct = default)
        => (await _inner.LoadAllWallets(ct)).Select(Decrypt).ToHashSet();

    public async Task<ArkWalletInfo?> GetWalletById(string walletId, CancellationToken ct = default)
        => await _inner.GetWalletById(walletId, ct) is { } w ? Decrypt(w) : null;

    public async Task<IReadOnlyList<ArkWalletInfo>> GetWalletsByIds(IEnumerable<string> walletIds, CancellationToken ct = default)
        => (await _inner.GetWalletsByIds(walletIds, ct)).Select(Decrypt).ToList();

    // ── Writes: encrypt the secret before it hits the DB ───────────────

    public Task SaveWallet(ArkWalletInfo wallet, CancellationToken ct = default)
        => _inner.SaveWallet(Encrypt(wallet), ct);

    public Task<bool> UpsertWallet(ArkWalletInfo wallet, bool updateIfExists = true, CancellationToken ct = default)
        => _inner.UpsertWallet(Encrypt(wallet), updateIfExists, ct);

    // ── Pass-through: these never carry the secret ─────────────────────

    public Task UpdateLastUsedIndex(string walletId, int lastUsedIndex, CancellationToken ct = default)
        => _inner.UpdateLastUsedIndex(walletId, lastUsedIndex, ct);

    public Task<bool> DeleteWallet(string walletId, CancellationToken ct = default)
        => _inner.DeleteWallet(walletId, ct);

    public Task UpdateDestination(string walletId, string? destination, CancellationToken ct = default)
        => _inner.UpdateDestination(walletId, destination, ct);

    public Task SetMetadataValue(string walletId, string key, string? value, CancellationToken ct = default)
        => _inner.SetMetadataValue(walletId, key, value, ct);
}
