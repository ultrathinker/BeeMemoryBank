using System.Text.Json;
using BeeMemoryBank.Web.Models;
using BeeMemoryBank.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BeeMemoryBank.Web.Pages;

[Authorize(Roles = "superadmin")]
public class AdminModel(ApiClient api, IHttpClientFactory httpClientFactory) : PageModel
{
    public string? SuccessMessage { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// The card the message of the last action belongs to (the id of its heading, <see cref="AdminCards"/>): the message shows in that card, where the button
    /// was, and not only at the top of a long page. Empty: a message of the page itself (it shows at the top).
    /// </summary>
    public string? MessageCard { get; set; }

    /// <summary>True when the message of the last action is the one of <paramref name="card"/>.</summary>
    public bool MessageIsFor(string card) =>
        !string.IsNullOrEmpty(SuccessMessage ?? ErrorMessage) && string.Equals(MessageCard, card, StringComparison.Ordinal) && CardIsShown(card);

    /// <summary>
    /// True when the message goes to the top of the page: it names no card, or names one this page does not show now (the card of a switch that is not
    /// offered, an empty list), so that a message is never lost.
    /// </summary>
    public bool MessageIsAtTop => string.IsNullOrEmpty(MessageCard) || !CardIsShown(MessageCard);

    private bool CardIsShown(string card) => card switch
    {
        AdminCards.OsAutoUnlock => OsAutoUnlockSupported && (VaultRole != "server" || OsAutoUnlockEnabled),
        AdminCards.RestoredPeers => RestoredPeers.Count > 0,
        _ => AdminCards.All.Contains(card),
    };

    public NodeIdentityDto? Identity { get; set; }
    public List<WhitelistEntryDto>? Whitelist { get; set; }
    public MasterPasswordNoticeDto? MasterPasswordNotice { get; set; }
    public Dictionary<Guid, NodeContactDto>? NodeSyncStatus { get; set; }

    /// <summary>Invisible Mode (BMB-313: it lives here, not in the header): this node calls nobody and answers nobody. Null when the node did not say.</summary>
    public bool? IsInvisible { get; set; }
    public List<SnapshotDto>? Snapshots { get; set; }
    public List<AgentDto>? Agents { get; set; }
    public List<UserDto>? Users { get; set; }
    public JsonElement? GraphEdgeStats { get; set; }
    public string? GraphRebuildReport { get; set; }
    public CompactionPreviewDto? CompactionPreview { get; set; }
    public List<SnapshotCheckpointDto>? SnapshotCheckpoints { get; set; }
    public DekRotationProgressDto? DekRotationProgress { get; set; }
    public string? DekRotationCommitId { get; set; }
    public bool ShowRecoveryKeyReminder { get; set; }
    public List<PeerPendingDekRotationDto> PeerPendingRotations { get; set; } = new();

    /// <summary>Devices a restore kept inactive until a superadmin confirms them (no anchor vouched for them).</summary>
    public List<RestoredPeerDto> RestoredPeers { get; set; } = new();

    public bool OsAutoUnlockEnabled { get; set; }
    public bool OsAutoUnlockSupported { get; set; }

    /// <summary>What the node says about its database (docs/design/step-b-spec.md 6.7); null when it did not answer.</summary>
    public VaultStatusDto? VaultStatus { get; set; }

    /// <summary>The role of this node (spec 11.0): <c>desktop</c> shows the OS auto-unlock card, <c>server</c> sends to the encryption page.</summary>
    public string VaultRole => VaultStatus?.AutoUnlock?.Role ?? "desktop";

    /// <summary>
    /// What can put the master DEK back after a Lock (GET /api/session/lock-impact). Null when
    /// the API could not answer — the Lock card then says nothing rather than claiming the node
    /// is safe.
    /// </summary>
    public LockImpactDto? LockImpact { get; set; }

    /// <summary>
    /// The "Devices on my network" card (Admin > Nodes): what the desktop node says about its network listener, turned into what the
    /// page offers. On a node that is not a desktop node (Docker) it says so, and the card explains what decides instead.
    /// </summary>
    public DevicesOnMyNetworkCard DevicesCard { get; set; } =
        DevicesOnMyNetworkCard.From(NodeLanClient.LanStatus.Unavailable, []);

    public string? CurrentVersion { get; set; }
    public JsonElement? UpdateStatus { get; set; }
    public JsonElement? UpdateCheck { get; set; }
    public string? CheckedManifestJson { get; set; }

    /// <summary>
    /// The "Metadata encryption" card (docs/design/step-a-spec.md 5.3): this node's readiness report. On a page load only
    /// what this node itself observed (it calls nobody); after "Ask every device now" the full report.
    /// </summary>
    public JsonElement? MetadataReadiness { get; set; }
    public bool MetadataReadinessCheckedAll { get; set; }

    /// <summary>When the report of "Ask every device now" was made; the card says how old the answer it shows is.</summary>
    public DateTime? MetadataReadinessCheckedAt { get; set; }

    /// <summary>How long the answer of "Ask every device now" stays on the card (it is kept across a refused switch, so the form does not vanish).</summary>
    public static readonly TimeSpan MetadataReadinessKept = MetadataReadinessCookie.Lifetime;

    /// <summary>The result of "Ask every device now" in a sentence, next to the button; null before anybody was asked.</summary>
    public string? MetadataAskSummary
    {
        get
        {
            if (!MetadataReadinessCheckedAll || MetadataReadiness is not { ValueKind: JsonValueKind.Object } report
                || !report.TryGetProperty("peers", out var peers) || peers.ValueKind != JsonValueKind.Array) return null;
            // The kept report may list only some of the devices (it is bounded to fit a cookie): its own counts are the whole.
            var total = report.TryGetProperty("peer_total", out var pt) && pt.ValueKind == JsonValueKind.Number ? pt.GetInt32() : peers.GetArrayLength();
            var notReady = report.TryGetProperty("peer_not_ready", out var pn) && pn.ValueKind == JsonValueKind.Number
                ? pn.GetInt32()
                : peers.EnumerateArray().Count(p => p.TryGetProperty("status", out var s) && s.GetString() != "ready");
            var result = total == 0 ? "No other device to ask."
                : notReady == 0 ? (total == 1 ? "The other device is ready." : $"All {total} devices are ready.")
                : $"{notReady} of {total} {(total == 1 ? "device is" : "devices are")} not ready.";
            return MetadataReadinessCheckedAt is { } at ? $"{result} Asked {RelativeTime(at)}." : result;
        }
    }

    /// <summary>The held part of the sync status (spec 6.9): changes received and not applied yet.</summary>
    public JsonElement? HeldEvents { get; set; }

    // Search-subsystem latency + index-health diagnostics. Shapes/counts/timings only --
    // the API never returns query text or article content here (see SearchMetricsEndpoints).
    public JsonElement? SearchMetrics { get; set; }

    /// <summary>
    /// Ends a handler: the page opens again at <paramref name="card"/> (a fragment, so the person is where the button was) and the message of the action
    /// shows in that card. A handler without a card of its own passes null and its message shows at the top.
    /// </summary>
    private IActionResult Back(string? card, string? msg = null, string? err = null) =>
        RedirectToPage(null, null, new { msg, err, card }, card);

    public async Task OnGetAsync(string? msg = null, string? err = null, string? card = null)
    {
        SuccessMessage = msg;
        ErrorMessage = err;
        MessageCard = card;
        GraphRebuildReport = TempData["GraphRebuildReport"] as string;
        DekRotationCommitId = TempData.Peek("DekRotationCommitId") as string;
        ShowRecoveryKeyReminder = TempData["RecoveryKeyReminder"] as string == "true";
        CheckedManifestJson = TempData.Peek("CheckedManifestJson") as string;
        if (TempData["UpdateCheck"] is string uc && !string.IsNullOrEmpty(uc))
        {
            try { UpdateCheck = JsonSerializer.Deserialize<JsonElement>(uc); }
            catch { /* stale/garbled TempData — ignore */ }
        }
        await LoadDataAsync();
    }

    public async Task<IActionResult> OnPostCheckUpdateAsync(string? manifestJson, string? manifestSignatureBase64)
    {
        manifestJson ??= "";
        manifestSignatureBase64 ??= "";
        var result = await api.CheckForUpdatesAsync(manifestJson, manifestSignatureBase64);
        TempData["UpdateCheck"] = result?.GetRawText();
        TempData["CheckedManifestJson"] = manifestJson;
        return Back(AdminCards.Updates);
    }

    public async Task<IActionResult> OnPostDownloadUpdateAsync(string? manifestJson)
    {
        manifestJson ??= "";
        await api.DownloadUpdateAsync(manifestJson);
        return Back(AdminCards.Updates);
    }

    public async Task<IActionResult> OnPostApplyUpdateAsync()
    {
        var ok = await api.ApplyUpdateAsync();
        return ok
            ? Back(AdminCards.Updates, msg: "Update triggered. The server is rebuilding and will restart — refresh this page in 30–60 seconds.")
            : Back(AdminCards.Updates, err: "Failed to trigger the update.");
    }

    public async Task<IActionResult> OnPostResetUpdateAsync()
    {
        var ok = await api.ResetUpdateAsync();
        TempData.Remove("UpdateCheck");
        TempData.Remove("CheckedManifestJson");
        return ok
            ? Back(AdminCards.Updates, msg: "Update state machine has been reset.")
            : Back(AdminCards.Updates, err: "Failed to reset update state machine.");
    }

    public async Task<IActionResult> OnPostChangeMasterPasswordAsync(string oldPassword, string newPassword)
    {
        if (string.IsNullOrEmpty(oldPassword) || string.IsNullOrEmpty(newPassword))
            return Back(AdminCards.MasterPassword, err: "Both the current and the new master password are required.");

        var (ok, message) = await api.ChangeMasterPasswordAsync(oldPassword, newPassword);

        // The API's own sentence is shown verbatim, not replaced with a generic "done": it names
        // how many peers are still on the old password, which is the fact the operator has to act
        // on and the reason this endpoint returns prose at all.
        return ok
            ? Back(AdminCards.MasterPassword, msg: message ?? "Master password changed on this node.")
            : Back(AdminCards.MasterPassword, err: "Failed to change the master password. Check the current password and that the vault is unlocked.");
    }

    /// <summary>Switches "Devices on my network": the node opens or closes its HTTPS listener, no restart, the vault stays unlocked.</summary>
    public async Task<IActionResult> OnPostSetDevicesOnMyNetworkAsync(bool enabled, CancellationToken ct)
    {
        var client = new NodeLanClient(httpClientFactory.CreateClient());
        var answer = await client.SetDevicesOnMyNetworkAsync(enabled, ct);
        if (answer.Error != null)
            return Back(AdminCards.Devices, err: answer.Error);
        return Back(AdminCards.Devices, msg: enabled
            ? "Devices on my network is on: other devices on this network can now reach this node."
            : "Devices on my network is off: this node answers this computer only.");
    }

    /// <summary>The Windows firewall step of the card: add the rule, or remove it. Each asks Windows for administrator permission.</summary>
    public async Task<IActionResult> OnPostDevicesFirewallAsync(string action, CancellationToken ct)
    {
        var client = new NodeLanClient(httpClientFactory.CreateClient());
        var add = string.Equals(action, "add", StringComparison.Ordinal);
        var answer = add ? await client.AddFirewallRuleAsync(ct) : await client.RemoveFirewallRuleAsync(ct);
        if (answer.Error != null)
            return Back(AdminCards.Devices, err: answer.Error);
        var done = add ? answer.FirewallRule : !answer.FirewallRule;
        return done
            ? Back(AdminCards.Devices, msg: add ? "The Windows Firewall rule is in place." : "The Windows Firewall rule is removed.")
            : Back(AdminCards.Devices, err: "Windows did not change the firewall (the permission prompt was declined or failed).");
    }

    /// <summary>Invisible Mode on or off (superadmin, like the page). On, this node rejects incoming sync calls and stops its own; the sync icon shows a badge.</summary>
    public async Task<IActionResult> OnPostSetInvisibleModeAsync(bool enabled)
    {
        var (ok, error) = await api.SetInvisibleModeAsync(enabled);
        if (!ok) return Back(AdminCards.InvisibleMode, err: error ?? "Could not change Invisible Mode.");
        return Back(AdminCards.InvisibleMode, msg: enabled
            ? "Invisible Mode is on: this node calls nobody and rejects incoming sync calls. The sync icon shows a badge while it is on."
            : "Invisible Mode is off: this node syncs with its peers again.");
    }

    public async Task<IActionResult> OnPostDismissPasswordNoticeAsync()
    {
        var ok = await api.DismissMasterPasswordNoticeAsync();
        return ok
            ? Back(AdminCards.MasterPassword, msg: "Password notice dismissed. It will reappear if another node reports a change after this one.")
            : Back(AdminCards.MasterPassword, err: "Failed to dismiss the password notice.");
    }

    public async Task<IActionResult> OnPostSetPeerSuperadminAsync(Guid nodeId, bool isSuperadmin)
    {
        var (ok, error) = await api.SetPeerSuperadminAsync(nodeId, isSuperadmin);
        return ok
            ? Back(AdminCards.TrustedNodes, msg: isSuperadmin
                ? "Peer promoted to superadmin."
                : "Peer demoted. It can no longer revoke peers, hard-delete content or restore the network.")
            : Back(AdminCards.TrustedNodes, err: error ?? "Failed to change peer authority");
    }

    public async Task<IActionResult> OnPostConfirmRestoredPeerAsync(Guid nodeId)
    {
        return await api.ConfirmRestoredPeerAsync(nodeId)
            ? Back(AdminCards.TrustedNodes, msg: "Device confirmed: it syncs with this one again. Promote it separately if it should be a superadmin.")
            : Back(AdminCards.RestoredPeers, err: "Could not confirm the device.");
    }

    public async Task<IActionResult> OnPostRevokeNodeAsync(Guid nodeId)
    {
        var ok = await api.RevokeNodeAsync(nodeId);
        return ok
            ? Back(AdminCards.TrustedNodes, msg: "Node access revoked")
            : Back(AdminCards.TrustedNodes, err: "Failed to revoke node access");
    }

    public async Task<IActionResult> OnPostChangeNodeAddressAsync(Guid nodeId, string newApiAddress, string password)
    {
        var (ok, error) = await api.ChangeNodeAddressAsync(nodeId, newApiAddress, password);
        return ok
            ? Back(AdminCards.TrustedNodes, msg: "Node address updated")
            : Back(AdminCards.TrustedNodes, err: error ?? "Failed to update node address");
    }

    /// <summary>"Let blind copies call this node": the Api verifies the address and the certificate and publishes the row.</summary>
    public async Task<IActionResult> OnPostSetBlindCallableAsync(Guid nodeId, string trust, string? address, string password, string? expectedPin)
    {
        var (result, error) = await api.SetBlindCallableAsync(nodeId, trust, address, password, expectedPin);
        if (result is null) return Back(AdminCards.TrustedNodes, err: error ?? "Could not change how blind copies reach this node.");
        var done = trust == "off" ? "Blind copies can no longer be paired to call this node." : "Blind copies can now be paired to call this node.";
        return Back(AdminCards.TrustedNodes, msg: string.IsNullOrEmpty(result.Notice) ? done : done + " " + result.Notice);
    }

    public async Task<IActionResult> OnPostSetAutoAcceptRestoreAsync(Guid nodeId, bool autoAccept)
    {
        var (ok, error) = await api.SetAutoAcceptRestoreAsync(nodeId, autoAccept);
        if (!ok)
            return Back(AdminCards.TrustedNodes, err: "Failed to update auto-accept setting: " + (error ?? "unknown"));
        return Back(AdminCards.TrustedNodes, msg: autoAccept
            ? "Auto-accept restore enabled — restores from this peer will apply automatically."
            : "Auto-accept restore disabled — restores from this peer remain pending; there is no manual approval prompt.");
    }

    public async Task<IActionResult> OnPostSetAutoAcceptDekRotationAsync(Guid nodeId, bool autoAccept)
    {
        var (ok, error) = await api.SetAutoAcceptDekRotationAsync(nodeId, autoAccept);
        if (!ok)
            return Back(AdminCards.TrustedNodes, err: "Failed to update auto-accept DEK rotation setting: " + (error ?? "unknown"));
        return Back(AdminCards.TrustedNodes, msg: autoAccept
            ? "Auto-accept DEK rotation enabled — DEK rotations from this peer will apply automatically."
            : "Auto-accept DEK rotation disabled — DEK rotations from this peer will require manual approval.");
    }

    public async Task<IActionResult> OnPostCreateSnapshotAsync()
    {
        var snap = await api.CreateSnapshotAsync();
        return snap != null
            ? Back(AdminCards.Snapshots, msg: "Snapshot created")
            : Back(AdminCards.Snapshots, err: "Failed to create snapshot");
    }

    public async Task<IActionResult> OnPostDeleteSnapshotAsync(string fileName)
    {
        var ok = await api.DeleteSnapshotAsync(fileName);
        return ok
            ? Back(AdminCards.Snapshots, msg: "Snapshot deleted")
            : Back(AdminCards.Snapshots, err: "Failed to delete snapshot");
    }

    public async Task<IActionResult> OnPostUploadSnapshotAsync(IFormFile file)
    {
        var result = await api.UploadSnapshotAsync(file);
        if (result == null) return BadRequest("Upload failed");
        return new JsonResult(result);
    }

    public async Task<IActionResult> OnPostInitiateNetworkRestoreAsync(string fileName, string? masterPassword)
    {
        // Restoring every node is the most destructive thing this page offers: the password is asked for
        // again, as it is for a restore of this node alone.
        if (string.IsNullOrEmpty(masterPassword))
            return new JsonResult(new { error = "Enter the master password to confirm the restore." }) { StatusCode = 400 };

        var (ok, eventId, error) = await api.InitiateNetworkRestoreAsync(fileName, masterPassword);
        if (!ok) return new JsonResult(new { error }) { StatusCode = 400 };
        return new JsonResult(new { eventId });
    }

    public async Task<IActionResult> OnGetDownloadSnapshotAsync(string fileName)
    {
        var result = await api.DownloadSnapshotAsync(fileName);
        if (result == null || !result.IsSuccessStatusCode)
            return Back(AdminCards.Snapshots, err: "Failed to download snapshot");

        var stream = await result.Content.ReadAsStreamAsync();
        return new FileStreamResult(new DisposingStreamWrapper(stream, result), "application/gzip") { FileDownloadName = fileName };
    }

    /// <summary>
    /// Restores this node only: it becomes a new node (the network restore has its own handler). On success the
    /// user lands on the sign-in page, which says what happened — the vault is locked after a restore, and the
    /// node is a new one that the other devices have to be joined to again.
    /// </summary>
    public async Task<IActionResult> OnPostRestoreSnapshotAsync(
        string fileName, string? masterPassword, bool createBackupFirst = true, string? mode = null)
    {
        // Nothing but a restore of this node is served here, and a value nobody defined is refused rather than read as the default.
        if (!RestoreModes.IsStandalone(mode))
            return Back(AdminCards.Snapshots, err: "Unknown restore mode. Restoring the whole network is started from the Restore dialog with that option chosen.");
        if (string.IsNullOrEmpty(masterPassword))
            return Back(AdminCards.Snapshots, err: "Enter the master password to confirm the restore.");

        var (ok, error, _) = await api.RestoreSnapshotAsync(fileName, masterPassword, createBackupFirst, RestoreModes.Standalone);
        if (ok)
            return RedirectToPage("/Login", new { restored = RestoreModes.Standalone });
        return Back(AdminCards.Snapshots, err: error ?? "Restore failed");
    }

    public async Task<IActionResult> OnPostDeleteAgentAsync(int agentId)
    {
        var ok = await api.DeleteAgentAsync(agentId);
        return ok
            ? Back(AdminCards.Agents, msg: "Agent revoked")
            : Back(AdminCards.Agents, err: "Failed to revoke agent");
    }

    public async Task<IActionResult> OnPostRebuildGraphAsync()
    {
        var report = await api.RebuildConceptTagEdgesAsync();
        TempData["GraphRebuildReport"] = report?.ToString();
        return Back(AdminCards.Diagnostics);
    }

    public async Task<IActionResult> OnPostCompactAsync(long? explicitCp = null)
    {
        var (ok, err, result) = await api.CompactAsync(explicitCp, "manual");
        if (ok && result != null)
        {
            return Back(AdminCards.Compaction, msg: $"Compacted to CP={result.CpAfter}, deleted {result.EventsDeleted} events, snapshot: {result.SnapshotFileName}");
        }
        return Back(AdminCards.Compaction, err: $"Compaction failed: {err}");
    }

    public async Task<IActionResult> OnPostCheckMetadataReadinessAsync()
    {
        var report = await api.GetMetadataReadinessAsync(askPeers: true);
        if (report is null) return Back(AdminCards.Metadata, err: "Could not get the readiness report.");
        KeepMetadataReport(report.Value);
        return Back(AdminCards.Metadata);
    }

    /// <summary>
    /// The answer of "Ask every device now" is kept for <see cref="MetadataReadinessKept"/> (read, not consumed, by the page), so that a switch the node
    /// refuses - a typo in the password, a box left empty - leaves the card as it was, with its form. In a cookie of its own (<see cref="MetadataReadinessCookie"/>):
    /// bounded in size, and expiring by itself, not when a later visit happens to look at its age.
    /// </summary>
    private void KeepMetadataReport(JsonElement report) => MetadataReadinessCookie.Write(HttpContext, report, DateTime.UtcNow);

    public async Task<IActionResult> OnPostActivateMetadataAsync(string masterPassword, bool backupTaken)
    {
        if (!backupTaken)
            return Back(AdminCards.Metadata, err: "Take a fresh backup of this node first, keep it offline, and tick the box.");
        if (string.IsNullOrEmpty(masterPassword))
            return Back(AdminCards.Metadata, err: "Enter the master password to switch metadata encryption on.");
        var (ok, error, answer) = await api.ActivateMetadataAsync(masterPassword);
        if (ok)
        {
            MetadataReadinessCookie.Clear(HttpContext);
            return Back(AdminCards.Metadata, msg: "Metadata encryption is on. Every device switches as it syncs.");
        }
        if (answer is { ValueKind: JsonValueKind.Object } a && a.TryGetProperty("blockers", out var blockers) && blockers.ValueKind == JsonValueKind.Array)
        {
            // Already on (a second tab, a repeated click, the activation of another node applied meanwhile): not a refusal, and nothing is left to switch.
            // What was kept is dropped, so that the page reads the real state, which is "on".
            if (blockers.EnumerateArray().Any(b => b.TryGetProperty("code", out var code) && code.GetString() == "already_activated"))
            {
                MetadataReadinessCookie.Clear(HttpContext);
                return Back(AdminCards.Metadata, msg: "Metadata encryption is already on.");
            }
            // A refusal: the node says what stands in the way. The devices are read again (this node's own observations: nobody is asked a second time),
            // the node's blockers go on the card, and the FORM STAYS - the person fixes what is said and presses it again.
            var peers = await api.GetMetadataReadinessAsync(askPeers: false) is { ValueKind: JsonValueKind.Object } fresh
                && fresh.TryGetProperty("peers", out var p) && p.ValueKind == JsonValueKind.Array ? p.Clone() : default;
            var report = new System.Text.Json.Nodes.JsonObject
            {
                ["activated"] = false,
                ["ready"] = false,
                ["refused"] = true,
                ["blockers"] = System.Text.Json.Nodes.JsonNode.Parse(blockers.GetRawText()),
                ["peers"] = peers.ValueKind == JsonValueKind.Array ? System.Text.Json.Nodes.JsonNode.Parse(peers.GetRawText()) : new System.Text.Json.Nodes.JsonArray(),
            };
            KeepMetadataReport(JsonSerializer.Deserialize<JsonElement>(report.ToJsonString()));
        }
        return Back(AdminCards.Metadata, err: error ?? "The activation failed.");
    }

    public async Task<IActionResult> OnPostProposeDekRotationAsync(string masterPassword)
    {
        var (ok, commitId, err) = await api.ProposeDekRotationAsync(masterPassword);
        if (ok)
        {
            TempData["DekRotationCommitId"] = commitId;
            return Back(AdminCards.DekRotation, msg: "DEK rotation proposed. Confirm to begin.");
        }
        return Back(AdminCards.DekRotation, err: err ?? "Failed to propose DEK rotation");
    }

    public async Task<IActionResult> OnPostAcceptDekRotationAsync(string commitEventId, string masterPassword)
    {
        var (ok, err) = await api.AcceptDekRotationAsync(commitEventId, masterPassword);
        if (ok)
        {
            TempData.Remove("DekRotationCommitId");
            TempData["RecoveryKeyReminder"] = "true";
            return Back(AdminCards.DekRotation, msg: "DEK rotation accepted. Re-encryption is in progress.");
        }
        TempData["DekRotationCommitId"] = commitEventId;
        return Back(AdminCards.DekRotation, err: err ?? "Failed to accept DEK rotation");
    }

    public async Task<IActionResult> OnPostCancelDekRotationAsync(string eventId)
    {
        var (ok, err) = await api.CancelDekRotationAsync(eventId);
        TempData.Remove("DekRotationCommitId");
        return ok
            ? Back(AdminCards.DekRotation, msg: "DEK rotation cancelled.")
            : Back(AdminCards.DekRotation, err: err ?? "Failed to cancel DEK rotation");
    }

    public async Task<IActionResult> OnPostPeerAcceptDekRotationAsync(string eventId)
    {
        var (ok, err) = await api.PeerAcceptDekRotationAsync(eventId);
        return ok
            ? Back(AdminCards.DekRotation, msg: "Peer DEK rotation applied successfully.")
            : Back(AdminCards.DekRotation, err: err ?? "Failed to apply peer DEK rotation");
    }

    public async Task<IActionResult> OnPostPeerRejectDekRotationAsync(string eventId)
    {
        var (ok, err) = await api.PeerRejectDekRotationAsync(eventId);
        return ok
            ? Back(AdminCards.DekRotation, msg: "Peer DEK rotation rejected. This node will desync from the network.")
            : Back(AdminCards.DekRotation, err: err ?? "Failed to reject peer DEK rotation");
    }

    public async Task<IActionResult> OnPostEnableAutoUnlockAsync()
    {
        var (ok, err) = await api.EnableAutoUnlockAsync();
        return ok
            ? Back(AdminCards.OsAutoUnlock, msg: "OS auto-unlock enabled. Anyone with access to this Windows account can now open the vault without a password.")
            : Back(AdminCards.OsAutoUnlock, err: err ?? "Failed to enable OS auto-unlock.");
    }

    public async Task<IActionResult> OnPostDisableAutoUnlockAsync()
    {
        var (ok, err) = await api.DisableAutoUnlockAsync();
        return ok
            ? Back(AdminCards.OsAutoUnlock, msg: "OS auto-unlock disabled. The vault will no longer unlock automatically on startup.")
            : Back(AdminCards.OsAutoUnlock, err: err ?? "Failed to disable OS auto-unlock.");
    }

    /// <summary>
    /// Wipes this node back to the pre-Setup state. Lives here — behind the page's
    /// <c>[Authorize(Roles = "superadmin")]</c> — and never on an anonymous page: with the master
    /// password as the only credential, an unauthenticated visitor could grind it and, on a
    /// correct guess, destroy the node. The password is still required as
    /// re-authentication (the API verifies it without unlocking the vault); a locked-out admin with
    /// no way to sign in uses `bmb init reset` on the host instead — see docs/deployment.md.
    /// </summary>
    public async Task<IActionResult> OnPostResetNodeAsync(string masterPassword)
    {
        if (string.IsNullOrWhiteSpace(masterPassword))
            return Back(AdminCards.ResetNode, err: "Master password is required to reset the node.");

        var (ok, err) = await api.ResetNodeAsync(masterPassword);
        return ok
            ? Redirect("/Setup?msg=Node+reset+complete")
            : Back(AdminCards.ResetNode, err: err ?? "Reset failed");
    }

    private async Task LoadDataAsync()
    {
        var tasks = new Task[]
        {
            api.GetIdentityAsync().ContinueWith(t => Identity = t.Result),
            api.GetWhitelistAsync().ContinueWith(t => Whitelist = t.Result),
            api.GetMasterPasswordNoticeAsync().ContinueWith(t => MasterPasswordNotice = t.Result),
            api.GetNodeSyncStatusAsync().ContinueWith(t => NodeSyncStatus = t.Result),
            api.GetInvisibleModeAsync().ContinueWith(t => IsInvisible = t.Result),
            api.GetSnapshotsAsync().ContinueWith(t => Snapshots = t.Result),
            api.GetAgentsAsync(all: true).ContinueWith(t => Agents = t.Result),
            api.GetUsersAsync().ContinueWith(t => Users = t.Result),
            api.GetConceptTagEdgeStatsAsync().ContinueWith(t => GraphEdgeStats = t.Result),
            api.GetCompactionPreviewAsync().ContinueWith(t => CompactionPreview = t.Result),
            api.GetSnapshotCheckpointsAsync().ContinueWith(t => SnapshotCheckpoints = t.Result),
            api.GetDekRotationProgressAsync().ContinueWith(t => DekRotationProgress = t.Result),
            api.GetPeerPendingDekRotationsAsync().ContinueWith(t => PeerPendingRotations = t.Result ?? new()),
            api.GetRestoredPeersAsync().ContinueWith(t => RestoredPeers = t.Result),
            api.GetServerVersionAsync().ContinueWith(t => CurrentVersion = t.Result),
            api.GetUpdateStatusAsync().ContinueWith(t => UpdateStatus = t.Result),
            api.GetSearchMetricsAsync().ContinueWith(t => SearchMetrics = t.Result),
            api.GetLockImpactAsync().ContinueWith(t => LockImpact = t.Result),
            LoadMetadataCardAsync(),
            LoadDevicesCardAsync(),
            api.GetAutoUnlockStatusAsync().ContinueWith(t =>
            {
                OsAutoUnlockEnabled = t.Result.Enabled;
                OsAutoUnlockSupported = t.Result.Supported;
            }),
            api.GetVaultStatusAsync().ContinueWith(t => VaultStatus = t.Result),
        };
        await Task.WhenAll(tasks);
    }

    private async Task LoadMetadataCardAsync()
    {
        HeldEvents = await api.GetHeldEventsAsync();
        // Read, not consumed: the answer of "Ask every device now" stays for a few minutes (see KeepMetadataReport); the cookie expires by itself.
        if (MetadataReadinessCookie.Read(HttpContext, DateTime.UtcNow) is { } kept)
        {
            MetadataReadiness = kept.Report;
            MetadataReadinessCheckedAll = true;
            MetadataReadinessCheckedAt = kept.At;
            return;
        }
        MetadataReadinessCookie.Clear(HttpContext);
        MetadataReadiness = await api.GetMetadataReadinessAsync(askPeers: false);
    }

    private async Task LoadDevicesCardAsync()
    {
        var status = await new NodeLanClient(httpClientFactory.CreateClient()).GetAsync();
        DevicesCard = DevicesOnMyNetworkCard.From(status, LanAddresses.BrowserUrls(status.Port, LanAddresses.GetLanIPv4Addresses()));
    }

    /// <summary>"we -> it" or "it -> us": whose call a contact was.</summary>
    public static string ContactDirectionWords(string? direction) => direction switch
    {
        "outbound" => "we \u2192 it",
        "inbound" => "it \u2192 us",
        _ => "",
    };

    public static string RelativeTime(DateTime? utc)
    {
        // Treat null / default / pre-2000 sentinels as "never synced" instead of computing a
        // nonsensical age from DateTime.MinValue (e.g. "739772d ago").
        if (utc == null || utc.Value == default || utc.Value.Year < 2000) return "Never";
        var diff = DateTime.UtcNow - utc.Value;
        if (diff.TotalMinutes < 1) return "just now";
        if (diff.TotalHours < 1) return $"{(int)diff.TotalMinutes} min ago";
        if (diff.TotalDays < 1) return $"{(int)diff.TotalHours}h ago";
        return $"{(int)diff.TotalDays}d ago";
    }
}
