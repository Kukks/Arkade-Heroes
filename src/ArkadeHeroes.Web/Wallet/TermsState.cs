using ArkadeHeroes.Shared;
using Microsoft.JSInterop;

namespace ArkadeHeroes.Web.Wallet;

/// <summary>
/// Decides whether the player still has to accept the Terms of Use, and carries the pending prompt while
/// they do. Singleton, like <see cref="WalletState"/>: WASM is single-threaded, so no locking is needed.
///
/// The SERVER's record on the player row is the source of truth. Local storage is only a cache, so a
/// returning player isn't re-asked on every page load — it never overrules the server, because it lives on
/// the player's own machine and survives nothing. Concretely:
///
///   • signed in  → the answer comes from <see cref="PlayerDto.TermsAcceptedVersion"/>, full stop. A cleared
///                  cache does not re-ask a question already answered, and a stale cache cannot suppress one
///                  the server says is still open (e.g. the acceptance POST never landed).
///   • signed out → the cache stands in, because there is no player row to ask about yet. This is the
///                  brand-new player who has accepted but whose wallet and registration do not exist yet.
///
/// The cache is keyed PER WALLET, and only an acceptance collected in THIS session is ever claimed to the
/// server. Both exist for the same reason: one browser can hold different players over time (import a
/// different recovery phrase, or point at a server whose database was reset), and an origin-wide "somebody
/// accepted once" flag would otherwise let one human's acceptance be recorded against another player who
/// was shown nothing — a durable row that reads as evidence but isn't.
/// </summary>
public class TermsState(IJSRuntime js)
{
    private const string CacheKeyPrefix = "ah:terms-accepted:";

    private string? _walletId;
    private int? _cachedVersion;

    /// <summary>The version cached for the CURRENT wallet (null = nothing cached, or no wallet yet).</summary>
    public int? CachedVersion => _cachedVersion;

    /// <summary>
    /// True when the player answered the prompt during this session — i.e. we actually put the terms in
    /// front of THIS human, just now. Never restored from storage: it is the difference between "somebody
    /// using this browser once agreed" and "the person sitting here agreed".
    /// </summary>
    public bool AcceptedThisSession { get; private set; }

    /// <summary>
    /// What to claim on a registration request. Non-null ONLY when the acceptance was collected in this
    /// session: a cached flag is good enough to skip re-asking, but not to assert to the server that a
    /// brand-new player agreed to anything. Reports <see cref="Terms.CurrentVersion"/> rather than the raw
    /// cache so a client newer than the server can't send a version the server would reject as
    /// from-the-future, locking registration out entirely.
    /// </summary>
    public int? VersionToRecord => AcceptedThisSession ? Terms.CurrentVersion : null;

    /// <summary>True while the acceptance prompt is open and waiting on the player.</summary>
    public bool Prompting => _pending is not null;

    /// <summary>Fired when the prompt opens or closes, or a version is recorded.</summary>
    public event Action? OnChange;

    private TaskCompletionSource<bool>? _pending;

    /// <summary>
    /// True when <paramref name="player"/> still has to accept before playing. Signed-in players are judged
    /// on the server's record; a signed-out browser falls back to this session's answer, then to the cache
    /// for the wallet currently loaded.
    /// </summary>
    public bool MustAccept(PlayerDto? player) => player is not null
        ? !Terms.Satisfies(player.TermsAcceptedVersion)
        : !(AcceptedThisSession || Terms.Satisfies(_cachedVersion));

    /// <summary>
    /// Point the cache at a wallet and read back what that wallet accepted. Call whenever the active wallet
    /// becomes known or changes — a different wallet is a different player, so it starts from no cache and
    /// gets asked, rather than inheriting the previous one's answer.
    /// </summary>
    public void HydrateFor(string? walletId)
    {
        _walletId = walletId;
        _cachedVersion = null;
        if (walletId is null) { OnChange?.Invoke(); return; }
        try
        {
            var stored = ((IJSInProcessRuntime)js).Invoke<string?>("localStorage.getItem", CacheKeyPrefix + walletId);
            if (int.TryParse(stored, out var version)) _cachedVersion = version;
        }
        catch { /* JS unavailable — no cache, so the gate simply asks again (the safe direction) */ }
        OnChange?.Invoke();
    }

    /// <summary>
    /// Opens the acceptance prompt and completes when the player answers: true if they accepted, false if
    /// they declined. Returns immediately when nothing needs accepting. A player who declines is not
    /// signed up and nothing irreversible has happened — they simply do not play.
    /// </summary>
    public Task<bool> RequestAcceptanceAsync(PlayerDto? player) =>
        MustAccept(player) ? RequestAcceptanceAsync() : Task.FromResult(true);

    /// <summary>
    /// Asks unless the player already answered in THIS session. Used once a player record exists and the
    /// SERVER says its acceptance is stale — at which point the cache must not be allowed to speak for
    /// them, only their own answer a moment ago may.
    /// </summary>
    public Task<bool> RequestAcceptanceAsync()
    {
        if (AcceptedThisSession) return Task.FromResult(true);
        // A second caller joins the prompt already on screen rather than stacking another one.
        _pending ??= new TaskCompletionSource<bool>();
        OnChange?.Invoke();
        return _pending.Task;
    }

    /// <summary>The player accepted: remember it for this session, cache it against the wallet if one is
    /// loaded, and release anyone awaiting the prompt. The caller is responsible for the durable half —
    /// recording it against the player on the server.</summary>
    public void Accepted(int version)
    {
        AcceptedThisSession = true;
        _cachedVersion = _cachedVersion is int have && have > version ? have : version;
        Persist();
        Resolve(true);
    }

    /// <summary>
    /// Attach this session's acceptance to a wallet that did not exist when it was given — the brand-new
    /// player accepts BEFORE any wallet is provisioned, so there was no key to cache it under at the time.
    /// </summary>
    public void AttachToWallet(string walletId)
    {
        _walletId = walletId;
        if (AcceptedThisSession) Persist();
    }

    private void Persist()
    {
        if (_walletId is null || _cachedVersion is not int version) return;
        try { ((IJSInProcessRuntime)js).InvokeVoid("localStorage.setItem", CacheKeyPrefix + _walletId, version.ToString()); }
        catch { /* JS unavailable — degrade to in-memory for this tab; the server record still stands */ }
    }

    /// <summary>The player declined. Nothing is recorded and nothing was created — they just don't play.</summary>
    public void Declined() => Resolve(false);

    private void Resolve(bool accepted)
    {
        var pending = _pending;
        _pending = null;
        pending?.TrySetResult(accepted);
        OnChange?.Invoke();
    }
}
