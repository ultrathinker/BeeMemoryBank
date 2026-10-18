using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;

namespace BeeMemoryBank.Web.Services;

/// <summary>
/// The answer of "Ask every device now" on the Metadata encryption card, kept in a cookie of its own for a few minutes so that a switch the node refuses (a typo
/// in the password, a condition that is not met) leaves the card as it was, with its form (BMB-305, BMB-303).
/// <para>
/// BMB-305 fix round: it used to ride in the TempData cookie, which has no expiry of its own (a report with the topology of the network stayed in the browser until
/// a later visit looked at its age) and no size limit (a long list of devices does not fit in a cookie: the browser drops it and the card loses its form). Here:
/// </para>
/// <list type="bullet">
/// <item>The cookie expires by itself after <see cref="Lifetime"/> (<c>Max-Age</c>), whether or not the page is visited again, and is sent only to the Admin page.</item>
/// <item>Only what the card shows is kept - a device's name, kind, status, version and last seen, the blockers - not capabilities, protocol numbers, ids or the
/// activation record, and no password: the report is compacted, compressed and protected (ASP.NET data protection), and bounded to <see cref="MaxEncodedLength"/>
/// characters: when the devices do not fit, the ones that are not ready are kept and the rest are counted (<c>peer_total</c>, <c>peer_not_ready</c>, <c>peers_omitted</c>).</item>
/// </list>
/// </summary>
public static class MetadataReadinessCookie
{
    public const string Name = "bmb_metadata_readiness";

    /// <summary>How long the answer stays: the cookie's own lifetime, and the age the page accepts.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>The longest value written (a cookie is allowed 4096 bytes with its name and attributes).</summary>
    public const int MaxEncodedLength = 3300;

    private const string Purpose = "BeeMemoryBank.Web.Admin.MetadataReadiness.v1";
    private const int MaxName = 60;
    private const int MaxBlockers = 12;

    /// <summary>Keeps <paramref name="report"/> (the readiness report of the Api, or one the page made of a refusal) for <see cref="Lifetime"/>.</summary>
    public static void Write(HttpContext context, JsonElement report, DateTime nowUtc)
    {
        var compact = Compact(report);
        var value = Encode(context, compact, nowUtc);
        context.Response.Cookies.Append(Name, value, Options(context));
    }

