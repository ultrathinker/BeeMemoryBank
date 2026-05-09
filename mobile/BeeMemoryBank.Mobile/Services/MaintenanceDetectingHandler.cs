using System.Net;
using System.Text.Json;

namespace BeeMemoryBank.Mobile.Services;

/// <summary>
/// Detects HTTP 503 responses from the BMB API (which always indicate node maintenance —
/// snapshot restore in progress, DEK rotation in progress, etc.) and rewrites the response
/// body so the generic error-display logic in pages shows a friendly "Node maintenance: …"
/// message instead of a raw "Service Unavailable".
///
/// Roadmap p5. Doesn't help raw `new HttpClient()` sites elsewhere in the app — those bypass
/// DI — but the named/typed clients registered in MauiProgram do route through this handler.
/// </summary>
public class MaintenanceDetectingHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var resp = await base.SendAsync(request, cancellationToken);
        if (resp.StatusCode != HttpStatusCode.ServiceUnavailable)
            return resp;

        // The API's MaintenanceMiddleware writes JSON: { "error": "...", "reason": "..." }.
        // Try to extract `reason`; fall back to a generic message if parsing fails.
        // Bound the body read at 16 KB and JSON depth at 8 — without bounds a malicious peer
        // or compromised proxy could send a multi-gigabyte 503 to OOM the mobile app.
        // (Found by Gemini R1 security review.)
        const int MaxBodyBytes = 16 * 1024;
        string reason = "Node is being maintained. Try again in a minute.";
        try
        {
            using var src = await resp.Content.ReadAsStreamAsync(cancellationToken);
            using var ms = new MemoryStream();
            var buf = new byte[4096];
            int read;
            while ((read = await src.ReadAsync(buf.AsMemory(), cancellationToken)) > 0)
            {
                if (ms.Length + read > MaxBodyBytes) break;   // truncate; ignore beyond cap
                ms.Write(buf, 0, read);
            }
            ms.Position = 0;
            using var doc = await JsonDocument.ParseAsync(ms,
                new JsonDocumentOptions { MaxDepth = 8 }, cancellationToken);
            if (doc.RootElement.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String)
            {
                var parsed = r.GetString();
                if (!string.IsNullOrWhiteSpace(parsed))
                    reason = $"Node maintenance: {parsed}";
            }
        }
        catch
        {
            // body wasn't the expected JSON shape, was too large, or was nested too deep —
            // keep the default reason.
        }

        // Rewrite content so any caller that does `await resp.Content.ReadAsStringAsync()` or
        // surfaces the response body to the user sees a sensible message.
        var rewritten = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            ReasonPhrase = reason,
            Content = new StringContent(
                JsonSerializer.Serialize(new { error = reason, reason }),
                System.Text.Encoding.UTF8, "application/json"),
            RequestMessage = resp.RequestMessage,
            Version = resp.Version
        };
        foreach (var header in resp.Headers)
            rewritten.Headers.TryAddWithoutValidation(header.Key, header.Value);
        resp.Dispose();
        return rewritten;
    }
}
