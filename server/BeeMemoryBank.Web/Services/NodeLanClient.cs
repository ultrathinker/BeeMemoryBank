using System.Net.Http.Json;
using System.Text.Json;

namespace BeeMemoryBank.Web.Services;

/// <summary>
/// Talks to the Windows node's <c>/node/lan</c> endpoints for the Connect page: status, open and
/// close the LAN join listener, and add its firewall rule. Those endpoints live in the node's front
/// process, not the Api, so the address comes from the node's <c>.runtime.json</c> in the data
/// directory, and the request carries the internal key the front checks.
///
/// <para>Outside the Windows node (Docker, a standalone Api+Web) there is no such file and every
/// call answers <see cref="LanStatus.Unavailable"/> — the page then says the node cannot open a LAN
/// listener, instead of offering a button that does nothing.</para>
/// </summary>
public sealed class NodeLanClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<LanStatus> GetAsync(CancellationToken ct = default) => SendAsync(HttpMethod.Get, "", ct);
    public Task<LanStatus> EnableAsync(CancellationToken ct = default) => SendAsync(HttpMethod.Post, "/enable", ct);
    public Task<LanStatus> DisableAsync(CancellationToken ct = default) => SendAsync(HttpMethod.Post, "/disable", ct);
    public Task<LanStatus> AddFirewallRuleAsync(CancellationToken ct = default) => SendAsync(HttpMethod.Post, "/firewall", ct);

    private async Task<LanStatus> SendAsync(HttpMethod method, string action, CancellationToken ct)
    {
        var front = ReadFrontUrl();
        var key = Environment.GetEnvironmentVariable("BMB_INTERNAL_KEY");
        if (front == null || string.IsNullOrEmpty(key)) return LanStatus.Unavailable;

        using var req = new HttpRequestMessage(method, $"{front.TrimEnd('/')}/node/lan{action}");
        req.Headers.TryAddWithoutValidation("X-Internal-Key", key);
        try
        {
            using var resp = await http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
                return await resp.Content.ReadFromJsonAsync<LanStatus>(Json, ct) ?? LanStatus.Unavailable;

            var error = await resp.Content.ReadFromJsonAsync<ErrorBody>(Json, ct).ConfigureAwait(false);
            return LanStatus.Unavailable with { Error = error?.Error ?? $"The node answered {(int)resp.StatusCode}." };
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            return LanStatus.Unavailable with { Error = $"The node could not be reached: {ex.Message}" };
        }
    }

    private static string? ReadFrontUrl()
    {
        var dataPath = Environment.GetEnvironmentVariable("BMB_DATA_PATH");
        if (string.IsNullOrEmpty(dataPath)) return null;
        var path = Path.Combine(dataPath, ".runtime.json");
        try
        {
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("frontUrl", out var url) ? url.GetString() : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed record ErrorBody(string? Error);

    /// <summary>The node's answer; <c>Mode</c> is "on-demand", "permanent" or "unavailable".</summary>
    public sealed record LanStatus(
        string Mode, bool Active, string? Token, DateTimeOffset? ExpiresAt, string? SpkiPin, int Port, bool FirewallRule,
        string? Error = null)
    {
        public static readonly LanStatus Unavailable = new("unavailable", false, null, null, null, 0, false);
    }
}
