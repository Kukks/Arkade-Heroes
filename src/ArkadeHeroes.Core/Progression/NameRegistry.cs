namespace ArkadeHeroes.Core.Progression;

/// <summary>
/// Rules for a player-claimed hero name — the unique-name registry sink. A custom name is a pure display
/// string (game state, not on-chain), but claiming one costs sats to the treasury and must be globally
/// unique. This validates the FORMAT (length + charset) and normalizes it (trim); GLOBAL uniqueness is
/// enforced server-side against the live roster (which this pure Core rule can't see).
/// </summary>
public static class NameRegistry
{
    public const int MinLength = 3;
    public const int MaxLength = 24;

    /// <summary>Returns a validation error, or null if the trimmed name is a legal claim; also outputs the normalized name.</summary>
    public static string? Validate(string? raw, out string normalized)
    {
        normalized = (raw ?? string.Empty).Trim();
        if (normalized.Length < MinLength) return $"A name must be at least {MinLength} characters.";
        if (normalized.Length > MaxLength) return $"A name must be at most {MaxLength} characters.";
        if (normalized.Contains("  ")) return "A name may not contain double spaces.";
        foreach (var ch in normalized)
            if (!char.IsLetterOrDigit(ch) && ch != ' ' && ch != '-' && ch != '\'' && ch != '.')
                return "A name may use only letters, digits, spaces, and - ' . characters.";
        return null;
    }
}
