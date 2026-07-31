using Microsoft.JSInterop;

namespace ArkadeHeroes.Web.Components;

/// <summary>
/// Whether the player has asked their system to suppress motion.
///
/// <para>CSS answers this for anything that animates. This exists for the part of a cinematic that is made
/// of WAITING rather than moving — the wave-by-wave dungeon crawls, which are paced with awaits in C#. A
/// reduced-motion player who still had to sit through those would get all of the delay and none of the
/// payoff, so they skip straight to the resolved state instead.</para>
///
/// <para>Asked once and cached: the query can change under you (an OS setting flipped mid-session), but
/// re-asking costs an interop hop per beat of an animation loop to catch a case a page reload already
/// fixes. A browser that cannot answer gets the animated path — the same default the stylesheet gives it.</para>
/// </summary>
public static class Motion
{
    private static Task<IJSObjectReference>? _module;
    private static bool? _reduced;

    public static async ValueTask<bool> PrefersReducedAsync(IJSRuntime js)
    {
        if (_reduced is bool known) return known;
        try
        {
            _module ??= js.InvokeAsync<IJSObjectReference>("import", "./js/motion.js").AsTask();
            _reduced = await (await _module).InvokeAsync<bool>("prefersReducedMotion");
        }
        catch
        {
            // A blocked module load or a browser without matchMedia is not a reason to change how the page
            // behaves — fall back to "motion is fine", and stop asking.
            _reduced = false;
        }
        return _reduced.Value;
    }
}
