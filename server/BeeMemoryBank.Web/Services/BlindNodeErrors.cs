using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BeeMemoryBank.Web.Services;

/// <summary>
/// What the "Blind nodes" page shows when something fails: the plain sentence (what is wrong and the
/// action that fixes it), one item per device when several are to blame, and the technical line the
/// Api gave, kept short and last.
/// </summary>
public sealed record BlindNodeError(string Message, IReadOnlyList<string> Items, string? Detail)
{
    public static BlindNodeError Of(string message, string? detail = null) => new(message, [], detail);
}

/// <summary>
/// The wording of every failure of the "Blind nodes" page. Nothing here decides anything: it turns what
/// the Api answered (status, <c>error</c>, <c>problems</c>, <c>details</c>) into text an operator can act
/// on without knowing what a protocol or a seed is. Each message says what is wrong and what to do.
/// </summary>
public static partial class BlindNodeErrors
{
    private const string RenewHint = "press Renew code on the blind node's console page and paste the new code here";

    public static BlindNodeError EmptyCode() => BlindNodeError.Of(
        "Paste the pair code shown on the blind node's console page (section Pairing), then press Add.");

    /// <summary>This app's own server (the Api) did not answer at all.</summary>
    public static BlindNodeError AppDidNotAnswer(Exception ex) => BlindNodeError.Of(
        "The Bee Memory Bank server on this computer did not answer. Wait a moment and try again; if it keeps happening, restart Bee Memory Bank.",
        Short(ex.Message));

    public static BlindNodeError ForDisconnect() => BlindNodeError.Of(
        "The blind node could not be disconnected. Try again; if it fails again, restart Bee Memory Bank and check that you are signed in as a superadmin.");

    public static BlindNodeError ForAdd(HttpStatusCode status, string body)
    {
        var b = ApiBody.Parse(body);
        switch ((int)status)
        {
            case 400:
                return BlindNodeError.Of(
                    "This is not a valid pair code. Copy the whole code again from the blind node's console page (section Pairing), paste it here and press Add.",
                    b.Error);
            case 409 when b.Problems.Count > 0:
                return new BlindNodeError(
                    "Cannot add the blind node yet. Fix the following, then press Add again.",
                    // One entry of `details` per problem; an Api without them (older) sends its own sentences.
                    b.Details.Count == b.Problems.Count ? b.Details.Select(Explain).ToList() : b.Problems,
                    null);
            case 401 or 403:
                return Refusal(b);
            case 502:
                return Unreachable(b.Error, reseed: false);
            default:
                return Other("The blind node could not be added.", "Press Add again.", status, b);
        }
    }

    public static BlindNodeError ForReseed(HttpStatusCode status, string body)
    {
        var b = ApiBody.Parse(body);
        return (int)status switch
        {
            404 => BlindNodeError.Of(
                "This blind node is not in the list any more, or it has no address. Reload the page; if it was disconnected, add it again with a new pair code."),
            401 or 403 => Refusal(b),
            502 => Unreachable(b.Error, reseed: true),
            >= 500 => BlindNodeError.Of(
                "This computer could not reach the blind node, or it stopped answering. Check that the blind node is running and that this computer can connect to it (same network or VPN, its port open in the firewall), then press Reseed again.",
                b.Error),
            _ => Other("The blind node could not be reseeded.", "Press Reseed again.", status, b),
        };
    }

    /// <summary>Not allowed: the vault is locked (Unlock first) or the signed-in user is not a superadmin.</summary>
    private static BlindNodeError Refusal(ApiBody b) =>
        b.Code == "session_locked" || (b.Error?.Contains("locked", StringComparison.OrdinalIgnoreCase) ?? false)
            ? BlindNodeError.Of("The vault on this computer is locked. Unlock it first (Unlock page), then try again.", b.Error)
            : BlindNodeError.Of("Only a superadmin can manage blind nodes. Sign in as a superadmin and try again.", b.Error);

