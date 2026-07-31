using Microsoft.JSInterop;

namespace ArkadeHeroes.Web.Wallet;

/// <summary>
/// Puts the wallet's recovery phrase in the browser's credential manager, so the thing that IS the account
/// travels with the player instead of living in one tab's storage.
///
/// <para>The wallet is already the account — registration binds this wallet's login key to the player and
/// signing a challenge is how they come back. What was missing is portability: the key lived in browser
/// storage, so another device was another account. Saving the phrase where the browser keeps passwords
/// closes that, and it does it through machinery the player already understands.</para>
///
/// <para>It is worth being blunt about the trade, because this is a wallet holding real bitcoin: a synced
/// password manager means the phrase reaches wherever that account reaches. That is the point — and it is
/// also the risk. The UI says so rather than presenting this as free safety, and the phrase is still shown
/// for manual backup, because this is an addition to writing it down, not a replacement for it.</para>
/// </summary>
public class WalletCredentialStore(IJSRuntime js, GameWallet wallet)
{
    private IJSObjectReference? _module;

    private async Task<IJSObjectReference> ModuleAsync() =>
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/wallet-credentials.js");

    /// <summary>What the browser's chooser shows next to the entry.</summary>
    private const string CredentialLabel = "Arkade Heroes recovery phrase";

    /// <summary>
    /// True when this browser can do it at all. Chromium-only and secure-context only, so on Firefox,
    /// Safari, or plain http this is false and the caller shows nothing rather than a button that fails.
    /// </summary>
    public async Task<bool> IsSupportedAsync()
    {
        try { return await (await ModuleAsync()).InvokeAsync<bool>("isSupported"); }
        catch { return false; }   // no JS, no module, no feature — all the same answer to the caller
    }

    /// <summary>What a save attempt actually achieved. See the module's <c>save</c> for why "accepted" and
    /// "confirmed" are different answers rather than both being success.</summary>
    public enum SaveOutcome { Confirmed, Accepted, Refused, Unsupported, NoPhrase }

    /// <summary>
    /// Saves the active wallet's phrase and reports what can actually be established.
    ///
    /// <para>Deliberately not a bool. The browser resolving the store call does not mean a credential
    /// exists, and Chrome often saves with no prompt at all — so "it didn't throw" and "your keys are
    /// backed up" are different claims, and only the second one is worth telling a player.</para>
    /// </summary>
    public async Task<SaveOutcome> SaveAsync(string walletId)
    {
        if (await wallet.GetMnemonicAsync(walletId) is not { Length: > 0 } phrase) return SaveOutcome.NoPhrase;
        try
        {
            // Keyed by wallet id so a second wallet cannot silently overwrite the first's phrase — on a
            // non-custodial wallet that would be losing the only copy of someone's keys.
            var status = await (await ModuleAsync())
                .InvokeAsync<string>("save", walletId, phrase, CredentialLabel);
            return status switch
            {
                "confirmed" => SaveOutcome.Confirmed,
                "accepted" => SaveOutcome.Accepted,
                "unsupported" => SaveOutcome.Unsupported,
                _ => SaveOutcome.Refused,
            };
        }
        catch { return SaveOutcome.Refused; }
    }

    /// <summary>
    /// Asks the player to pick a saved phrase and returns it, or null if they dismiss the chooser or the
    /// browser has nothing stored. The caller imports it — this class never touches wallet state itself.
    /// </summary>
    public async Task<string?> LoadPhraseAsync()
    {
        try
        {
            var found = await (await ModuleAsync()).InvokeAsync<StoredCredential?>("load");
            return found?.Secret is { Length: > 0 } s ? s : null;
        }
        catch { return null; }
    }

    private sealed record StoredCredential(string Id, string Secret);
}
