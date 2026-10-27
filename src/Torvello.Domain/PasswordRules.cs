namespace Torvello.Domain;

/// <summary>
/// Password policy shared with the desktop till (src/shared/auth-rules.ts there).
/// Keep both in step when changing the rules.
/// </summary>
public static class PasswordRules
{
    public const int MinLength = 8;
    public const int MaxLength = 128;

    private static readonly HashSet<string> CommonPasswords = new(StringComparer.OrdinalIgnoreCase)
    {
        "password1", "password123", "12345678a", "qwerty123", "abc12345", "torvello1", "torvello123", "torvello@2026",
    };

    /// <summary>Returns why the password is not acceptable, or null when it is fine.</summary>
    public static string? Problem(string password, string? email = null)
    {
        if (password.Length < MinLength) return $"Password must be at least {MinLength} characters";
        if (password.Length > MaxLength) return "Password is too long";
        if (!password.Any(char.IsAsciiLetter) || !password.Any(char.IsAsciiDigit)) return "Use both letters and numbers";
        if (CommonPasswords.Contains(password)) return "That password is too common";

        var name = email?.Split('@')[0] ?? "";
        if (name.Length >= 3 && password.Contains(name, StringComparison.OrdinalIgnoreCase))
        {
            return "Password must not contain your email name";
        }
        return null;
    }
}
