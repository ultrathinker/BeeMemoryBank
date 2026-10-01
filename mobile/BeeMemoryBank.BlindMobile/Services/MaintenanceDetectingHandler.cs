using System.Net;
using System.Text.Json;

namespace BeeMemoryBank.BlindMobile.Services;

/// <summary>
/// Detects HTTP 503 responses from the BMB API (which always indicate node maintenance —
/// snapshot restore in progress, DEK rotation in progress, etc.) and rewrites the response
/// body so the generic error-display logic in pages shows a friendly "Node maintenance: …"
/// message instead of a raw "Service Unavailable".
/// </summary>
public class MaintenanceDetectingHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var resp = await base.SendAsync(request, cancellationToken);
        if (resp.StatusCode != HttpStatusCode.ServiceUnavailable)
            return resp;

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
                if (ms.Length + read > MaxBodyBytes) break;
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
            // body wasn't the expected JSON shape, was too large, or was nested too deep — keep default reason
        }

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
