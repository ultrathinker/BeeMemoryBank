using System.Net.Http.Headers;
using System.Text;

namespace BeeMemoryBank.Cli.Commands;

/// <summary>
/// Where the CLI finds the blind node's Api and how to authenticate to it: base URL and the
/// node's internal key (from the environment, or the {dataPath}/.internal-key file the Api
/// writes in development — in production docker-entrypoint exports both to every process).
/// The <c>Handler</c> field exists for tests and embedding; production leaves it null.
/// </summary>
public sealed class BlindApiOptions
{
    public string? BaseUrl { get; init; }
    public string? InternalKey { get; init; }
    public HttpMessageHandler? Handler { get; init; }
}

/// <summary>
/// Minimal HTTP client for the Api's /api/blind/* surface. Every call carries the internal key;
/// nothing here talks to the network beyond the node's own Api.
/// </summary>
public sealed class BlindApi(BlindApiOptions options)
{
    public static string DefaultBaseUrl =>
        Environment.GetEnvironmentVariable("BMB_API_URL") ?? "http://127.0.0.1:5300";

    private readonly HttpClient _http = new(options.Handler ?? new SocketsHttpHandler())
    {
        BaseAddress = new Uri(options.BaseUrl ?? DefaultBaseUrl),
    };

    /// <summary>The key from the environment, or the data-dir key file the Api shares with local processes.</summary>
    public static string? ResolveInternalKey(string? dataPath)
    {
        var key = Environment.GetEnvironmentVariable("BMB_INTERNAL_KEY");
        if (!string.IsNullOrEmpty(key)) return key;
        if (dataPath is null) return null;
        var keyFile = Path.Combine(dataPath, ".internal-key");
        return File.Exists(keyFile) ? File.ReadAllText(keyFile).Trim() : null;
    }

    public async Task<(int Status, string Body)> SendAsync(string method, string path, string? json = null)
    {
        using var req = new HttpRequestMessage(new HttpMethod(method), path);
        var key = options.InternalKey;
        if (string.IsNullOrEmpty(key))
            throw new InvalidOperationException(
                "no internal key: set BMB_INTERNAL_KEY or point --data at the node's data directory");
        req.Headers.Add("X-Internal-Key", key);
        // `bmb blind` runs on the node itself with the node's internal key: it is the local
        // administrator, and the /api/blind/* routes are gated .RequireSuperadmin().
        req.Headers.Add("X-User-Role", "superadmin");
        if (json is not null)
        {
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        using var resp = await _http.SendAsync(req);
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync());
    }
}
