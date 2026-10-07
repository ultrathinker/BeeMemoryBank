using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BeeMemoryBank.Web.Models;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Web.Services;

public partial class ApiClient
{
    // ─── Software updates ───────────────────────────────────────────────────────

    public async Task<string?> GetServerVersionAsync()
    {
        try
        {
            var el = await http.GetFromJsonAsync<JsonElement>("/api/version", JsonOpts);
            return el.TryGetProperty("version", out var v) ? v.GetString() : null;
        }
        catch { return null; }
    }

    public async Task<JsonElement?> CheckForUpdatesAsync(string manifestJson, string manifestSignatureBase64)
    {
        try
        {
            var resp = await http.PostAsJsonAsync("/node/update/check", new { manifestJson, manifestSignatureBase64 }, JsonOpts);
            if (resp.IsSuccessStatusCode)
                return await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
            return null;
        }
        catch { return null; }
    }

    public async Task<JsonElement?> DownloadUpdateAsync(string manifestJson)
    {
        try
        {
            var resp = await http.PostAsJsonAsync("/node/update/download", new { manifestJson }, JsonOpts);
            if (resp.IsSuccessStatusCode)
                return await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
            return null;
        }
        catch { return null; }
    }

    public async Task<bool> ApplyUpdateAsync()
    {
        try
        {
            var resp = await http.PostAsync("/node/update/apply", null);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<JsonElement?> GetUpdateStatusAsync()
    {
        try { return await http.GetFromJsonAsync<JsonElement>("/node/update/status", JsonOpts); }
        catch { return null; }
    }

    public async Task<bool> ResetUpdateAsync()
    {
        try
        {
            var resp = await http.PostAsync("/node/update/reset", null);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<List<SnapshotDto>?> GetSnapshotsAsync() =>
        await http.GetFromJsonAsync<List<SnapshotDto>>("/api/snapshots", JsonOpts);

    public async Task<SnapshotDto?> CreateSnapshotAsync()
    {
        var resp = await http.PostAsync("/api/snapshots", null);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<SnapshotDto>(JsonOpts);
    }

    public async Task<SnapshotUploadDto?> UploadSnapshotAsync(IFormFile file)
    {
        using var content = new MultipartFormDataContent();
        using var streamContent = new StreamContent(file.OpenReadStream());
        content.Add(streamContent, "file", file.FileName);
        var resp = await http.PostAsync("/api/snapshots/upload", content);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<SnapshotUploadDto>(JsonOpts);
    }

    /// <summary>
    /// Starts a restore of the whole network from a snapshot of this node, by file name. The master password
    /// is re-checked by the API; on failure the error is the API's own sentence, not its JSON.
    /// </summary>
    public async Task<(bool ok, string? eventId, string? error)> InitiateNetworkRestoreAsync(string fileName, string masterPassword)
    {
        var resp = await http.PostAsJsonAsync("/api/snapshots/restore-network", new {
            SnapshotFileId = Guid.Empty,
            Mode = "NetworkWide",
            ForeignMasterPassword = (string?)null,
            MasterPassword = masterPassword,
            FileName = fileName
        }, JsonOpts);
        if (!resp.IsSuccessStatusCode)
            return (false, null, ErrorTextOf(await resp.Content.ReadAsStringAsync()) ?? "Failed to initiate network restore");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        return (true, body.GetProperty("eventId").GetString(), null);
    }

    // The "error" of an API error body ({"error":"..."}), or the body itself when it is plain text.
    private static string? ErrorTextOf(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String)
                return e.GetString();
            if (doc.RootElement.ValueKind == JsonValueKind.String)
                return doc.RootElement.GetString();
        }
        catch (JsonException) { }
        return body;
    }

    public async Task<RestoreProgressDto?> GetRestoreProgressAsync()
    {
        var resp = await http.GetAsync("/api/snapshots/restore/progress");
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<RestoreProgressDto>(JsonOpts);
    }

    public async Task<bool> ContinueRestoreWithoutBackupAsync(Guid eventId, string masterPassword)
    {
        var resp = await http.PostAsJsonAsync("/api/snapshots/restore/continue-without-backup",
            new { EventId = eventId, MasterPassword = masterPassword }, JsonOpts);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> CancelRestoreAsync(string eventId)
    {
        var resp = await http.PostAsync($"/api/snapshots/restore/cancel?eventId={Uri.EscapeDataString(eventId)}", null);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> DeleteSnapshotAsync(string fileName)
    {
        var resp = await http.DeleteAsync(
            $"/api/snapshots/{Uri.EscapeDataString(fileName)}");
        return resp.IsSuccessStatusCode;
    }

    public async Task<HttpResponseMessage?> DownloadSnapshotAsync(string fileName)
    {
        try
        {
            return await http.GetAsync(
                $"/api/snapshots/{Uri.EscapeDataString(fileName)}/download",
                HttpCompletionOption.ResponseHeadersRead);
        }
        catch { return null; }
    }

    /// <param name="mode">What the restore does to this node's identity: see <see cref="RestoreModes"/>.</param>
    public async Task<(bool ok, string? error, string? backupFileName)> RestoreSnapshotAsync(
        string fileName, string masterPassword, bool createBackupFirst = true, string mode = RestoreModes.Standalone)
    {
        var resp = await http.PostAsync("/api/snapshots/restore",
            Body(new { fileName, masterPassword, createBackupFirst, mode }));
        if (resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
            var backup = body.TryGetProperty("backupFileName", out var bp) ? bp.GetString() : null;
            return (true, null, backup);
        }
        var errBody = await resp.Content.ReadAsStringAsync();
        string error;
        try
        {
            var doc = JsonDocument.Parse(errBody);
            error = doc.RootElement.GetProperty("error").GetString() ?? "Restore failed";
        }
        catch { error = "Restore failed"; }
        return (false, error, null);
    }

    // ─── DEK Rotation ─────────────────────────────────────────────────────────

    public async Task<DekRotationProgressDto?> GetDekRotationProgressAsync()
    {
        var resp = await http.GetAsync("/api/dek-rotation/progress");
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<DekRotationProgressDto>(JsonOpts);
    }

    public async Task<(bool Ok, string? CommitEventId, string? Error)> ProposeDekRotationAsync(string masterPassword)
    {
        var resp = await http.PostAsync("/api/dek-rotation/propose",
            Body(new { masterPassword }));
        if (resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
            var commitId = body.TryGetProperty("commitEventId", out var cid) ? cid.GetString() : null;
            return (true, commitId, null);
        }
        var errBody = await resp.Content.ReadAsStringAsync();
        string error;
        try
        {
            var doc = JsonDocument.Parse(errBody);
            error = doc.RootElement.GetProperty("error").GetString() ?? "DEK rotation propose failed";
        }
        catch { error = "DEK rotation propose failed"; }
        return (false, null, error);
    }

    public async Task<(bool Ok, string? Error)> AcceptDekRotationAsync(string commitEventId, string masterPassword)
    {
        var resp = await http.PostAsync("/api/dek-rotation/accept",
            Body(new { commitEventId, masterPassword }));
        if (resp.IsSuccessStatusCode) return (true, null);
        var errBody = await resp.Content.ReadAsStringAsync();
        string error;
        try
        {
            var doc = JsonDocument.Parse(errBody);
            error = doc.RootElement.GetProperty("error").GetString() ?? "DEK rotation accept failed";
        }
        catch { error = "DEK rotation accept failed"; }
        return (false, error);
    }

    public async Task<(bool Ok, string? Error)> CancelDekRotationAsync(string eventId)
    {
        var resp = await http.PostAsync($"/api/dek-rotation/cancel/{Uri.EscapeDataString(eventId)}", null);
        if (resp.IsSuccessStatusCode) return (true, null);
        var errBody = await resp.Content.ReadAsStringAsync();
        string error;
        try
        {
            var doc = JsonDocument.Parse(errBody);
            error = doc.RootElement.GetProperty("error").GetString() ?? "DEK rotation cancel failed";
        }
        catch { error = "DEK rotation cancel failed"; }
        return (false, error);
    }

    public async Task<List<PeerPendingDekRotationDto>?> GetPeerPendingDekRotationsAsync()
    {
        try
        {
            return await http.GetFromJsonAsync<List<PeerPendingDekRotationDto>>("/api/dek-rotation/peer-pending", JsonOpts);
        }
        catch { return null; }
    }

    public async Task<(bool Ok, string? Error)> PeerAcceptDekRotationAsync(string eventId)
    {
        var resp = await http.PostAsync($"/api/dek-rotation/peer-accept/{Uri.EscapeDataString(eventId)}", null);
        if (resp.IsSuccessStatusCode) return (true, null);
        var errBody = await resp.Content.ReadAsStringAsync();
        string error;
        try
        {
            var doc = JsonDocument.Parse(errBody);
            error = doc.RootElement.GetProperty("error").GetString() ?? "Peer accept failed";
        }
        catch { error = "Peer accept failed"; }
        return (false, error);
    }

    public async Task<(bool Ok, string? Error)> PeerRejectDekRotationAsync(string eventId)
    {
        var resp = await http.PostAsync($"/api/dek-rotation/peer-reject/{Uri.EscapeDataString(eventId)}", null);
        if (resp.IsSuccessStatusCode) return (true, null);
        var errBody = await resp.Content.ReadAsStringAsync();
        string error;
        try
        {
            var doc = JsonDocument.Parse(errBody);
            error = doc.RootElement.GetProperty("error").GetString() ?? "Peer reject failed";
        }
        catch { error = "Peer reject failed"; }
        return (false, error);
    }

    // ─── Activity ─────────────────────────────────────────────────────────────

    public async Task<CompactionPreviewDto?> GetCompactionPreviewAsync()
    {
        try
        {
            return await http.GetFromJsonAsync<CompactionPreviewDto>("/api/admin/compact/preview", JsonOpts);
        }
        catch { return null; }
    }

    public async Task<(bool Ok, string? Error, CompactionResultDto? Result)> CompactAsync(long? explicitCp = null, string reason = "manual")
    {
        var resp = await http.PostAsJsonAsync("/api/admin/compact",
            new { explicitCp, reason }, JsonOpts);
        if (resp.IsSuccessStatusCode)
        {
            var result = await resp.Content.ReadFromJsonAsync<CompactionResultDto>(JsonOpts);
            return (true, null, result);
        }
        var err = await resp.Content.ReadAsStringAsync();
        return (false, err, null);
    }

    public async Task<List<SnapshotCheckpointDto>?> GetSnapshotCheckpointsAsync()
    {
        try
        {
            return await http.GetFromJsonAsync<List<SnapshotCheckpointDto>>("/api/admin/compact/checkpoints", JsonOpts);
        }
        catch { return null; }
    }
}
