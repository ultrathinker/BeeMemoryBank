using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Web;

namespace BeeMemoryBank.SearchBench;

/// <summary>
/// Thin HTTP client over a running <c>BeeMemoryBank.Api</c> instance. Sends the <c>X-Internal-Key</c>
/// header the Api's <c>RequireInternalKey</c> gate expects (the harness generates the key and sets
/// it as <c>BMB_INTERNAL_KEY</c> on the child process, so loopback bypass is intentionally NOT in
/// play — the harness authenticates explicitly, exactly like the Web proxy does).
/// </summary>
internal sealed class BenchClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly string _base;
    private readonly string _internalKey;

    public BenchClient(string baseUrl, string internalKey, int maxConnectionsPerServer = 200)
    {
        _base = baseUrl.TrimEnd('/');
        _internalKey = internalKey;
        var handler = new SocketsHttpHandler
        {
            // The mixed-load scenario opens many concurrent connections to the same loopback endpoint.
            MaxConnectionsPerServer = Math.Max(4, maxConnectionsPerServer),
            // The Api is on loopback; DNS never changes, so we can cache indefinitely.
            PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.All
        };
        _http = new HttpClient(handler)
        {
            // Per-request timeout for the closed-loop scenarios; mixed load uses its own CancellationTokenSource.
            Timeout = TimeSpan.FromMinutes(5)
        };
    }

    /// <summary>True if the server returns 2xx for the given latency (in ms) and status code.</summary>
    public async Task<(bool success, int status, double latencyMs, long? resultCount, string? error)> SendSearchAsync(
        string query, bool content, CancellationToken ct)
    {
        var url = $"{_base}/api/search?q={HttpUtility.UrlEncode(query)}&content={(content ? "true" : "false")}";
        return await SendSearchCoreAsync(url, ct);
    }

    public async Task<(bool success, int status, double latencyMs, long? resultCount, string? error)> SendSemanticAsync(
        string query, int topK, CancellationToken ct)
    {
        var url = $"{_base}/api/search/semantic";
        return await PostJsonAndMeasureAsync(url, $$"""{"query":{{JsonSerializer.Serialize(query)}},"topK":{{topK}}}""", ct);
    }

    public async Task<bool> UnlockAsync(string password, CancellationToken ct)
    {
        var url = $"{_base}/api/session/unlock";
        var body = $$"""{"password":{{JsonSerializer.Serialize(password)}}}""";
        using var req = NewRequest(HttpMethod.Post, url);
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        try
        {
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<bool> WaitForHealthAsync(TimeSpan timeout, CancellationToken ct)
    {
        var url = $"{_base}/health";
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        while (!cts.IsCancellationRequested)
        {
            try
            {
                using var req = NewRequest(HttpMethod.Get, url);
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                if (resp.IsSuccessStatusCode)
                    return true;
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { break; }
            catch { /* keep polling */ }
            try { await Task.Delay(200, cts.Token); } catch { break; }
        }
        return false;
    }

    private async Task<(bool success, int status, double latencyMs, long? resultCount, string? error)> SendSearchCoreAsync(
        string url, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var req = NewRequest(HttpMethod.Get, url);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            long? count = null;
            if (resp.IsSuccessStatusCode)
                count = await TryReadResultCountAsync(resp, ct);
            else
                count = null;
            sw.Stop();
            string? err = resp.IsSuccessStatusCode ? null : $"HTTP {resp.StatusCode}";
            return (resp.IsSuccessStatusCode, (int)resp.StatusCode, sw.Elapsed.TotalMilliseconds, count, err);
        }
        catch (OperationCanceledException) { sw.Stop(); return (false, 0, sw.Elapsed.TotalMilliseconds, null, "cancelled"); }
        catch (Exception ex) { sw.Stop(); return (false, 0, sw.Elapsed.TotalMilliseconds, null, ex.GetType().Name); }
    }

    private async Task<(bool success, int status, double latencyMs, long? resultCount, string? error)> PostJsonAndMeasureAsync(
        string url, string jsonBody, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var req = NewRequest(HttpMethod.Post, url);
            req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            long? count = null;
            if (resp.IsSuccessStatusCode)
                count = await TryReadResultCountAsync(resp, ct);
            sw.Stop();
            string? err = resp.IsSuccessStatusCode ? null : $"HTTP {resp.StatusCode}";
            return (resp.IsSuccessStatusCode, (int)resp.StatusCode, sw.Elapsed.TotalMilliseconds, count, err);
        }
        catch (OperationCanceledException) { sw.Stop(); return (false, 0, sw.Elapsed.TotalMilliseconds, null, "cancelled"); }
        catch (Exception ex) { sw.Stop(); return (false, 0, sw.Elapsed.TotalMilliseconds, null, ex.GetType().Name); }
    }

    /// <summary>Best-effort extraction of the result count so the report can show query selectivity.</summary>
    /// <remarks>
    /// <c>/api/search</c> returns <c>{"folders":[...],"articles":[...]}</c> and
    /// <c>/api/search/semantic</c> returns a top-level JSON array. Both shapes are read without
    /// buffering the whole payload — the stream is scanned for the <c>articles</c> array length /
    /// top-level array length only. On any parse hiccup we return null (latency is unaffected).
    /// </remarks>
    private async Task<long?> TryReadResultCountAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                return doc.RootElement.GetArrayLength();
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("articles", out var articles) &&
                articles.ValueKind == JsonValueKind.Array)
            {
                long total = articles.GetArrayLength();
                if (doc.RootElement.TryGetProperty("folders", out var folders) &&
                    folders.ValueKind == JsonValueKind.Array)
                    total += folders.GetArrayLength();
                return total;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private HttpRequestMessage NewRequest(HttpMethod method, string url)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.TryAddWithoutValidation("X-Internal-Key", _internalKey);
        // Present as superadmin so CallerScopeMiddleware grants full-vault read scope. Without this,
        // a caller carrying only X-Internal-Key has no user/agent identity and is deny-alled (every
        // search/tree read returns an empty list) — see CallerScopeMiddleware's anonymous branch.
        // The role header is trusted by CallerIdentity.Extract precisely because the internal key is
        // valid (the harness sets BMB_INTERNAL_KEY); this mirrors exactly what the Web proxy does.
        req.Headers.TryAddWithoutValidation("X-User-Role", "superadmin");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return req;
    }

    public void Dispose() => _http.Dispose();
}
