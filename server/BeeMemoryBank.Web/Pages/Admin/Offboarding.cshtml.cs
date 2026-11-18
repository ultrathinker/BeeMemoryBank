using BeeMemoryBank.Web.Models;
using BeeMemoryBank.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace BeeMemoryBank.Web.Pages.Admin;

/// <summary>
/// "People being removed" (release 2.12.0, plan WS-E): the list of removals, and one removal with its steps, what each step waits for (with the two
/// actions: wait, or revoke that node), the review of S3, and "continue" with the administrator's password for the rotation. Superadmins only (the
/// base class carries <c>[Authorize(Roles = "superadmin")]</c>, so the strict stamp check of every request applies).
/// </summary>
public class OffboardingModel(ApiClient api) : AdminPageModel(api)
{
    public List<OffboardingDto> Processes { get; private set; } = [];
    public OffboardingDto? Process { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid? id, string? msg = null, string? err = null)
    {
        SuccessMessage = msg;
        ErrorMessage = err;
        if (id is { } one)
        {
            Process = await Api.GetOffboardingAsync(one);
            if (Process is null) ErrorMessage ??= "There is no such removal.";
        }
        Processes = await Api.GetOffboardingsAsync() ?? [];
        return Page();
    }

    public async Task<IActionResult> OnPostContinueAsync(Guid id, string? password)
    {
        var (ok, error) = await Api.ContinueOffboardingAsync(id, string.IsNullOrEmpty(password) ? null : password);
        return BackTo(id, ok ? "Moved on as far as it can go now." : null, error);
    }

    public async Task<IActionResult> OnPostDecideAsync(Guid id, string itemId, string decision, string? reason)
    {
        var (ok, error) = await Api.ResolveOffboardingReviewAsync(id, [new OffboardingDecisionDto(itemId, decision, reason)], false, null);
        return BackTo(id, ok ? "Decision saved." : null, error);
    }

    public async Task<IActionResult> OnPostRemoveSuperadminAsync(Guid id, string itemId, string? password)
    {
        var (ok, error) = await Api.ResolveOffboardingReviewAsync(id, [new OffboardingDecisionDto(itemId, "remove", null, "delete")], false, password);
        return BackTo(id, ok ? "Their removal has started; it shares the remaining steps with this one." : null, error);
    }

    public async Task<IActionResult> OnPostAcceptDefaultsAsync(Guid id)
    {
        var (ok, error) = await Api.ResolveOffboardingReviewAsync(id, [], true, null);
        return BackTo(id, ok ? "Every open item but the superadmins was decided as pre-selected. Decide each superadmin yourself." : null, error);
    }

    public async Task<IActionResult> OnPostRevokeNodeAsync(Guid id, Guid nodeId)
    {
        var (ok, error) = await Api.RevokeOffboardingNodeAsync(id, nodeId);
        return BackTo(id, ok ? "The node was revoked; nothing waits for it any more." : null, error);
    }

    public async Task<IActionResult> OnPostTakeOverAsync(Guid id, string? password)
    {
        var (ok, error) = await Api.TakeOverOffboardingAsync(id, password ?? "");
        return BackTo(id, ok ? "This node drives the removal now." : null, error);
    }

    private IActionResult BackTo(Guid id, string? msg, string? err) => RedirectToPage(null, null, new { id, msg, err });
}
