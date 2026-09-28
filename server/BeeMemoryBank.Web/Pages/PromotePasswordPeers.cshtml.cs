using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Web.Models;
using BeeMemoryBank.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BeeMemoryBank.Web.Pages;

/// <summary>
/// BMB-42 for devices that joined before the owner's decision "whoever knows the master password is
/// a superadmin": they are still recorded as plain peers, so their hard deletes and password notices
/// are refused by the mesh. This page lists them and, on an explicit confirm, promotes each through
/// the ordinary <c>PUT /api/whitelist/{nodeId}/superadmin</c> — one <c>whitelist_update</c> per
/// device, so the rest of the mesh agrees. There is no silent migration: a row recorded content-only
/// by the old default looks exactly like one an admin demoted on purpose, so a person decides.
/// </summary>
[Authorize(Roles = UserRoles.Superadmin)]
public class PromotePasswordPeersModel(ApiClient api) : PageModel
{
    public IReadOnlyList<WhitelistEntryDto> Candidates { get; private set; } = Array.Empty<WhitelistEntryDto>();

    public async Task OnGetAsync() => Candidates = CandidatesOf(await api.GetWhitelistAsync());

    public async Task<IActionResult> OnPostAsync(List<Guid> nodeIds)
    {
        // Promote only what is STILL a candidate: a device revoked, or promoted elsewhere, since the
        // list was shown is skipped rather than acted on from a stale form.
        var current = CandidatesOf(await api.GetWhitelistAsync()).Select(c => c.NodeId).ToHashSet();
        var failed = new List<string>();
        var promoted = 0;
        foreach (var nodeId in nodeIds.Where(current.Contains))
        {
            var (ok, error) = await api.SetPeerSuperadminAsync(nodeId, isSuperadmin: true);
            if (ok) promoted++;
            else failed.Add($"{nodeId.ToString()[..8]}: {error}");
        }

        return failed.Count == 0
            ? RedirectToPage("/Admin", new { msg = $"{promoted} device(s) granted superadmin." })
            : RedirectToPage("/Admin", new { err = $"{promoted} granted; failed: {string.Join("; ", failed)}" });
    }

    /// <summary>
    /// Active peers that are not superadmins and not blind nodes (a blind node never is one). Shared
    /// with the Admin page's banner so both always agree on the list.
    /// </summary>
    public static IReadOnlyList<WhitelistEntryDto> CandidatesOf(IEnumerable<WhitelistEntryDto>? whitelist) =>
        (whitelist ?? [])
            .Where(e => e.Status == "A" && !e.IsSuperadmin && !BlindNodeId.IsBlind(e.NodeId))
            .ToList();
}
