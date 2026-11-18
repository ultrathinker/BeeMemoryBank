using System.Net.Http.Json;
using System.Text.Json;

namespace BeeMemoryBank.Cli.Commands;

/// <summary>
/// <c>bmb accounts-home get</c> and <c>bmb accounts-home set --node-id &lt;guid&gt;</c>
/// against the running node's API using the internal key.
/// </summary>
public static class AccountsHomeCommand
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private static HttpClient CreateClient(string dataPath)
    {
        var url = Environment.GetEnvironmentVariable("BMB_API_URL") ?? "http://localhost:5300";
        var key = Environment.GetEnvironmentVariable("BMB_INTERNAL_KEY");
        if (string.IsNullOrEmpty(key))
        {
            var keyFile = Path.Combine(dataPath, ".internal-key");
            if (File.Exists(keyFile))
            {
                try { key = File.ReadAllText(keyFile).Trim(); } catch { }
            }
        }
        var http = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(30) };
        if (!string.IsNullOrEmpty(key)) http.DefaultRequestHeaders.Add("X-Internal-Key", key);
        http.DefaultRequestHeaders.Add("X-User-Role", "superadmin");
        return http;
    }

    public static async Task<int> GetAsync(string dataPath, TextWriter? output = null)
    {
        output ??= Console.Out;
        try
        {
            using var http = CreateClient(dataPath);
            var resp = await http.GetAsync("/api/shared-doors/status");
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                await output.WriteLineAsync($"Error ({(int)resp.StatusCode}): {ExtractError(body)}");
                return 1;
            }
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var nodeId = root.TryGetProperty("accountsHomeNodeId", out var nid) && nid.ValueKind == JsonValueKind.String
                ? nid.GetString()
                : "(not set)";
            var name = root.TryGetProperty("accountsHomeName", out var nm) && nm.ValueKind == JsonValueKind.String
                ? nm.GetString()
                : null;
            await output.WriteLineAsync(name is null
                ? $"Accounts node: {nodeId}"
                : $"Accounts node: {name} ({nodeId})");
            return 0;
        }
        catch (HttpRequestException)
        {
            await output.WriteLineAsync("Error: could not reach the node API.");
            return 1;
        }
    }

    public static async Task<int> SetAsync(string dataPath, Guid nodeId, TextWriter? output = null)
    {
        output ??= Console.Out;
        if (nodeId == Guid.Empty)
        {
            await output.WriteLineAsync("Error: provide a valid non-empty node id.");
            return 1;
        }
        try
        {
            using var http = CreateClient(dataPath);
            var resp = await http.PutAsJsonAsync("/api/accounts-home", new { nodeId });
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                await output.WriteLineAsync($"Error ({(int)resp.StatusCode}): {ExtractError(body)}");
                return 1;
            }
            await output.WriteLineAsync($"Accounts node set to {nodeId:D}.");
            return 0;
        }
        catch (HttpRequestException)
        {
            await output.WriteLineAsync("Error: could not reach the node API.");
            return 1;
        }
    }

    private static string ExtractError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String)
                return err.GetString() ?? body;
        }
        catch { }
        return string.IsNullOrWhiteSpace(body) ? "request failed" : body;
    }
}
