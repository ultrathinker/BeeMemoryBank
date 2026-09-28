namespace BeeMemoryBank.Core.Models;

/// <summary>
/// The text form shared by the codes a user carries between devices (<see cref="JoinCode"/>,
/// <see cref="BlindPhoneCode"/>, <see cref="BlindCallCode"/>): <c>&lt;prefix&gt;k=v&amp;k=v</c>, values
/// URI-escaped. Parsing is strict because these codes decide where secrets go.
/// </summary>
internal static class CodeText
{
    public static string Format(string prefix, IEnumerable<(string Key, string? Value)> fields) =>
        prefix + string.Join("&", fields.Where(f => f.Value != null).Select(f => f.Key + "=" + Uri.EscapeDataString(f.Value!)));

    /// <summary>
    /// The fields of <paramref name="text"/>, or null if it does not start with <paramref name="prefix"/>,
    /// has a malformed pair, or repeats a key — a repeated key is ambiguous (which address was meant?).
    /// </summary>
    public static Dictionary<string, string>? Parse(string? text, string prefix)
    {
        text = text?.Trim();
        if (text is null || !text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in text[prefix.Length..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) return null;
            if (!fields.TryAdd(pair[..eq], Uri.UnescapeDataString(pair[(eq + 1)..]))) return null;
        }
        return fields;
    }

    /// <summary>
    /// <paramref name="address"/> as <c>https://host[:port]</c> if it is exactly that: https (a pin means
    /// nothing over plain HTTP), a host, no path, query, fragment or user info (a path would let a code
    /// point a request at some other endpoint).
    /// </summary>
    public static string? HttpsOrigin(string? address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrEmpty(uri.Host)
            || uri.PathAndQuery != "/"
            || !string.IsNullOrEmpty(uri.Fragment)
            || !string.IsNullOrEmpty(uri.UserInfo))
            return null;
        return uri.GetLeftPart(UriPartial.Authority);
    }

    /// <summary>The bytes of a base64url value of exactly <paramref name="length"/> bytes, else null.</summary>
    public static byte[]? Base64UrlOfLength(string? value, int length)
    {
        if (string.IsNullOrEmpty(value)) return null;
        try
        {
            var bytes = System.Buffers.Text.Base64Url.DecodeFromChars(value);
            return bytes.Length == length ? bytes : null;
        }
        catch (FormatException) { return null; }
    }
}
