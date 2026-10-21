using System.Text.Json;
using BeeMemoryBank.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace BeeMemoryBank.Web.Pages.Admin;

public class SoftwareUpdatesModel(ApiClient api) : AdminPageModel(api)
{
    public string? CurrentVersion { get; private set; }
    public JsonElement? UpdateStatus { get; private set; }

    /// <summary>The manifest the last check was made with: the download step posts it again so that the node can find the file and the hash it declares.</summary>
    public string? CheckedManifestJson { get; private set; }

    public async Task OnGetAsync()
    {
        CheckedManifestJson = TempData.Peek("CheckedManifestJson") as string;
        await Task.WhenAll(
            Api.GetServerVersionAsync().ContinueWith(t => CurrentVersion = t.Result),
            Api.GetUpdateStatusAsync().ContinueWith(t => UpdateStatus = t.Result));
    }

    public async Task<IActionResult> OnPostCheckUpdateAsync(string? manifestJson, string? manifestSignatureBase64)
    {
        manifestJson ??= "";
        manifestSignatureBase64 ??= "";
        await Api.CheckForUpdatesAsync(manifestJson, manifestSignatureBase64);
        TempData["CheckedManifestJson"] = manifestJson;
        return Back();
    }

    public async Task<IActionResult> OnPostDownloadUpdateAsync(string? manifestJson)
    {
        manifestJson ??= "";
        await Api.DownloadUpdateAsync(manifestJson);
        return Back();
    }

    public async Task<IActionResult> OnPostApplyUpdateAsync()
    {
        var ok = await Api.ApplyUpdateAsync();
        return ok
            ? Back(msg: "Update triggered. The server is rebuilding and will restart — refresh this page in 30–60 seconds.")
            : Back(err: "Failed to trigger the update.");
    }

    public async Task<IActionResult> OnPostResetUpdateAsync()
    {
        var ok = await Api.ResetUpdateAsync();
        TempData.Remove("CheckedManifestJson");
        return ok
            ? Back(msg: "Update state machine has been reset.")
            : Back(err: "Failed to reset update state machine.");
    }
}
