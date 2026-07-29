using System.Security.Cryptography;
using System.Text;

namespace ArkadeHeroes.Server;

/// <summary>
/// The operator console's authentication: ONE shared secret, configured out of band as
/// <c>Game:AdminToken</c> (<c>Game__AdminToken</c> in the environment) and supplied on every request under
/// <c>/api/admin</c> in the <see cref="Shared.AdminApiContract.TokenHeader"/> header.
///
/// FAIL CLOSED, TWICE. With the secret unset, <c>Program</c> does not map the admin group at all — the
/// routes do not exist and every one of them 404s, so there is no handler for a bug to leave reachable.
/// <see cref="Matches"/> then refuses an unset secret a second time on its own, so the surface cannot be
/// opened by an empty string even if it were ever mapped by mistake. "Not configured" must mean OFF, never
/// "no password" — this server holds real bitcoin and the admin surface is the highest-value target in it.
///
/// The secret is never logged, never put in a URL, and never returned by any endpoint. A refused request is
/// logged as the FACT of a refusal and its path only: a rejected guess is still a secret-shaped string, and
/// a log full of them is a dictionary.
/// </summary>
public static class AdminGate
{
    /// <summary>Whether the admin surface is configured at all. Whitespace counts as unset — a secret made
    /// entirely of spaces is a configuration accident, and treating it as a live password would be the one
    /// direction that fails open.</summary>
    public static bool IsEnabled(string? configured) => !string.IsNullOrWhiteSpace(configured);

    /// <summary>
    /// Constant-time comparison of the supplied token against the configured one.
    ///
    /// Both sides are hashed to a fixed 32 bytes FIRST, then compared with
    /// <see cref="CryptographicOperations.FixedTimeEquals"/>. Hashing is what makes the comparison
    /// LENGTH-independent: FixedTimeEquals on the raw strings returns immediately when the lengths differ,
    /// which hands an attacker the secret's length for free and turns guessing it into a much smaller
    /// search. Over the digests, every wrong guess — wrong length or wrong content — costs the same.
    /// </summary>
    public static bool Matches(string? configured, string? supplied)
    {
        // The second refusal. Unreachable while Program only maps the group when IsEnabled, and kept
        // anyway so this method is safe to call from anywhere without re-deriving that guarantee.
        if (!IsEnabled(configured)) return false;

        // No early return on a missing header: an absent token takes the same path as a wrong one.
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(configured!)),
            SHA256.HashData(Encoding.UTF8.GetBytes(supplied ?? "")));
    }
}
