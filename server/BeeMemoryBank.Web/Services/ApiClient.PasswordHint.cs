using System.Net;
using System.Net.Http.Json;
using BeeMemoryBank.Web.Models;

namespace BeeMemoryBank.Web.Services;

public partial class ApiClient
{
    /// <summary>
    /// "Show my password hint" on the sign-in page: anonymous on purpose (the person cannot sign in, which is why
    /// they need it), so the browser never calls this directly - the Razor page posts to the Web server, which calls
    /// the node with this method. The node answers an unknown name and a user without a hint identically, and this
    /// method keeps that one answer as <see cref="PasswordHintLookupOutcome.NoHint"/>.
    /// </summary>
    public async Task<PasswordHintLookupResult> LookupPasswordHintAsync(string username, string? clientIp)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await http.PostAsync("/api/session/password-hint",
                Body(new { username, clientIp }));
        }
        catch (HttpRequestException)
        {
            return new PasswordHintLookupResult(PasswordHintLookupOutcome.Unavailable);
        }

        using (resp)
        {
            switch (resp.StatusCode)
            {
                case HttpStatusCode.OK:
                    var body = await resp.Content.ReadFromJsonAsync<PasswordHintResponseDto>(JsonOpts);
                    return body?.Hint is { Length: > 0 } hint
                        ? new PasswordHintLookupResult(PasswordHintLookupOutcome.Found, hint)
                        : new PasswordHintLookupResult(PasswordHintLookupOutcome.NoHint, Message: body?.Message);
                case HttpStatusCode.TooManyRequests:
                    return new PasswordHintLookupResult(PasswordHintLookupOutcome.TooManyAttempts);
                case HttpStatusCode.BadRequest:
                    return new PasswordHintLookupResult(PasswordHintLookupOutcome.Invalid, Message: await ReadErrorAsync(resp));
                default:
                    return new PasswordHintLookupResult(PasswordHintLookupOutcome.Unavailable);
            }
        }
    }

    /// <summary>The signed-in caller's own hint, or null when they have none. Null also when the node did not answer.</summary>
    public async Task<string?> GetMyPasswordHintAsync()
    {
        try
        {
            var body = await http.GetFromJsonAsync<CurrentPasswordHintDto>("/api/users/me/password-hint", JsonOpts);
            return body?.PasswordHint;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    /// <summary>Adds or replaces the signed-in caller's own hint (the Profile page's soft prompt).</summary>
    public async Task<(bool Ok, string? Error)> SetMyPasswordHintAsync(string passwordHint)
    {
        var resp = await http.PostAsync("/api/users/me/password-hint", Body(new { passwordHint }));
        if (resp.IsSuccessStatusCode) return (true, null);
        return (false, await ReadErrorAsync(resp));
    }

    // ─── Recovery-key step (release 2.7.5) ───────────────────────────────────

    /// <summary>The node's recovery-key state; null when the node did not answer (the gates then stay out of the way).</summary>
    public async Task<RecoveryKeyStatusDto?> GetRecoveryKeyStatusAsync()
    {
        try
        {
            return await http.GetFromJsonAsync<RecoveryKeyStatusDto>("/api/keys/recovery-status", JsonOpts);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Issues a new recovery key to the vault (the existing /api/keys/add-recovery, superadmin and an unlocked
    /// session). The key is returned to the caller once and is never stored in recoverable form anywhere.
    /// </summary>
    public async Task<(string? Key, string? Error)> AddRecoveryKeyAsync()
    {
        var resp = await http.PostAsync("/api/keys/add-recovery", content: null);
        if (!resp.IsSuccessStatusCode)
            return (null, await ReadErrorAsync(resp) ?? "The node refused to issue a recovery key.");
        var body = await resp.Content.ReadFromJsonAsync<RecoveryKeyDto>(JsonOpts);
        return body?.RecoveryKey is { Length: > 0 } key
            ? (key, null)
            : (null, "The node did not return a recovery key.");
    }

    /// <summary>The person confirmed they saved the key; idempotent on the node.</summary>
    public async Task<bool> AcknowledgeRecoveryKeyAsync()
    {
        var resp = await http.PostAsync("/api/keys/recovery-acknowledge", content: null);
        return resp.IsSuccessStatusCode;
    }

    /// <summary>The operator dismissed the "no recovery key has been issued" notice.</summary>
    public async Task<bool> DismissRecoveryKeyNoticeAsync()
    {
        var resp = await http.PostAsync("/api/keys/recovery-notice/dismiss", content: null);
        return resp.IsSuccessStatusCode;
    }

    private sealed record PasswordHintResponseDto(string? Hint, string? Message);
    private sealed record CurrentPasswordHintDto(string? PasswordHint);
    private sealed record RecoveryKeyDto(string RecoveryKey);
}