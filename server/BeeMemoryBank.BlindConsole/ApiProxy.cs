using System.Net.Http.Headers;
using System.Text;

namespace BeeMemoryBank.BlindConsole;

/// <summary>
/// The console's one client for the Api: every forwarded call carries the node's internal key,
/// which never leaves this process (the page's JavaScript talks only to the console itself).
/// An interface so tests can substitute canned Api answers without a socket.
/// </summary>
public interface IApiProxy
{
    Task<(int Status, string Content, string ContentType)> SendAsync(
        string method, string path, string? jsonBody, CancellationToken ct);
}

public sealed class ApiProxy(string baseUrl, string internalKey, HttpMessageHandler? handler = null) : IApiProxy
{
    public const string CookieName = "bmb_console_session";

    private readonly HttpClient _http = new(handler ?? new SocketsHttpHandler()) { BaseAddress = new Uri(baseUrl) };

    public async Task<(int Status, string Content, string ContentType)> SendAsync(
        string method, string path, string? jsonBody, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(new HttpMethod(method), path);
        req.Headers.Add("X-Internal-Key", internalKey);
        // The console is this node's local administrator: every route it may reach is gated
        // .RequireSuperadmin() Api-side, and the Api honours the role only behind the internal
        // key. Who may act through the console is decided here, by the console's own login.
        req.Headers.Add("X-User-Role", "superadmin");
        if (jsonBody is not null)
        {
            req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        var contentType = resp.Content.Headers.ContentType?.MediaType ?? "application/json";
        return ((int)resp.StatusCode, body, contentType);
    }
}
