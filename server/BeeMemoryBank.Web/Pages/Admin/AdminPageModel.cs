using BeeMemoryBank.Web.Models;
using BeeMemoryBank.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BeeMemoryBank.Web.Pages.Admin;

/// <summary>
/// What every page of the Admin area is: a superadmin's page, listed in <see cref="AdminPageCatalog"/>, drawn by the template <c>_AdminPage</c>, that loads only
/// what it shows. A handler ends with <see cref="Back"/>: the page opens again with the message of the action in the message area of the template, right
/// under the title, where the button was (BMB-303). The old single page had a card per block and named the card of a message; now the page is the card.
/// </summary>
[Authorize(Roles = "superadmin")]
public abstract class AdminPageModel(ApiClient api) : PageModel
{
    protected ApiClient Api { get; } = api;

    /// <summary>What the last action did, in a sentence (the <c>msg</c> of the address the handler sent the browser back to).</summary>
    [BindProperty(Name = "msg", SupportsGet = true)]
    public string? SuccessMessage { get; set; }

    /// <summary>Why the last action did not do it (the <c>err</c> of the address).</summary>
    [BindProperty(Name = "err", SupportsGet = true)]
    public string? ErrorMessage { get; set; }

    /// <summary>What an error also says, one line each (the devices to blame): the message area lists them under <see cref="ErrorMessage"/>. Empty for most pages.</summary>
    public IReadOnlyList<string> ErrorItems { get; protected set; } = [];

    /// <summary>The technical line of an error, small and last in the message area; null when there is none.</summary>
    public string? ErrorDetail { get; protected set; }

    /// <summary>The list entry of this page: its title and icon, which the template shows. A page that is not in the catalog cannot be drawn.</summary>
    public AdminPageInfo Info =>
        AdminPageCatalog.Find(RouteData.Values["page"] as string)
        ?? throw new InvalidOperationException($"{GetType().Name} ({RouteData.Values["page"]}) is not in AdminPageCatalog: add it, the index and the template read it.");

    /// <summary>
    /// The scripts of the page besides <c>admin.js</c> (their addresses under <c>/js/pages/</c>). The template loads them, in the Scripts section the single-page
    /// navigation executes after a swap: a page of the area has no script tag of its own.
    /// </summary>
    public virtual IReadOnlyList<string> PageScripts => [];

    /// <summary>Ends a handler: this page again, with the message of the action. A handler with nothing to say passes nothing.</summary>
    protected IActionResult Back(string? msg = null, string? err = null) => RedirectToPage(null, null, new { msg, err });

    // ---- what two pages show alike ---------------------------------------------------------------------------------------------------------------------

    /// <summary>The notice that another node changed the master password (set by the <c>master_password_changed</c> event); shown on the index and on the Master Password page.</summary>
    public MasterPasswordNoticeDto? MasterPasswordNotice { get; protected set; }

    /// <summary>Data-key rotations other nodes started that this one has not applied yet; shown on the index and on the DEK Rotation page.</summary>
    public List<PeerPendingDekRotationDto> PeerPendingRotations { get; protected set; } = [];

    protected async Task LoadMasterPasswordNoticeAsync() => MasterPasswordNotice = await Api.GetMasterPasswordNoticeAsync();

    protected async Task LoadPeerPendingRotationsAsync() => PeerPendingRotations = await Api.GetPeerPendingDekRotationsAsync() ?? [];

    protected async Task<IActionResult> DismissPasswordNoticeAsync()
    {
        var ok = await Api.DismissMasterPasswordNoticeAsync();
        return ok
            ? Back(msg: "Password notice dismissed. It will reappear if another node reports a change after this one.")
            : Back(err: "Failed to dismiss the password notice.");
    }

    protected async Task<IActionResult> PeerAcceptDekRotationAsync(string eventId)
    {
        var (ok, err) = await Api.PeerAcceptDekRotationAsync(eventId);
        return ok
            ? Back(msg: "Peer DEK rotation applied successfully.")
            : Back(err: err ?? "Failed to apply peer DEK rotation");
    }

    protected async Task<IActionResult> PeerRejectDekRotationAsync(string eventId)
    {
        var (ok, err) = await Api.PeerRejectDekRotationAsync(eventId);
        return ok
            ? Back(msg: "Peer DEK rotation rejected. This node will desync from the network.")
            : Back(err: err ?? "Failed to reject peer DEK rotation");
    }
}

/// <summary>Words the pages of the Admin area share.</summary>
public static class AdminText
{
    /// <summary>"we -> it" or "it -> us": whose call a contact was.</summary>
    public static string ContactDirectionWords(string? direction) => direction switch
    {
        "outbound" => "we → it",
        "inbound" => "it → us",
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
