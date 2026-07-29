namespace ArkadeHeroes.Shared;

/// <summary>
/// The Terms of Use a player must EXPLICITLY accept before they can play. This game stakes real bitcoin and
/// permanently destroys assets — a death-match burns the loser, a fusion burns both inputs, and a lost
/// recovery phrase is gone for good. The terms document is the only place a player is told that, so the
/// acceptance has to be a deliberate act, recorded against the player, and re-asked when the document changes.
///
/// Shared by the server (which records the accepted version against the player row) and the browser (which
/// decides whether to prompt), so both sides agree on what "current" means at compile time.
/// </summary>
public static class Terms
{
    /// <summary>
    /// The version of <see cref="DocumentPath"/> currently in force.
    ///
    /// ▲ BUMP THIS WHENEVER docs/TERMS.md CHANGES MATERIALLY. ▲
    ///
    /// Every player whose recorded acceptance is older than this is re-prompted the next time they open the
    /// game, so bumping it is how a changed document gets re-agreed. Cosmetic edits (a typo, a heading) do
    /// not need a bump; anything that changes what a player is agreeing to does.
    ///
    /// This is deliberately a hand-maintained constant rather than a hash of the file: the browser build is
    /// Blazor WASM and cannot hash a repo file at runtime, and a content hash would also re-prompt every
    /// player over a fixed typo.
    /// </summary>
    public const int CurrentVersion = 1;

    /// <summary>The canonical document, versioned in the repo alongside this constant.</summary>
    public const string DocumentPath = "docs/TERMS.md";

    /// <summary>
    /// Where the browser build serves its bundled copy of <see cref="DocumentPath"/> from, so the terms are
    /// readable inside the acceptance UI without navigating away. The Web project copies the document here at
    /// build time when it is present.
    /// </summary>
    public const string BundledDocumentPath = "terms.md";

    /// <summary>
    /// True when an acceptance recorded at <paramref name="acceptedVersion"/> still covers
    /// <paramref name="currentVersion"/>. Takes the current version explicitly so the re-prompt rule is
    /// testable against a bumped document without editing the constant.
    /// </summary>
    public static bool Satisfies(int? acceptedVersion, int currentVersion) =>
        acceptedVersion is int accepted && accepted >= currentVersion;

    /// <summary>True when this acceptance covers <see cref="CurrentVersion"/> — i.e. do NOT prompt.</summary>
    public static bool Satisfies(int? acceptedVersion) => Satisfies(acceptedVersion, CurrentVersion);

    /// <summary>
    /// A version a player may legitimately claim to have accepted: a real version that already exists.
    /// Zero (the shape a missing field deserialises into), negatives, and versions from the future are all
    /// refused rather than recorded — a stored "accepted v9999" would silently satisfy every future bump.
    /// </summary>
    public static bool IsAcceptableVersion(int version) => version > 0 && version <= CurrentVersion;
}
