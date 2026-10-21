using BeeMemoryBank.Web.Models;
using BeeMemoryBank.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace BeeMemoryBank.Web.Pages.Admin;

public class CompactionModel(ApiClient api) : AdminPageModel(api)
{
    public CompactionPreviewDto? CompactionPreview { get; private set; }
    public List<SnapshotCheckpointDto>? SnapshotCheckpoints { get; private set; }

    public async Task OnGetAsync()
    {
        await Task.WhenAll(
            Api.GetCompactionPreviewAsync().ContinueWith(t => CompactionPreview = t.Result),
            Api.GetSnapshotCheckpointsAsync().ContinueWith(t => SnapshotCheckpoints = t.Result));
    }

    public async Task<IActionResult> OnPostCompactAsync(long? explicitCp = null)
    {
        var (ok, err, result) = await Api.CompactAsync(explicitCp, "manual");
        if (ok && result != null)
        {
            return Back(msg: $"Compacted to CP={result.CpAfter}, deleted {result.EventsDeleted} events, snapshot: {result.SnapshotFileName}");
        }
        return Back(err: $"Compaction failed: {err}");
    }
}
