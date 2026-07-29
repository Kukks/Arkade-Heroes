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
/// </summary>
public class TermsState(IJSRuntime js)
{
    private const string CacheKey = "ah:terms-accepted";

    /// <summary>The version cached from a previous acceptance in this browser (null = nothing cached).</summary>
    public int? CachedVersion { get; private set; }

    /// <summary>
    /// What to claim on a registration request: the version this build actually showed the player, or null
    /// if they haven't accepted it. Deliberately not <see cref="CachedVersion"/> raw — a cache written by a
    /// NEWER build than the server knows about would be refused as a version from the future, locking a
    /// rolled-back client out of registering entirely. The honest claim is the one we displayed.
    /// </summary>
    public int? VersionToRecord => Terms.Satisfies(CachedVersion) ? Terms.CurrentVersion : null;

    /// <summary>True while the acceptance prompt is open and waiting on the player.</summary>
    public bool Prompting => _pending is not null;

    /// <summary>Fired when the prompt opens or closes, or a version is recorded.</summary>
    public event Action? OnChange;

    private TaskCompletionSource<bool>? _pending;

    /// <summary>
    /// True when <paramref name="player"/> still has to accept before playing. Signed-in players are judged
    /// on the server's record; a signed-out browser falls back to the local cache.
    /// </summary>
    public bool MustAccept(PlayerDto? player) => player is not null
        ? !Terms.Satisfies(player.TermsAcceptedVersion)
        : !Terms.Satisfies(CachedVersion);

    /// <summary>Re-read the cached acceptance from browser storage. Call once, at startup.</summary>
    public void Hydrate()
    {
        try
        {
            var stored = ((IJSInProcessRuntime)js).Invoke<string?>("localStorage.getItem", CacheKey);
            if (int.TryParse(stored, out var version)) CachedVersion = version;
        }
        catch { /* JS unavailable — no cache, so the gate simply asks again (the safe direction) */ }
    }

    /// <summary>
    /// Opens the acceptance prompt and completes when the player answers: true if they accepted, false if
    /// they declined. Returns immediately when nothing needs accepting. A player who declines is not
    /// signed up and nothing irreversible has happened — they simply do not play.
    /// </summary>
    public Task<bool> RequestAcceptanceAsync(PlayerDto? player)
    {
        if (!MustAccept(player)) return Task.FromResult(true);
        // A second caller joins the prompt already on screen rather than stacking another one.
        _pending ??= new TaskCompletionSource<bool>();
        OnChange?.Invoke();
        return _pending.Task;
    }

    /// <summary>The player accepted: cache the version locally and release anyone awaiting the prompt.
    /// The caller is responsible for the durable half — recording it against the player on the server.</summary>
    public void Accepted(int version)
    {
        CachedVersion = CachedVersion is int have && have > version ? have : version;
        try { ((IJSInProcessRuntime)js).InvokeVoid("localStorage.setItem", CacheKey, CachedVersion.ToString()); }
        catch { /* JS unavailable — degrade to in-memory for this tab; the server record still stands */ }
        Resolve(true);
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
