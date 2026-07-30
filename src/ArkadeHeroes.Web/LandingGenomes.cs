namespace ArkadeHeroes.Web;

/// <summary>
/// The genomes the landing page draws, in one place so the showcase art and the codex explainer
/// cannot drift apart.
///
/// <para>The two parents are arbitrary but fixed. Everything derived from them is the literal
/// output of the game's own Core functions for <see cref="Entropy"/>, obtained by running them —
/// not hex chosen to look plausible. ArkadeHeroes.Web does not reference ArkadeHeroes.Core, so
/// they are pasted here rather than computed at render time, and
/// <c>LandingGenomeTests</c> re-derives every one of them from Core on each build so a change to
/// the mixer, the fusion rule or the absorb odds fails the suite instead of quietly turning the
/// landing page into a lie.</para>
/// </summary>
internal static class LandingGenomes
{
    /// <summary>Parent A, and the fusion base.</summary>
    public const string Ember = "a3f21c05d84b6790e2c1358fa07d4b621a2b3c4d5e6f708190a1b2c3d4e5f607";

    /// <summary>Parent B, and the death-match winner.</summary>
    public const string Tide = "5e7d19c4a2360f8b71d3e50629ac8f1422334455667788990011aabbccddeeff";

    /// <summary>A third creature, showcase art only — nothing is derived from it.</summary>
    public const string Glimmer = "0c4a8e26f1b95d3708e2a6c41d7f0b53f0e1d2c3b4a5968778695a4b3c2d1e0f";

    /// <summary>
    /// Shared breeding/fusion entropy. Chosen (by scanning seeds) so the child demonstrates all
    /// three inheritance rules at once: traits straight from a parent, one recessive surfaced,
    /// and exactly one fresh mutation.
    /// </summary>
    public const string Entropy = "2594b6a92ebfb1c3312deb7d01c015fb95e9fbe9bd7bc6b527af07813ec7b910";

    /// <summary><c>GeneMixer.Mix(Ember, Tide, Entropy)</c>.</summary>
    public const string Child = "5ef21cc4d8360f8b71d3e506297d8f0e22333c556677709990a1c3c31be5f607";

    /// <summary><c>Fusion.Fuse(Ember, Tide, Entropy)</c> — base Ember, sacrifice Tide.</summary>
    public const string Fused = "a3f21c05d84b6790e2c1358fa07d4b621a1a3c3c5e6f70880011b2c3d4ccf6ff";

    /// <summary>Entropy for the absorb roll — the first seed on which the roll actually fires.</summary>
    public const string AbsorbEntropy = "9d9f290527a6be626a8f5985b26e19b237b44872b03631811df4416fc1713178";

    /// <summary><c>Absorb.Resolve(Tide, Ember, AbsorbEntropy, AbsorbOdds.Default)</c> — winner Tide, loser Ember.</summary>
    public const string Absorbed = "5e7d19c4a2360f8b71d3e50629ac8f1422334455667788990011aabbccddf6ff";
}
