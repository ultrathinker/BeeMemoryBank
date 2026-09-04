using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BeeMemoryBank.Web.Models;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Web.Services;

public partial class ApiClient(HttpClient http)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    // Auth headers (X-Internal-Key, X-User-Role) are added automatically
    // by InternalKeyHandler registered as a DelegatingHandler on the HttpClient.

    // ─── Init ────────────────────────────────────────────────────────────────

    public async Task<bool?> GetInitStatusAsync()
    {
        try
        {
            var resp = await http.GetFromJsonAsync<InitStatusDto>("/api/init/status", JsonOpts);
            return resp?.Initialized;
        }
        catch
        {
            return null; // API unreachable — unknown state
        }
    }

    public async Task<(bool Ok, string? Error)> InitStandaloneAsync(string adminUsername, string displayName, string password)
    {
        var resp = await http.PostAsync("/api/init/standalone",
            Body(new { adminUsername, displayName, password }));
        if (resp.IsSuccessStatusCode)
            return (true, null);
        var body = await resp.Content.ReadAsStringAsync();
        string error;
        try
        {
            var doc = JsonDocument.Parse(body);
            error = doc.RootElement.GetProperty("error").GetString() ?? "Initialization failed";
        }
        catch { error = "Initialization failed"; }
        return (false, error);
    }

    public async Task<(bool Ok, string? Error)> InitJoinAsync(string adminUsername, string displayName, string remoteUrl, string password)
    {
        var resp = await http.PostAsync("/api/init/join",
            Body(new { adminUsername, displayName, remoteUrl, password }));
        if (resp.IsSuccessStatusCode)
            return (true, null);
        var body = await resp.Content.ReadAsStringAsync();
        string error;
        try
        {
            var doc = JsonDocument.Parse(body);
            error = doc.RootElement.GetProperty("error").GetString() ?? "Join failed";
        }
        catch { error = "Join failed"; }
        return (false, error);
    }

    public async Task<(bool Ok, string? Error)> ResetNodeAsync(string masterPassword)
    {
        var resp = await http.PostAsJsonAsync("/api/init/reset",
            new { masterPassword }, JsonOpts);
        if (resp.IsSuccessStatusCode) return (true, null);
        var err = await resp.Content.ReadAsStringAsync();
        return (false, err);
    }

    // ─── Session ──────────────────────────────────────────────────────────────

    public async Task<bool> UnlockAsync(string password)
    {
        var resp = await http.PostAsync("/api/session/unlock", Body(new { password }));
        return resp.IsSuccessStatusCode;
    }

    public async Task<LoginResult> LoginAsync(string username, string password)
    {
        var resp = await http.PostAsync("/api/session/login", Body(new { username, password }));
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync();
            string error;
            string? code = null;
            try
            {
                var errorDoc = JsonDocument.Parse(body);
                error = errorDoc.RootElement.GetProperty("error").GetString() ?? "Login failed";
                if (errorDoc.RootElement.TryGetProperty("code", out var codeProp))
                    code = codeProp.GetString();
            }
            catch
            {
                error = "Login failed";
            }
            // The API says which refusal this is in `code`; the message text is for the user, not
            // for us. The text match stays as a fallback only because Web and Api are separate
            // containers in the docker deployment and can briefly run different builds during a
            // rolling restart — an Api that predates ErrorCodes sends no code at all.
            var isLocked = resp.StatusCode == System.Net.HttpStatusCode.Forbidden
                && (code == "session_locked" || (code == null && error.Contains("locked")));
            return new LoginResult(false, error, isLocked, null, null, null, null, null, null);
        }
        var result = await resp.Content.ReadFromJsonAsync<LoginResponse>(JsonOpts);
        return new LoginResult(true, null, false, result!.Username, result.DisplayName, result.Role, result.UserId.ToString(), result.MigratedSyntheticUsername, result.SecurityStamp);
    }

    public async Task LockAsync()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/session/lock");

        await http.SendAsync(request);
    }

    /// <summary>
    /// Clears this node's "a peer changed the master password" banner. The operator is asserting
    /// that the announced change is one they made, which no timestamp on the event can establish
    /// — see POST /api/keys/password-notice/dismiss.
    /// </summary>
    public async Task<bool> DismissMasterPasswordNoticeAsync()
    {
        var resp = await http.PostAsync("/api/keys/password-notice/dismiss", content: null);
        return resp.IsSuccessStatusCode;
    }

    /// <summary>
    /// What can undo a Lock on this node: the superadmin-owned agent keys that re-unlock the
    /// process on their next request, plus whether OS auto-unlock is on. Returns null when the
    /// API cannot answer, so the caller can stay silent rather than promise "nothing can".
    /// </summary>
    public async Task<LockImpactDto?> GetLockImpactAsync()
    {
        try
        {
            return await http.GetFromJsonAsync<LockImpactDto>("/api/session/lock-impact", JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> IsUnlockedAsync()
    {
        var resp = await http.GetFromJsonAsync<SessionStatusDto>("/api/session/status", JsonOpts);
        return resp?.IsUnlocked ?? false;
    }

    public async Task<SessionSettingsDto?> GetSessionSettingsAsync()
    {
        try
        {
            return await http.GetFromJsonAsync<SessionSettingsDto>("/api/session/settings", JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    public async Task<(bool Ok, string? Error)> SetSessionSettingsAsync(int expireHours, bool slidingExpiration)
    {
        var resp = await http.PutAsJsonAsync("/api/session/settings",
            new { expireHours, slidingExpiration }, JsonOpts);
        if (resp.IsSuccessStatusCode) return (true, null);
        var error = await TryReadErrorAsync(resp);
        return (false, error ?? $"HTTP {(int)resp.StatusCode}");
    }

    // W3 (Option A): fetch this user's node-local security stamp for cookie revalidation.
    // userId is passed EXPLICITLY (not read from HttpContext) because this is called from
    // OnValidatePrincipal, where HttpContext.User is not yet the authenticated principal —
    // so InternalKeyHandler cannot forward X-User-Id. We set it manually here; InternalKeyHandler
    // still injects X-Internal-Key (from env) and skips its own X-User-Id when it is absent
    // (HttpContext.User is empty during validation, and now also skips it if the header was
    // already set by this caller — see InternalKeyHandler).
    //
    // F2: returns a tri-state outcome so OnValidatePrincipal can distinguish an AUTHORITATIVE
    // "user no longer exists" (HTTP 404 → must REJECT) from a transport error / 5xx (→ FAIL OPEN).
    // Previously both collapsed to null → fail-open, which wrongly kept a deleted/demoted user's
    // session alive on a 404.
    //   Found        — 200 with a real stamp body; compare against the cookie claim.
    //   NotFound     — HTTP 404; the API definitively says the user is gone → RejectPrincipal.
    //   Unavailable  — transport error / 5xx / other non-success / malformed 200 body → fail OPEN.
    public async Task<SecurityStampLookup> GetSecurityStampAsync(int userId)
    {
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "/api/users/me/stamp");
            req.Headers.TryAddWithoutValidation("X-User-Id", userId.ToString());
            var resp = await http.SendAsync(req);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                return new SecurityStampLookup(SecurityStampLookupOutcome.NotFound, null);
            if (!resp.IsSuccessStatusCode)
                return new SecurityStampLookup(SecurityStampLookupOutcome.Unavailable, null);
            var body = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
            var stamp = body.TryGetProperty("stamp", out var s) ? s.GetString() : null;
            if (string.IsNullOrEmpty(stamp))
                return new SecurityStampLookup(SecurityStampLookupOutcome.Unavailable, null);
            return new SecurityStampLookup(SecurityStampLookupOutcome.Found, stamp);
        }
        catch
        {
            return new SecurityStampLookup(SecurityStampLookupOutcome.Unavailable, null);
        }
    }

    public async Task<(bool Ok, string? Body, int Status)> PostRawAsync(string path, string json)
        => await PostRawAsync(path, json, method: "POST");

    /// <summary>Generic JSON passthrough with a selectable HTTP method (POST/PATCH/PUT/DELETE).
    /// Identity headers are injected by InternalKeyHandler. Used by the AI chat proxy routes that
    /// need PATCH/DELETE on /api/chat/*. Keeps status + body verbatim.</summary>
    public async Task<(bool Ok, string? Body, int Status)> PostRawAsync(string path, string json, string method)
    {
        var req = new HttpRequestMessage(new HttpMethod(method), "/api/" + path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        var resp = await http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        return (resp.IsSuccessStatusCode, body, (int)resp.StatusCode);
    }

    // W2: unified pass-through for the hand-written proxy routes that survive W1. Reads the
    // upstream response and preserves status + body + content-type VERBATIM, so an API 403/409
    // (and its error text) reaches the browser unchanged instead of being collapsed to a 502.
    // Identity headers (X-Internal-Key / X-User-*) are still injected by InternalKeyHandler.
    // F3: guard the send so that an API-down (HttpRequestException / TaskCanceledException)
    // returns a graceful 502 like the W1 catch-all forwarder, instead of bubbling up as an
    // unhandled 500.
    public async Task<(int Status, string Body, string? ContentType)> ForwardGetAsync(string path)
    {
        try
        {
            var resp = await http.GetAsync("/api/" + path);
            var body = await resp.Content.ReadAsStringAsync();
            var contentType = resp.Content.Headers.ContentType?.MediaType;
            return ((int)resp.StatusCode, body, contentType);
        }
        catch (HttpRequestException) { return (502, "", null); }
        catch (TaskCanceledException) { return (502, "", null); }
    }

    // W1: low-level forward used by the catch-all forwarder. Sends an already-built
    // HttpRequestMessage through this client (so InternalKeyHandler still injects identity
    // headers) and returns the raw upstream response. The caller copies status/body/headers.
    public Task<HttpResponseMessage> SendForwardAsync(HttpRequestMessage request) =>
        http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

    // Phase 2: same as above but forwards the caller's CancellationToken so a browser disconnect
    // (ctx.RequestAborted) cancels the upstream API call. Used ONLY by the dedicated SSE streaming
    // passthrough — the W1 catch-all keeps the parameterless overload. ResponseHeadersRead means the
    // timeout/return is at headers, then the body streams unbounded (plan §2 Phase 2, §6).
    public Task<HttpResponseMessage> SendForwardAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

    private async Task<string?> TryReadErrorAsync(HttpResponseMessage resp)
    {
        try
        {
            var err = await resp.Content.ReadFromJsonAsync<JsonNode>(JsonOpts);
            return err?["error"]?.GetValue<string>();
        }
        catch { return null; }
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static StringContent Body(object obj) =>
        new(JsonSerializer.Serialize(obj, JsonOpts), Encoding.UTF8, "application/json");

    private static async Task<string?> ReadErrorAsync(HttpResponseMessage resp)
    {
        var body = await resp.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e))
                return e.GetString();
        }
        catch { }
        return null;
    }
}

// W3 (Option A) / F2: tri-state outcome for GetSecurityStampAsync, so OnValidatePrincipal can
// distinguish an authoritative "user gone" (NotFound → reject) from a transport error / 5xx
// (Unavailable → fail open) from a real lookup (Found → compare stamps).
public enum SecurityStampLookupOutcome { Found, NotFound, Unavailable }

public sealed record SecurityStampLookup(SecurityStampLookupOutcome Outcome, string? Stamp);
