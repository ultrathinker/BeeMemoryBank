using BeeMemoryBank.Web.Models;
using BeeMemoryBank.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BeeMemoryBank.Web.Pages;

/// <summary>
/// "Blind nodes" (plan 9): add one by its pair code, see status, protocol and last contact with the
/// alarms of plan 5.6, reseed, disconnect. Superadmin only, like Admin: every action here changes
/// who the network trusts. Also (release 2.7): invite a friend's device, which redeems the invitation, and confirm
/// that redemption here against the match code the friend reads out.
/// </summary>
[Authorize(Roles = "superadmin")]
public class BlindNodesModel(ApiClient api) : PageModel
{
    public List<BlindNodeDto>? Nodes { get; set; }

    /// <summary>The full nodes blind copies can be told to call (a hub, ADR 0007), with the trust mode of each.</summary>
    public List<BlindPhoneListenerDto>? Hubs { get; set; }
    public string? SuccessMessage { get; set; }
    public BlindNodeError? Error { get; set; }
    public string? WarningMessage { get; set; }

    /// <summary>The pair code the operator pasted, given back in the box when the add failed.</summary>
    public string? Code { get; set; }

    /// <summary>The invitations of this node (newest first), or null when they could not be loaded.</summary>
    public List<BlindInviteDto>? Invites { get; set; }

    /// <summary>The redemptions that wait for the operator's decision.</summary>
    public IEnumerable<BlindInviteDto> WaitingInvites => (Invites ?? []).Where(i => i.State == "redeemed");

    /// <summary>Confirmations under way: the owner said yes and the node is adding the device (or finishing it after an interruption).</summary>
    public IEnumerable<BlindInviteDto> FinishingInvites => (Invites ?? []).Where(i => i.State == "confirming");

    /// <summary>Invitations nobody has redeemed yet.</summary>
    public IEnumerable<BlindInviteDto> OpenInvites => (Invites ?? []).Where(i => i.State == "issued");

    /// <summary>What was decided lately (shown briefly: the history of the table, not an inbox).</summary>
    public IEnumerable<BlindInviteDto> DecidedInvites => (Invites ?? []).Where(i => i.State is "confirmed" or "refused" or "expired").Take(5);

    public BlindInviteDefaultsDto? InviteDefaults { get; set; }

    /// <summary>The invitation just made: shown on this response only, never put in a redirect, a cookie or the address.</summary>
    public BlindInviteIssuedDto? Issued { get; set; }

    /// <summary>The QR of <see cref="Issued"/> (a PNG data URI).</summary>
    public string? IssuedQr { get; set; }

    /// <summary>What the invitation form shows: the operator's last entries after a failure, else the node's declared address and name.</summary>
    public string? InviteAddress { get; set; }
    public string? InviteLabel { get; set; }
    public string? InviteSourceName { get; set; }
    public int InviteLifetime { get; set; } = 30;
    public string? InviteError { get; set; }

    public async Task OnGetAsync(string? msg, string? err, string? warn, string? detail)
    {
        SuccessMessage = msg;
        WarningMessage = warn;
        if (!string.IsNullOrEmpty(err)) Error = BlindNodeError.Of(err, detail);
        Nodes = await api.ListBlindNodesAsync();
        Hubs = await LoadHubsAsync();
        await LoadInvitesAsync();
    }

    private async Task LoadInvitesAsync()
    {
        Invites = await api.ListBlindInvitesAsync();
        InviteDefaults = await api.GetBlindInviteDefaultsAsync();
        // The address a friend's device will call: what the operator declared for this node, else the address this page was opened at.
        InviteAddress ??= InviteDefaults?.Address ?? $"{Request.Scheme}://{Request.Host}";
        InviteSourceName ??= InviteDefaults?.SourceName;
        if (InviteDefaults is { } d && InviteLifetime == 30) InviteLifetime = d.DefaultLifetime;
    }

    private async Task<List<BlindPhoneListenerDto>?> LoadHubsAsync() =>
        (await api.ListBlindPhoneListenersAsync())?.Where(l => !l.IsBlind).ToList();