    /// <summary>The Api's 502: this computer could not reach the blind node, or the blind node refused what was sent.</summary>
    private static BlindNodeError Unreachable(string? message, bool reseed)
    {
        var m = message ?? "";
        if (m.Contains("is not the one in the pair code", StringComparison.Ordinal))
            return BlindNodeError.Of(
                "The address in the pair code answered as a different node. Copy the pair code again from the blind node you want to add, paste it here and press Add.", m);

        if (m.StartsWith("Could not reach the blind node", StringComparison.Ordinal))
        {
            var at = Address().Match(m) is { Success: true } a ? $" at {a.Groups[1].Value}" : "";
            return BlindNodeError.Of(
                $"This computer could not reach the blind node{at}. Check that the blind node is running and that this computer can connect to that address and port " +
                $"(same network or VPN, the port open in the firewall of the blind node's machine). If its address changed, {RenewHint}, then press Add.", m);
        }

        var status = Refused().Match(m) is { Success: true } r ? int.Parse(r.Groups[1].Value) : 0;
        return status switch
        {
            401 or 403 when reseed => BlindNodeError.Of(
                "The blind node no longer accepts this computer as its manager (it may have been wiped or paired again from another computer). Add it again: " +
                $"{RenewHint}, then press Add.", m),
            401 or 403 => BlindNodeError.Of(
                $"The blind node did not accept this pair code: it has expired or was already used. {Capitalize(RenewHint)}, then press Add.", m),
            409 => BlindNodeError.Of(
                "The blind node is still receiving another copy. Wait a few minutes, then press " + (reseed ? "Reseed" : "Add") + " again.", m),
            507 => BlindNodeError.Of(
                "The blind node does not have enough free disk space for the copy. Free some space on the blind node's machine (or give it a bigger disk), then press " +
                (reseed ? "Reseed" : "Add") + " again.", m),
            _ => BlindNodeError.Of(
                "The blind node refused the copy this computer sent. " + (reseed ? "Press Reseed again; " : $"{Capitalize(RenewHint)} and press Add again; ") +
                "if it keeps failing, update the blind node to the same version as this computer.", m),
        };
    }

    private static BlindNodeError Other(string what, string action, HttpStatusCode status, ApiBody b) => BlindNodeError.Of(
        $"{what} Something went wrong on this computer (error {(int)status}). {action} If it keeps failing, look at Admin → Diagnostics.",
        b.Error);

    /// <summary>One pre-flight problem in plain words: the device, what is wrong, what to do.</summary>
    private static string Explain(ApiBody.Problem p) => p.Kind switch
    {
        "unknown_protocol" =>
            $"The device '{p.Device}' has not been seen on the current version (sync protocol 3) for over {p.Days ?? 7} days. " +
            "Update it to the latest version and open it once so it syncs, or remove it under Admin → Nodes.",
        "old_protocol" =>
            $"The device '{p.Device}' runs an older version (sync protocol {p.Protocol}; the blind node needs 3). " +
            "Update it to the latest version and open it once so it syncs, or remove it under Admin → Nodes.",
        "not_superadmin" =>
            $"The device '{p.Device}' does not yet accept this computer as a superadmin. " +
            $"On '{p.Device}' open Admin → Nodes and make this computer a superadmin.",
        "refused" =>
            $"The device '{p.Device}' did not accept this computer's check: it may run an older version, or it no longer lists this computer. " +
            "Update it to the latest version and open it once so it syncs; if that does not help, remove it under Admin → Nodes.",
        _ => p.Text ?? $"The device '{p.Device}' needs attention.",
    };

    private static string Capitalize(string s) => char.ToUpperInvariant(s[0]) + s[1..];

    private static string? Short(string? s) => s is { Length: > 300 } ? s[..300] + "…" : s;

    [GeneratedRegex(@"at (\S+?) with the key")]
    private static partial Regex Address();

    [GeneratedRegex(@"refused the seed \((\d{3})\)")]
    private static partial Regex Refused();

    /// <summary>What the Api's error bodies carry: <c>error</c>, <c>code</c>, and for a pre-flight refusal <c>problems</c> and <c>details</c>.</summary>
    private sealed record ApiBody(string? Error, string? Code, IReadOnlyList<string> Problems, IReadOnlyList<ApiBody.Problem> Details)
    {
        public sealed record Problem(string? Kind, string? Device, int? Protocol, int? Days, string? Text);

        public static ApiBody Parse(string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                string? Str(string name) => root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                var problems = root.TryGetProperty("problems", out var ps) && ps.ValueKind == JsonValueKind.Array
                    ? ps.EnumerateArray().Select(x => x.GetString()).OfType<string>().ToList()
                    : [];
                var details = root.TryGetProperty("details", out var ds) && ds.ValueKind == JsonValueKind.Array
                    ? ds.EnumerateArray().Select((d, i) => new Problem(
                        d.TryGetProperty("kind", out var k) ? k.GetString() : null,
                        d.TryGetProperty("device", out var dv) ? dv.GetString() : null,
                        d.TryGetProperty("protocol", out var pr) && pr.ValueKind == JsonValueKind.Number ? pr.GetInt32() : null,
                        d.TryGetProperty("days", out var dy) && dy.ValueKind == JsonValueKind.Number ? dy.GetInt32() : null,
                        i < problems.Count ? problems[i] : null)).ToList()
                    : [];
                return new ApiBody(Short(Str("error")), Str("code"), problems, details);
            }
            catch (JsonException)
            {
                return new ApiBody(Short(body.Length == 0 ? null : body), null, [], []);
            }
        }
    }
}
