// Saving the wallet's recovery phrase into the browser's own credential manager, so it rides the
// password manager the player already trusts (and, if they sync it, follows them to their other devices).
//
// This is the Credential Management API's PasswordCredential, which is Chromium-only and secure-context
// only — Firefox and Safari have no PasswordCredential at all. Every function here therefore feature-tests
// first and degrades to "not available" rather than throwing: the wallet must keep working in a browser
// that cannot do this, because the phrase is still shown for manual backup either way.
//
// The C# side (WalletCredentialStore) invokes these by name. There is no compiler between the two, so a
// rename here silently breaks the interop at runtime — WalletCredentialInteropTests pins the names.

export function isSupported() {
    return typeof window !== 'undefined'
        && window.isSecureContext === true
        && typeof window.PasswordCredential === 'function'
        && !!(navigator.credentials && navigator.credentials.store && navigator.credentials.get);
}

/// Stores one phrase under `id` and reports what actually happened, as one of:
///   'confirmed'   — stored, and read back to prove it
///   'accepted'    — the browser took the request but wouldn't confirm; may have saved silently
///   'refused'     — the call threw (blocked, dismissed, or not permitted here)
///   'unsupported' — no PasswordCredential in this browser
///
/// The distinction matters. `navigator.credentials.store()` resolves when the browser ACCEPTS THE REQUEST,
/// not when a credential exists — and Chrome frequently stores with no visible prompt at all. So a resolved
/// promise alone proves nothing, and reporting "saved" on it is how you tell someone their keys are backed
/// up when they might not be. The read-back is the only part that actually establishes anything.
export async function save(id, secret, name) {
    if (!isSupported()) return 'unsupported';
    try {
        await navigator.credentials.store(new PasswordCredential({ id, password: secret, name }));
    } catch {
        return 'refused';
    }
    try {
        // 'silent' so confirming costs the player no extra dialog. A null here is INCONCLUSIVE — some
        // browsers require mediation before handing anything back — so it must not be reported as failure.
        const found = await navigator.credentials.get({ password: true, mediation: 'silent' });
        if (found && found.id === id) return 'confirmed';
    } catch {
        // Silent read not permitted. Still inconclusive.
    }
    return 'accepted';
}

/// Asks the player to pick a saved phrase. `mediation: 'required'` always shows the chooser, which is what
/// an explicit "restore" button should do — silently adopting a stored wallet would be a surprising way to
/// change which keys are live.
export async function load() {
    if (!isSupported()) return null;
    try {
        const cred = await navigator.credentials.get({ password: true, mediation: 'required' });
        if (!cred || !cred.password) return null;
        return { id: cred.id, secret: cred.password };
    } catch {
        return null;
    }
}
