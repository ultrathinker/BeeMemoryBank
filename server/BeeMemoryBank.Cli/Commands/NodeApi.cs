using System.Net.Http.Json;
using System.Text.Json;

namespace BeeMemoryBank.Cli.Commands;

/// <summary>The running node's Api, as the CLI verbs that talk to it reach it (the internal key from the environment or the data folder).</summary>
public static class NodeApi
{
    /// <summary>Where the running node is, instead of <c>BMB_API_URL</c> and the default port: the tests, which must not reach a node of the machine they run on.</summary>
    public static string? UrlOverride { get; set; }

    internal static HttpClient CreateClient(string dataPath, TimeSpan? timeout = null)
    {
        var url = UrlOverride ?? Environment.GetEnvironmentVariable("BMB_API_URL") ?? "http://localhost:5300";
        var key = Environment.GetEnvironmentVariable("BMB_INTERNAL_KEY");
        if (string.IsNullOrEmpty(key))
        {
            var keyFile = Path.Combine(dataPath, ".internal-key");
            if (File.Exists(keyFile))
            {
                try { key = File.ReadAllText(keyFile).Trim(); } catch (IOException) { /* unreadable: the Api answers 404 without it */ }
            }
        }
        var http = new HttpClient { BaseAddress = new Uri(url), Timeout = timeout ?? TimeSpan.FromSeconds(30) };
        if (!string.IsNullOrEmpty(key)) http.DefaultRequestHeaders.Add("X-Internal-Key", key);
        http.DefaultRequestHeaders.Add("X-User-Role", "superadmin");
        return http;
    }

    /// <summary>The unauthenticated status of the running node, or null when none answers (not running, or another address).</summary>
    internal static async Task<JsonElement?> TryVaultStatusAsync(string dataPath)
    {
        try
        {
            using var http = CreateClient(dataPath, TimeSpan.FromSeconds(2));
            using var response = await http.GetAsync("/api/vault/status");
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}