    /// <summary>The kept report and when it was made, or null (none, expired, damaged, or not protected by this site).</summary>
    public static (JsonElement Report, DateTime At)? Read(HttpContext context, DateTime nowUtc)
    {
        if (!context.Request.Cookies.TryGetValue(Name, out var value) || string.IsNullOrEmpty(value)) return null;
        try
        {
            var protector = context.RequestServices.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose);
            var packed = protector.Unprotect(Convert.FromBase64String(FromUrl(value)));
            using var brotli = new BrotliStream(new MemoryStream(packed), CompressionMode.Decompress);
            using var plain = new MemoryStream();
            brotli.CopyTo(plain);
            using var doc = JsonDocument.Parse(plain.ToArray());
            var at = doc.RootElement.GetProperty("at").GetDateTime().ToUniversalTime();
            if (nowUtc - at >= Lifetime || at - nowUtc > TimeSpan.FromMinutes(1)) return null;
            return (doc.RootElement.GetProperty("report").Clone(), at);
        }
        catch (Exception ex) when (ex is FormatException or System.Security.Cryptography.CryptographicException or JsonException or InvalidDataException
                                       or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    public static void Clear(HttpContext context) => context.Response.Cookies.Delete(Name, Options(context));

    private static CookieOptions Options(HttpContext context) => new()
    {
        HttpOnly = true,
        Secure = context.Request.IsHttps,
        SameSite = SameSiteMode.Lax,   // the page is reached by a redirect after a POST: a top-level navigation of this site
        Path = "/Admin",
        MaxAge = Lifetime,
        IsEssential = true,
    };

    /// <summary>What the card shows of a report, bounded: strings are cut, the blockers capped, the devices that are ready are the first to be left out.</summary>
    internal static JsonObject Compact(JsonElement report)
    {
        var result = new JsonObject
        {
            ["activated"] = report.TryGetProperty("activated", out var a) && a.ValueKind == JsonValueKind.True,
            ["ready"] = report.TryGetProperty("ready", out var r) && r.ValueKind == JsonValueKind.True,
            ["activation"] = null,
        };
        if (report.TryGetProperty("refused", out var refused) && refused.ValueKind == JsonValueKind.True) result["refused"] = true;
        // An answer that says "on" keeps the one thing the card shows of the activation: since when.
        if (report.TryGetProperty("activation", out var activation) && Text(activation, "activated_at", 40) is { } activatedAt)
            result["activation"] = new JsonObject { ["activated_at"] = activatedAt };

        var blockers = new JsonArray();
        var blockersOmitted = 0;
        if (report.TryGetProperty("blockers", out var bs) && bs.ValueKind == JsonValueKind.Array)
            foreach (var b in bs.EnumerateArray())
            {
                if (blockers.Count >= MaxBlockers) { blockersOmitted++; continue; }
                var entry = new JsonObject { ["code"] = Text(b, "code", 40) };
                if (Text(b, "display_name", MaxName) is { } name) entry["display_name"] = name;
                if (Text(b, "status", 20) is { } status) entry["status"] = status;
                blockers.Add(entry);
            }
        result["blockers"] = blockers;
        if (blockersOmitted > 0) result["blockers_omitted"] = blockersOmitted;

        var peers = new List<JsonObject>();
        if (report.TryGetProperty("peers", out var ps) && ps.ValueKind == JsonValueKind.Array)
            foreach (var p in ps.EnumerateArray())
                peers.Add(new JsonObject
                {
                    ["display_name"] = Text(p, "display_name", MaxName) ?? "",
                    ["kind"] = Text(p, "kind", 10) ?? "full",
                    ["status"] = Text(p, "status", 20) ?? "unknown",
                    ["app_version"] = Text(p, "app_version", 20),
                    ["seen_at"] = Text(p, "seen_at", 40),
                });
        result["peer_total"] = peers.Count;
        result["peer_not_ready"] = peers.Count(p => p["status"]?.GetValue<string>() != "ready");
        // Not ready first: those are the ones the card is about.
        result["peers"] = new JsonArray(peers.OrderBy(p => p["status"]?.GetValue<string>() == "ready" ? 1 : 0).Select(p => (JsonNode?)p).ToArray());
        return result;
    }

    private static string? Text(JsonElement element, string property, int max)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String) return null;
        var text = value.GetString();
        return text is null ? null : text.Length <= max ? text : text[..max];
    }

    /// <summary>The protected value, no longer than <see cref="MaxEncodedLength"/>: devices are dropped from the end of the list until it fits.</summary>
    private static string Encode(HttpContext context, JsonObject compact, DateTime nowUtc)
    {
        var protector = context.RequestServices.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose);
        var peers = (JsonArray)compact["peers"]!;
        var keep = peers.Count;
        while (true)
        {
            var report = (JsonObject)compact.DeepClone();
            var kept = new JsonArray(peers.Take(keep).Select(p => p!.DeepClone()).ToArray());
            report["peers"] = kept;
            if (keep < peers.Count) report["peers_omitted"] = peers.Count - keep;
            var envelope = new JsonObject { ["at"] = nowUtc, ["report"] = report };

            using var packed = new MemoryStream();
            using (var brotli = new BrotliStream(packed, CompressionLevel.SmallestSize, leaveOpen: true))
                brotli.Write(Encoding.UTF8.GetBytes(envelope.ToJsonString()));
            var value = ToUrl(Convert.ToBase64String(protector.Protect(packed.ToArray())));
            if (value.Length <= MaxEncodedLength || keep == 0) return value;
            keep = keep > 1 ? keep / 2 : 0;
        }
    }

    private static string ToUrl(string base64) => base64.TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string FromUrl(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        return base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
    }
}
