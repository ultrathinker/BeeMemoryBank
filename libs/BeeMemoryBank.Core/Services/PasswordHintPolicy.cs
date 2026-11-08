namespace BeeMemoryBank.Core.Services;

/// <summary>
/// The one rule for a password hint, shared by every place a password is created or replaced: the web forms, the HTTP
/// endpoints, the command line and the phone apps. A hint is not a second password: it is shown to anyone who types the
/// username on the sign-in page (there is no e-mail recovery), so it must help the person and nobody else.
///
/// <para>The rules: trimmed; 1 to 120 characters (so: not empty); no control characters or line breaks; it must not contain the
/// password or the username (case-insensitive, both sides trimmed) and it must never equal the password. The messages
/// are English sentences safe to show to the person who typed the hint - they say nothing about any other account.</para>
/// </summary>
public static class PasswordHintPolicy
{
    public const int MinimumLength = 1;
    public const int MaximumLength = 120;

    public const string RequiredMessage =
        "A password hint is required. Write a few words only you would recognise; it will be shown on the sign-in page if you forget your password.";

    /// <summary>The stored form: surrounding whitespace is not part of a hint.</summary>
    public static string? Normalize(string? hint) => hint?.Trim();

    /// <summary>
    /// Why <paramref name="hint"/> cannot be used for <paramref name="username"/>'s password, or null when it can.
    /// A null or blank hint is refused: callers that genuinely have no person (seeds, tools, tests) simply do not call
    /// this, and store no hint.
    /// </summary>
    public static string? Validate(string? hint, string? password, string? username)
    {
        var text = Normalize(hint);
        if (string.IsNullOrEmpty(text)) return RequiredMessage;

        if (text.Length < MinimumLength || text.Length > MaximumLength)
            return $"The password hint must be {MinimumLength} to {MaximumLength} characters long.";

        if (text.Any(char.IsControl))
            return "The password hint must not contain line breaks or control characters.";

        var hintComparison = text.ToLowerInvariant();
        var passwordComparison = (password ?? "").Trim().ToLowerInvariant();
        if (passwordComparison.Length > 0 && hintComparison.Contains(passwordComparison, StringComparison.Ordinal))
            return "The password hint must not contain the password.";

        var usernameComparison = (username ?? "").Trim().ToLowerInvariant();
        if (usernameComparison.Length > 0 && hintComparison.Contains(usernameComparison, StringComparison.Ordinal))
            return "The password hint must not contain the username.";

        return null;
    }

    /// <summary>Throws the policy's sentence; the service layer's seam for callers that validate by exception.</summary>
    public static string NormalizeAndValidate(string hint, string password, string username)
    {
        if (Validate(hint, password, username) is { } problem) throw new ArgumentException(problem);
        return Normalize(hint)!;
    }
}