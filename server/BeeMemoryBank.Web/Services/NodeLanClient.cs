using System.Net.Http.Json;
using System.Text.Json;

namespace BeeMemoryBank.Web.Services;

/// <summary>
/// Talks to the desktop node's <c>/node/lan</c> endpoints for the Connect page and the "Devices on my network" card: status,
/// open and close the temporary door, switch the permanent setting, and add or remove the firewall rule. Those endpoints live
/// in the node's front process, not the Api, so the address comes from the node's <c>.runtime.json</c> in the data directory,
/// and the request carries the internal key the front checks.
///
/// <para>Outside the desktop node (Docker, a standalone Api+Web) there is no such file and every call answers
/// <see cref="LanStatus.Unavailable"/> — the pages then say what applies there instead of offering a button that does
/// nothing.</para>
/// </summary>
/// <param name="http">The client the calls go through.</param>
/// <param name="dataPath">The data folder holding <c>.runtime.json</c>; null: <c>BMB_DATA_PATH</c> (a test passes its own).</param>
/// <param name="internalKey">The key the front checks; null: <c>BMB_INTERNAL_KEY</c>.</param>
public sealed class NodeLanClient(HttpClient http, string? dataPath = null, string? internalKey = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<LanStatus> GetAsync(CancellationToken ct = default) => SendAsync(HttpMethod.Get, "", null, ct);
    public Task<LanStatus> EnableAsync(CancellationToken ct = default) => SendAsync(HttpMethod.Post, "/enable", null, ct);
    public Task<LanStatus> DisableAsync(CancellationToken ct = default) => SendAsync(HttpMethod.Post, "/disable", null, ct);
    public Task<LanStatus> AddFirewallRuleAsync(CancellationToken ct = default) => SendAsync(HttpMethod.Post, "/firewall", null, ct);
    public Task<LanStatus> RemoveFirewallRuleAsync(CancellationToken ct = default) => SendAsync(HttpMethod.Post, "/firewall/remove", null, ct);

    /// <summary>Switches "Devices on my network". The answer carries <see cref="LanStatus.Error"/> when the node refused or could not.</summary>
    public Task<LanStatus> SetDevicesOnMyNetworkAsync(bool enabled, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, "/network", new { enabled }, ct);

    private async Task<LanStatus> SendAsync(HttpMethod method, string action, object? body, CancellationToken ct)
    {
        var front = ReadFrontUrl(dataPath ?? Environment.GetEnvironmentVariable("BMB_DATA_PATH"));
        var key = internalKey ?? Environment.GetEnvironmentVariable("BMB_INTERNAL_KEY");
        if (front == null || string.IsNullOrEmpty(key)) return LanStatus.Unavailable;

        using var req = new HttpRequestMessage(method, $"{front.TrimEnd('/')}/node/lan{action}");
        req.Headers.TryAddWithoutValidation("X-Internal-Key", key);
        if (body != null) req.Content = JsonContent.Create(body, options: Json);
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

    private static string? ReadFrontUrl(string? dataPath)
    {
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

    /// <summary>
    /// The node's answer. <c>Mode</c> is "on-demand", "permanent" or "unavailable". <c>Setting</c> is "off", "on" or "environment"
    /// (BMB_HTTPS_ENABLED=1 decides). <c>Platform</c> is "windows", "macos" or "other"; <c>FirewallManaged</c> is true where the app
    /// can offer to change the firewall (the Windows desktop app), and <c>FirewallRule</c> only means something then.
    /// </summary>
    public sealed record LanStatus(
        string Mode, bool Active, string? Token, DateTimeOffset? ExpiresAt, string? SpkiPin, int Port, bool FirewallRule,
        string? Error = null, string Setting = "off", string Platform = "other", bool FirewallManaged = false)
    {
        public static readonly LanStatus Unavailable = new("unavailable", false, null, null, null, 0, false);

        public bool IsAvailable => Mode != "unavailable";
    }
}
