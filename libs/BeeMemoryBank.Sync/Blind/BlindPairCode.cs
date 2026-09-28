using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BeeMemoryBank.Sync.Blind;

/// <summary>
/// The pair code a blind node prints (plan 4.1): everything the PC needs to trust it without any
/// third party — its NodeId and public key, where to reach it, the pin of its self-signed TLS key,
/// and a one-time secret (15 minutes) that authorizes the first seed.
///
/// <para>Text form: <c>BMBBLIND1.</c> + base64url(JSON). Copied by hand between two screens, so it
/// must survive a clipboard and a messenger unchanged.</para>
/// </summary>
public sealed record BlindPairCode(
    [property: JsonPropertyName("node_id")] Guid NodeId,
    [property: JsonPropertyName("public_key")] string PublicKeyB64,
    [property: JsonPropertyName("address")] string Address,
    [property: JsonPropertyName("tls_spki")] string TlsSpki,
    [property: JsonPropertyName("secret")] string Secret,
    [property: JsonPropertyName("expires_at")] DateTime ExpiresAt)
{
    public const string Prefix = "BMBBLIND1.";

    /// <summary>How long a pairing secret stays valid (plan 4.1).</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    public override string ToString() =>
        Prefix + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(this))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// Parses a pasted code. Refuses anything that is not a well-formed blind code — in
    /// particular a NodeId without the blind mark: adding an ordinary node through this path would
    /// skip everything the join flow checks.
    /// </summary>
    public static BlindPairCode Parse(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith(Prefix, StringComparison.Ordinal))
            throw new FormatException("Not a blind node pair code.");

        BlindPairCode? code;
        try
        {
            var b64 = trimmed[Prefix.Length..].Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            code = JsonSerializer.Deserialize<BlindPairCode>(Convert.FromBase64String(b64));
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw new FormatException("The pair code is damaged.", ex);
        }

        if (code is null || !Core.Models.BlindNodeId.IsBlind(code.NodeId))
            throw new FormatException("The pair code does not name a blind node.");
        if (!Uri.TryCreate(code.Address, UriKind.Absolute, out var address) || address.Scheme != Uri.UriSchemeHttps)
            throw new FormatException("The pair code carries no HTTPS address.");
        if (string.IsNullOrEmpty(code.Secret) || string.IsNullOrEmpty(code.TlsSpki) || string.IsNullOrEmpty(code.PublicKeyB64))
            throw new FormatException("The pair code is incomplete.");
        return code;
    }
}
