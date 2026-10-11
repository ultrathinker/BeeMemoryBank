using System.Net;
using System.Text.Json.Serialization;

namespace BeeMemoryBank.Web.Services;

public partial class ApiClient
{
    // ─── The encrypted database (docs/design/step-b-spec.md 6.7, 10, 11.2) ──────────────────────────────────────────

    /// <summary>What the node says about its database without anybody signed in; null when the API did not answer.</summary>
    public async Task<VaultStatusDto?> GetVaultStatusAsync()
    {
        try
        {
            return await http.GetFromJsonAsync<VaultStatusDto>("/api/vault/status", JsonOpts);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>The preflight of "Encrypt this node"; null when the API did not answer.</summary>
    public async Task<SealPreflightDto?> GetSealPreflightAsync(bool acceptNoSnapshot)
    {
        try
        {
            return await http.GetFromJsonAsync<SealPreflightDto>("/api/vault/seal/preflight" + (acceptNoSnapshot ? "?accept_no_snapshot=true" : ""), JsonOpts);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>Starts the migration. The page then follows <c>/api-proxy/vault/seal/progress</c>.</summary>
    public async Task<(bool Ok, string? Error)> StartSealAsync(string password, bool acceptNoSnapshot)
    {
        var resp = await http.PostAsJsonAsync("/api/vault/seal", new { password, accept_no_snapshot = acceptNoSnapshot }, JsonOpts);
        if (resp.IsSuccessStatusCode) return (true, null);
        return (false, resp.StatusCode == HttpStatusCode.Unauthorized ? "Invalid master password." : await ReadErrorAsync(resp) ?? "The encryption could not be started.");
    }

    /// <summary>Erases the listed unencrypted copies. Null when the password is wrong or the API did not answer.</summary>
    public async Task<(EraseResultDto? Result, string? Error)> ErasePlainCopiesAsync(string password, IReadOnlyList<string> paths)
    {
        var resp = await http.PostAsJsonAsync("/api/vault/seal/erase-plain-copies", new { password, paths }, JsonOpts);
        if (resp.StatusCode == HttpStatusCode.Unauthorized) return (null, "Invalid master password.");
        if (!resp.IsSuccessStatusCode) return (null, await ReadErrorAsync(resp) ?? "The copies could not be erased.");
        return (await resp.Content.ReadFromJsonAsync<EraseResultDto>(JsonOpts), null);
    }

    /// <summary>The way-back snapshot of the encryption (name, size, date), for a signed-in superadmin only.</summary>
    public async Task<WayBackDto?> GetWayBackAsync()
    {
        try
        {
            return await http.GetFromJsonAsync<WayBackDto>("/api/vault/pre-encryption", JsonOpts);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public async Task<(bool Ok, string? Error)> DeleteWayBackAsync(string password)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, "/api/vault/pre-encryption") { Content = JsonContent.Create(new { password }, options: JsonOpts) };
        var resp = await http.SendAsync(request);
        if (resp.IsSuccessStatusCode) return (true, null);
        return (false, resp.StatusCode == HttpStatusCode.Unauthorized ? "Invalid master password." : await ReadErrorAsync(resp) ?? "The snapshot could not be deleted.");
    }

    /// <summary>"Start unlocked after a restart" of a server (spec 11.2); null when the API did not answer or the node is a desktop.</summary>
    public async Task<ServerAutoUnlockDto?> GetServerAutoUnlockAsync()
    {
        try
        {
            return await http.GetFromJsonAsync<ServerAutoUnlockDto>("/api/keys/server-auto-unlock/status", JsonOpts);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public async Task<(bool Ok, string? Error)> EnableServerAutoUnlockAsync(string password, string confirmation)
    {
        var resp = await http.PostAsJsonAsync("/api/keys/server-auto-unlock/enable", new { password, confirmation }, JsonOpts);
        if (resp.IsSuccessStatusCode) return (true, null);
        return (false, resp.StatusCode == HttpStatusCode.Unauthorized ? "Invalid master password." : await ReadErrorAsync(resp) ?? "Auto-unlock could not be turned on.");
    }

    public async Task<(bool Ok, string? Error)> DisableServerAutoUnlockAsync(string password)
    {
        var resp = await http.PostAsJsonAsync("/api/keys/server-auto-unlock/disable", new { password }, JsonOpts);
        if (resp.IsSuccessStatusCode) return (true, null);
        return (false, resp.StatusCode == HttpStatusCode.Unauthorized ? "Invalid master password." : await ReadErrorAsync(resp) ?? "Auto-unlock could not be turned off.");
    }
}

public sealed record VaultInboxDto(
    [property: JsonPropertyName("events")] long Events,
    [property: JsonPropertyName("blobs")] long Blobs);

public sealed record VaultAutoUnlockDto(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("os")] bool Os,
    [property: JsonPropertyName("server")] bool Server);

public sealed record VaultMigrationDto(
    [property: JsonPropertyName("phase")] string? Phase,
    [property: JsonPropertyName("wipe_pending")] List<string>? WipePending,
    [property: JsonPropertyName("linked_plain")] List<string>? LinkedPlain);

/// <summary><c>GET /api/vault/status</c>: no path, no size of a plain file, no count of anything inside the database, no key.</summary>
public sealed record VaultStatusDto(
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("sealed_at")] string? SealedAt,
    [property: JsonPropertyName("inbox")] VaultInboxDto? Inbox,
    [property: JsonPropertyName("auto_unlock")] VaultAutoUnlockDto? AutoUnlock,
    [property: JsonPropertyName("last_refusal")] string? LastRefusal,
    [property: JsonPropertyName("migration")] VaultMigrationDto? Migration)
{
    public bool IsSealed => Mode == "sealed";

    /// <summary>A sealed node whose database is not open: the login page is the way in.</summary>
    public bool IsClosed => IsSealed && State is "Closed" or "Opening" or "Closing";
}

public sealed record SealBlockerDto(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);

public sealed record PlainCopyDto(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("bytes")] long Bytes,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("detail")] string? Detail,
    [property: JsonPropertyName("can_be_erased")] bool CanBeErased);

public sealed record SealPreflightDto(
    [property: JsonPropertyName("can_start")] bool CanStart,
    [property: JsonPropertyName("blockers")] List<SealBlockerDto> Blockers,
    [property: JsonPropertyName("warnings")] List<SealBlockerDto> Warnings,
    [property: JsonPropertyName("plain_copies")] List<PlainCopyDto> PlainCopies,
    [property: JsonPropertyName("database_bytes")] long DatabaseBytes);

public sealed record EraseRefusalDto(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("kind")] string Kind);

public sealed record EraseResultDto(
    [property: JsonPropertyName("erased")] List<string> Erased,
    [property: JsonPropertyName("unlinked_only")] List<string> UnlinkedOnly,
    [property: JsonPropertyName("not_erased")] List<string> NotErased,
    [property: JsonPropertyName("refused")] List<EraseRefusalDto> Refused);

public sealed record WayBackDto(
    [property: JsonPropertyName("exists")] bool Exists,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("bytes")] long Bytes,
    [property: JsonPropertyName("created_at")] DateTime? CreatedAt);

public sealed record ServerAutoUnlockDto(string Role, bool Enabled, string State, bool KeyFileConfigured, string? Notice);
