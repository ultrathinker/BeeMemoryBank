using System.Net.Http.Json;
using System.Text.Json;

namespace BeeMemoryBank.Web.Services;

/// <summary>
/// Talks to the desktop node's <c>/node/lan</c> endpoints for Admin &gt; Devices (the Connect a device dialog) and Admin &gt; Network settings: status,
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

    /// <summary>
    /// The client a page uses. The data folder is the host's own setting (<c>BeeMemoryBank:DataPath</c>, which the host reads before <c>BMB_DATA_PATH</c>), so that a
    /// test host that names its folder reaches its stand-in node and not whatever the process environment says.
    /// </summary>
    public static NodeLanClient For(IHttpClientFactory httpClientFactory, Microsoft.Extensions.Configuration.IConfiguration? configuration) =>
        new(httpClientFactory.CreateClient(), dataPath: configuration?["BeeMemoryBank:DataPath"]);

    public Task<LanStatus> GetAsync(CancellationToken ct = default) => SendAsync(HttpMethod.Get, "", null, ct);
    public Task<LanStatus> EnableAsync(CancellationToken ct = default) => SendAsync(HttpMethod.Post, "/enable", null, ct);
    public Task<LanStatus> DisableAsync(CancellationToken ct = default) => SendAsync(HttpMethod.Post, "/disable", null, ct);
    public Task<LanStatus> AddFirewallRuleAsync(CancellationToken ct = default) => SendAsync(HttpMethod.Post, "/firewall", null, ct);
    public Task<LanStatus> RemoveFirewallRuleAsync(CancellationToken ct = default) => SendAsync(HttpMethod.Post, "/firewall/remove", null, ct);

    /// <summary>Switches "Devices on my network". The answer carries <see cref="LanStatus.Error"/> when the node refused or could not.</summary>
    public Task<LanStatus> SetDevicesOnMyNetworkAsync(bool enabled, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, "/network", new { enabled }, ct);

    /// <summary>Switches "Sync with your devices on this network" (the sync surface of the LAN listener).</summary>
    public Task<LanStatus> SetLanSyncAsync(bool enabled, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, "/sync", new { enabled }, ct);

    /// <summary>Moves every LAN surface to another port.</summary>
    public Task<LanStatus> SetLanPortAsync(int port, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, "/port", new { port }, ct);

    /// <summary>The person's answer about one network: trusted, "not this network", or forgotten (null).</summary>
    public Task<LanStatus> AnswerNetworkAsync(string fingerprint, bool? trusted, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, "/networks", new { fingerprint, trusted }, ct);

    /// <summary>The one-time notice about LAN sync was read.</summary>
    public Task<LanStatus> DismissLanNoticeAsync(CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, "/notice/dismiss", null, ct);

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
            return LanStatus.Unavailable with { Error = $"The node could not be reached ({BeeMemoryBank.Sync.FailureText.Say(ex)})." };
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
        string? Error = null, string Setting = "off", string Platform = "other", bool FirewallManaged = false, LanSyncStatus? LanSync = null)
    {
        public static readonly LanStatus Unavailable = new("unavailable", false, null, null, null, 0, false);

        public bool IsAvailable => Mode != "unavailable";
    }

    /// <summary>The sync surface (docs/design/lan-peer-sync.md 6): its setting ("on", "off", "environment"), where it answers now, the port, and the networks.</summary>
    public sealed record LanSyncStatus(
        string Setting, IReadOnlyList<string> Serving, int Port, string PortSource, string? BindError, bool NoticePending,
        IReadOnlyList<LanNetwork> Networks, IReadOnlyList<LanNetworkAnswer> Answers, bool FirewallLegacyRule = false);

    /// <summary>One network this computer is on. <c>Reason</c>: windows-private, windows-domain, windows-public, trusted, refused, unknown, pending, vpn-off, virtual.</summary>
    public sealed record LanNetwork(string Interface, string Address, string Label, string? Fingerprint, bool Trusted, string Reason, bool CanAsk, string Kind);

    /// <summary>An answer the person gave about a network.</summary>
    public sealed record LanNetworkAnswer(string Fingerprint, string Label, bool Trusted, string How, DateTime At);
}
