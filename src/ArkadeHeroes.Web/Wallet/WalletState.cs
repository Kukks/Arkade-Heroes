using ArkadeHeroes.Shared;
using Microsoft.JSInterop;
using NArk.Abstractions.Contracts;
using NArk.Abstractions.VTXOs;

namespace ArkadeHeroes.Web.Wallet;

/// <summary>
/// The active wallet's shared, in-tab session state — which wallet is loaded and its
/// current balance. Subscribes to the SDK's storage-change events so the HUD refreshes
/// automatically when the background sync writes new VTXOs. Mirrors the NArk sample's
/// WalletState. Singleton: WASM is single-threaded, so no locking is needed.
/// </summary>
public class WalletState : IDisposable
{
    private readonly IVtxoStorage _vtxoStorage;
    private readonly IContractStorage _contractStorage;
    private readonly IJSRuntime _js;
    private const string BackupPendingKey = "ah:backup-pending";

    public string? ActiveWalletId { get; private set; }
    public string? ActiveAddress { get; private set; }

    /// <summary>What a spend can actually reach right now, in sats — the number the HUD shows.</summary>
    public long BalanceSats { get; private set; }

    /// <summary>
    /// Sats the wallet holds that no spend can touch yet, because the coin hasn't settled into a batch.
    /// Shown alongside the balance rather than folded into it: the pill used to include these, so it
    /// promised money the very next action would refuse to spend.
    /// </summary>
    public long SettlingSats { get; private set; }

    /// <summary>The game player this wallet is signed in as, or null (wallet exists but no game session).</summary>
    public PlayerDto? Player { get; private set; }

    /// <summary>True once the wallet has proven its login key to the game server this session.</summary>
    public bool IsSignedIn => Player is not null;

    /// <summary>True once the player has a wallet loaded in this tab.</summary>
    public bool Connected => ActiveWalletId is not null;

    /// <summary>A truncated receive address for the HUD pill (empty until an address is known).</summary>
    public string ShortAddress => ActiveAddress is { Length: > 0 } a
        ? (a.Length <= 16 ? a : $"{a[..8]}…{a[^4..]}")
        : "";

    /// <summary>Fired whenever the active wallet or its balance changes, or the SDK syncs new VTXOs/contracts.</summary>
    public event Action? OnChange;

    private readonly GameWallet _wallet;
    private bool _balanceRefreshRunning;
    private bool _balanceRefreshWanted;

    public WalletState(IVtxoStorage vtxoStorage, IContractStorage contractStorage, IJSRuntime js, GameWallet wallet)
    {
        _vtxoStorage = vtxoStorage;
        _contractStorage = contractStorage;
        _js = js;
        _wallet = wallet;

        _vtxoStorage.VtxosChanged += OnVtxosChanged;
        _contractStorage.ContractsChanged += OnContractsChanged;
    }

    /// <summary>
    /// Recomputes the balance from the coins actually on hand.
    ///
    /// <para>Incoming sats used to redraw the HUD without changing the number on it: the storage event fired
    /// <see cref="OnChange"/>, every subscriber re-rendered, and all of them read the same stale
    /// <see cref="BalanceSats"/> — because nothing recomputed it. Navigating did, which is why a page change
    /// "fixed" it and made the bug look like a rendering problem instead of a missing read.</para>
    /// </summary>
    public async Task RefreshBalanceAsync()
    {
        if (ActiveWalletId is not { } id) return;

        // Coalesce: a settling spend can raise VtxosChanged several times in a row, and each refresh is a
        // storage read. Run one, remember that more arrived, and do a single catch-up pass at the end.
        if (_balanceRefreshRunning) { _balanceRefreshWanted = true; return; }
        _balanceRefreshRunning = true;
        try
        {
            do
            {
                _balanceRefreshWanted = false;
                var (sats, settling) = await _wallet.GetBalanceBreakdownAsync(id);
                if (sats != BalanceSats || settling != SettlingSats)
                    UpdateBalance(sats, settling);   // UpdateBalance raises OnChange
            }
            while (_balanceRefreshWanted);
        }
        catch { /* a failed read leaves the last known balance rather than blanking the HUD */ }
        finally { _balanceRefreshRunning = false; }
    }

    public void SetActiveWallet(string? walletId, string? address = null)
    {
        ActiveWalletId = walletId;
        ActiveAddress = address;
        OnChange?.Invoke();
    }

    /// <summary>Sets the spendable balance, leaving the settling figure as last read.</summary>
    public void UpdateBalance(long sats) => UpdateBalance(sats, SettlingSats);

    public void UpdateBalance(long sats, long settling)
    {
        BalanceSats = sats;
        SettlingSats = settling;
        OnChange?.Invoke();
    }

    public void SetPlayer(PlayerDto? player)
    {
        Player = player;
        OnChange?.Invoke();
    }

    /// <summary>True when the wallet was auto-provisioned during "Play" onboarding and its recovery
    /// phrase hasn't been backed up yet — the shell nudges until the player backs up (non-custodial:
    /// the key exists, the backup step is just deferred off the critical path).</summary>
    public bool BackupPending { get; private set; }

    public void SetBackupPending(bool pending)
    {
        BackupPending = pending;
        // Persist per-wallet so the nudge survives a refresh until the phrase is actually saved —
        // a non-custodial wallet whose phrase is never backed up is one lost tab from gone.
        try
        {
            var js = (IJSInProcessRuntime)_js;
            if (pending && ActiveWalletId is not null)
                js.InvokeVoid("localStorage.setItem", BackupPendingKey, ActiveWalletId);
            else
                js.InvokeVoid("localStorage.removeItem", BackupPendingKey);
        }
        catch { /* JS unavailable — degrade to session-only (the pre-persistence behaviour) */ }
        OnChange?.Invoke();
    }

    /// <summary>Re-light the backup nudge on load if this wallet was flagged un-backed-up in a prior
    /// session. Call once the active wallet is known (the id must match what was stored).</summary>
    public void HydrateBackupPending()
    {
        try
        {
            var stored = ((IJSInProcessRuntime)_js).Invoke<string?>("localStorage.getItem", BackupPendingKey);
            if (stored is not null && stored == ActiveWalletId && !BackupPending)
            {
                BackupPending = true;
                OnChange?.Invoke();
            }
        }
        catch { /* JS unavailable — no restore */ }
    }

    public void NotifyChanged() => OnChange?.Invoke();

    private void OnVtxosChanged(object? sender, ArkVtxo vtxo)
    {
        OnChange?.Invoke();          // redraw now with what we have
        _ = RefreshBalanceAsync();   // then correct the number, which is what actually changed
    }
    private void OnContractsChanged(object? sender, ArkContractEntity contract) => OnChange?.Invoke();

    public void Dispose()
    {
        _vtxoStorage.VtxosChanged -= OnVtxosChanged;
        _contractStorage.ContractsChanged -= OnContractsChanged;
    }
}
