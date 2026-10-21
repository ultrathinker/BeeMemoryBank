using System.Text.Json;
using BeeMemoryBank.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace BeeMemoryBank.Web.Pages.Admin;

/// <summary>
/// The "Metadata encryption" page (docs/design/step-a-spec.md 5.3): this node's readiness report. On a page load only what this node itself observed (it calls
/// nobody); after "Ask every device now" the full report.
/// </summary>
public class MetadataEncryptionModel(ApiClient api) : AdminPageModel(api)
{
    public JsonElement? MetadataReadiness { get; private set; }
    public bool MetadataReadinessCheckedAll { get; private set; }

    /// <summary>When the report of "Ask every device now" was made; the page says how old the answer it shows is.</summary>
    public DateTime? MetadataReadinessCheckedAt { get; private set; }

    /// <summary>The held part of the sync status (spec 6.9): changes received and not applied yet.</summary>
    public JsonElement? HeldEvents { get; private set; }

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
            return MetadataReadinessCheckedAt is { } at ? $"{result} Asked {AdminText.RelativeTime(at)}." : result;
        }
    }

    public async Task OnGetAsync()
    {
        HeldEvents = await Api.GetHeldEventsAsync();
        // Read, not consumed: the answer of "Ask every device now" stays for a few minutes (see KeepMetadataReport); the cookie expires by itself.
        if (MetadataReadinessCookie.Read(HttpContext, DateTime.UtcNow) is { } kept)
        {
            MetadataReadiness = kept.Report;
            MetadataReadinessCheckedAll = true;
            MetadataReadinessCheckedAt = kept.At;
            return;
        }
        MetadataReadinessCookie.Clear(HttpContext);
        MetadataReadiness = await Api.GetMetadataReadinessAsync(askPeers: false);
    }

    public async Task<IActionResult> OnPostCheckMetadataReadinessAsync()
    {
        var report = await Api.GetMetadataReadinessAsync(askPeers: true);
        if (report is null) return Back(err: "Could not get the readiness report.");
        KeepMetadataReport(report.Value);
        return Back();
    }

    /// <summary>
    /// The answer of "Ask every device now" is kept for <see cref="MetadataReadinessCookie.Lifetime"/> (read, not consumed, by the page), so that a switch the node
    /// refuses - a typo in the password, a box left empty - leaves the page as it was, with its form. In a cookie of its own (<see cref="MetadataReadinessCookie"/>):
    /// bounded in size, and expiring by itself, not when a later visit happens to look at its age.
    /// </summary>
    private void KeepMetadataReport(JsonElement report) => MetadataReadinessCookie.Write(HttpContext, report, DateTime.UtcNow);

    public async Task<IActionResult> OnPostActivateMetadataAsync(string masterPassword, bool backupTaken)
    {
        if (!backupTaken)
            return Back(err: "Take a fresh backup of this node first, keep it offline, and tick the box.");
        if (string.IsNullOrEmpty(masterPassword))
            return Back(err: "Enter the master password to switch metadata encryption on.");
        var (ok, error, answer) = await Api.ActivateMetadataAsync(masterPassword);
        if (ok)
        {
            MetadataReadinessCookie.Clear(HttpContext);
            return Back(msg: "Metadata encryption is on. Every device switches as it syncs.");
        }
        if (answer is { ValueKind: JsonValueKind.Object } a && a.TryGetProperty("blockers", out var blockers) && blockers.ValueKind == JsonValueKind.Array)
        {
            // Already on (a second tab, a repeated click, the activation of another node applied meanwhile): not a refusal, and nothing is left to switch.
            // What was kept is dropped, so that the page reads the real state, which is "on".
            if (blockers.EnumerateArray().Any(b => b.TryGetProperty("code", out var code) && code.GetString() == "already_activated"))
            {
                MetadataReadinessCookie.Clear(HttpContext);
                return Back(msg: "Metadata encryption is already on.");
            }
            // A refusal: the node says what stands in the way. The devices are read again (this node's own observations: nobody is asked a second time),
            // the node's blockers go on the page, and the FORM STAYS - the person fixes what is said and presses it again.
            var peers = await Api.GetMetadataReadinessAsync(askPeers: false) is { ValueKind: JsonValueKind.Object } fresh
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
        return Back(err: error ?? "The activation failed.");
    }
}