    public async Task<IActionResult> OnPostAddAsync(string code)
    {
        // A failed add shows the page again, right here, with the code still in the box: the
        // operator fixes what the message says and presses Add, instead of pasting the code afresh.
        if (string.IsNullOrWhiteSpace(code))
            return await ShowAsync(BlindNodeErrors.EmptyCode(), code);
        var (ok, error, warnings) = await api.AddBlindNodeAsync(code.Trim());
        if (!ok) return await ShowAsync(error, code.Trim());
        return warnings.Count > 0
            ? RedirectToPage(new { msg = "Blind node added and seeded.", warn = string.Join(" ", warnings) })
            : RedirectToPage(new { msg = "Blind node added and seeded." });
    }

    /// <summary>
    /// Makes an invitation. The answer is this page again (not a redirect): the string is a one-time capability, so it is shown once, in the
    /// response to the operator's own request, and kept nowhere on the web side. A refusal shows the form again with what the operator typed.
    /// </summary>
    public async Task<IActionResult> OnPostInviteAsync(string? label, string? sourceName, int lifetime, string? address)
    {
        InviteLabel = label;
        InviteSourceName = sourceName;
        InviteAddress = address;
        InviteLifetime = lifetime;
        var (invite, error) = await api.IssueBlindInviteAsync(label, sourceName, lifetime, address);
        InviteError = error;
        if (invite is not null)
        {
            Issued = invite;
            IssuedQr = ConnectModel.GenerateQrPngDataUri(invite.Text);
            // The page holds a bearer secret: no cache, no history entry that can be shown again.
            Response.Headers.CacheControl = "no-store";
            Response.Headers.Pragma = "no-cache";
            InviteLabel = null;
        }
        Nodes = await api.ListBlindNodesAsync();
        Hubs = await LoadHubsAsync();
        await LoadInvitesAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostConfirmInviteAsync(string inviteId, string? password)
    {
        var (ok, error) = await api.ConfirmBlindInviteAsync(inviteId, password);
        return ok
            ? RedirectToPage(new { msg = "The blind copy is confirmed. It is now one of your blind nodes and starts its first copy." })
            : RedirectToPage(new { err = error ?? "The invitation could not be confirmed." });
    }

    public async Task<IActionResult> OnPostRefuseInviteAsync(string inviteId)
    {
        var (ok, error) = await api.RefuseBlindInviteAsync(inviteId);
        return ok
            ? RedirectToPage(new { msg = "Refused. Nothing of your memory bank was sent to that device, the backup key it sent is cleared, and its invitation is used up." })
            : RedirectToPage(new { err = error ?? "The redemption could not be refused." });
    }

    public async Task<IActionResult> OnPostWithdrawInviteAsync(string inviteId)
    {
        var (ok, error) = await api.WithdrawBlindInviteAsync(inviteId);
        return ok
            ? RedirectToPage(new { msg = "The invitation is withdrawn." })
            : RedirectToPage(new { err = error ?? "The invitation could not be withdrawn." });
    }

    public async Task<IActionResult> OnPostReseedAsync(Guid nodeId)
    {
        var (ok, error) = await api.ReseedBlindNodeAsync(nodeId);
        return ok
            ? RedirectToPage(new { msg = "Blind node reseeded." })
            : RedirectToPage(new { err = error!.Message, detail = error.Detail });
    }

    public async Task<IActionResult> OnPostDisconnectAsync(Guid nodeId)
    {
        bool ok;
        try
        {
            ok = await api.RevokeNodeAsync(nodeId);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            var down = BlindNodeErrors.AppDidNotAnswer(ex);
            return RedirectToPage(new { err = down.Message, detail = down.Detail });
        }
        if (ok) return RedirectToPage(new { msg = "Blind node disconnected. Wipe it on its own console." });
        var failed = BlindNodeErrors.ForDisconnect();
        return RedirectToPage(new { err = failed.Message });
    }

    private async Task<IActionResult> ShowAsync(BlindNodeError? error, string? code)
    {
        Error = error;
        Code = code;
        Nodes = await api.ListBlindNodesAsync();
        Hubs = await LoadHubsAsync();
        await LoadInvitesAsync();
        return Page();
    }
}
