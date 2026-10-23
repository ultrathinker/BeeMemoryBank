using BeeMemoryBank.Web.Models;
using BeeMemoryBank.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace BeeMemoryBank.Web.Pages.Admin;

/// <summary>
/// "Blind nodes" (plan 9): add one by its pair code, see status, protocol and last contact with the alarms of plan 5.6, reseed, disconnect. Also (release 2.7):
/// invite a friend's device, which redeems the invitation, and confirm that redemption here against the match code the friend reads out; and add an Android
/// blind copy by the code its phone shows (the page of its own, <c>/AndroidBlindCopy</c>, is folded in). Superadmin only, like the whole Admin area: every
/// action here changes who the network trusts.
/// </summary>
public class BlindNodesModel(ApiClient api) : AdminPageModel(api)
{
    /// <summary>The Copy button and the countdown of the invitation just made.</summary>
    public override IReadOnlyList<string> PageScripts => ["/js/pages/blind-invites.js"];

    public List<BlindNodeDto>? Nodes { get; set; }

    /// <summary>Every node a blind copy can be told to call (a blind node or a hub, ADR 0007); null when the list could not be loaded.</summary>
    public List<BlindPhoneListenerDto>? Listeners { get; set; }

    /// <summary>The full nodes blind copies can be told to call (a hub, ADR 0007), with the trust mode of each.</summary>
    public List<BlindPhoneListenerDto>? Hubs => Listeners?.Where(l => !l.IsBlind).ToList();

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

    /// <summary>The phone just paired (the card "Add an Android blind copy"): shown on this response, with the code that tells the phone where to call.</summary>
    public BlindPhonePairedDto? Paired { get; set; }

    /// <summary>The QR of the call code of <see cref="Paired"/> (a PNG data URI).</summary>
    public string? CallCodeQr { get; set; }

    /// <summary>Why the last pairing of a phone was refused (the plain <see cref="AdminPageModel.ErrorMessage"/> is the message of an action that ended in a redirect).</summary>
    public string? PhoneError { get; set; }

    public async Task OnGetAsync(string? msg, string? err, string? warn, string? detail)
    {
        SuccessMessage = msg;
        WarningMessage = warn;
        if (!string.IsNullOrEmpty(err)) ShowError(BlindNodeError.Of(err, detail));
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        Nodes = await Api.ListBlindNodesAsync();
        Listeners = await Api.ListBlindPhoneListenersAsync();
        await LoadInvitesAsync();
    }

    private async Task LoadInvitesAsync()
    {
        Invites = await Api.ListBlindInvitesAsync();
        InviteDefaults = await Api.GetBlindInviteDefaultsAsync();
        // The address a friend's device will call: what the operator declared for this node, else the address this page was opened at.
        InviteAddress ??= InviteDefaults?.Address ?? $"{Request.Scheme}://{Request.Host}";
        InviteSourceName ??= InviteDefaults?.SourceName;
        if (InviteDefaults is { } d && InviteLifetime == 30) InviteLifetime = d.DefaultLifetime;
    }

    /// <summary>Puts what went wrong in the message area under the title: the sentence, the devices to blame as a list, the technical line last and small.</summary>
    private void ShowError(BlindNodeError? error)
    {
        if (error is null) return;
        (ErrorMessage, ErrorItems, ErrorDetail) = error;
    }

    /// <summary>Ends a handler: this page again; the error and the warning of the blind nodes carry their own words, so they travel as well.</summary>
    private IActionResult Again(string? msg = null, string? err = null, string? detail = null, string? warn = null)
    {
        var values = new Dictionary<string, object?>();
        if (msg != null) values["msg"] = msg;
        if (err != null) values["err"] = err;
        if (detail != null) values["detail"] = detail;
        if (warn != null) values["warn"] = warn;
        return RedirectToPage(null, null, values);
    }

    public async Task<IActionResult> OnPostAddAsync(string code)
    {
        // A failed add shows the page again, right here, with the code still in the box: the
        // operator fixes what the message says and presses Add, instead of pasting the code afresh.
        if (string.IsNullOrWhiteSpace(code))
            return await ShowAsync(BlindNodeErrors.EmptyCode(), code);
        var (ok, error, warnings) = await Api.AddBlindNodeAsync(code.Trim());
        if (!ok) return await ShowAsync(error, code.Trim());
        return warnings.Count > 0
            ? Again(msg: "Blind node added and seeded.", warn: string.Join(" ", warnings))
            : Again(msg: "Blind node added and seeded.");
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
        var (invite, error) = await Api.IssueBlindInviteAsync(label, sourceName, lifetime, address);
        InviteError = error;
        if (invite is not null)
        {
            Issued = invite;
            IssuedQr = ConnectDeviceModel.GenerateQrPngDataUri(invite.Text);
            // The page holds a bearer secret: no cache, no history entry that can be shown again.
            Response.Headers.CacheControl = "no-store";
            Response.Headers.Pragma = "no-cache";
            InviteLabel = null;
        }
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostConfirmInviteAsync(string inviteId, string? password)
    {
        var (ok, error) = await Api.ConfirmBlindInviteAsync(inviteId, password);
        return ok
            ? Again(msg: "The blind copy is confirmed. It is now one of your blind nodes and starts its first copy.")
            : Again(err: error ?? "The invitation could not be confirmed.");
    }

    public async Task<IActionResult> OnPostRefuseInviteAsync(string inviteId)
    {
        var (ok, error) = await Api.RefuseBlindInviteAsync(inviteId);
        return ok
            ? Again(msg: "Refused. Nothing of your memory bank was sent to that device, the backup key it sent is cleared, and its invitation is used up.")
            : Again(err: error ?? "The redemption could not be refused.");
    }

    public async Task<IActionResult> OnPostWithdrawInviteAsync(string inviteId)
    {
        var (ok, error) = await Api.WithdrawBlindInviteAsync(inviteId);
        return ok
            ? Again(msg: "The invitation is withdrawn.")
            : Again(err: error ?? "The invitation could not be withdrawn.");
    }

    public async Task<IActionResult> OnPostReseedAsync(Guid nodeId)
    {
        var (ok, error) = await Api.ReseedBlindNodeAsync(nodeId);
        return ok
            ? Again(msg: "Blind node reseeded.")
            : Again(err: error!.Message, detail: error.Detail);
    }

    public async Task<IActionResult> OnPostDisconnectAsync(Guid nodeId)
    {
        bool ok;
        try
        {
            ok = await Api.RevokeNodeAsync(nodeId);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            var down = BlindNodeErrors.AppDidNotAnswer(ex);
            return Again(err: down.Message, detail: down.Detail);
        }
        if (ok) return Again(msg: "Blind node disconnected. Wipe it on its own console.");
        var failed = BlindNodeErrors.ForDisconnect();
        return Again(err: failed.Message);
    }

    /// <summary>
    /// "Add an Android blind copy": the phone's code and the node it will call. The answer is this page again, not a redirect, with the code that tells the phone
    /// where to call and which certificate to trust (as text and as a QR), or the reason the pairing was refused.
    /// </summary>
    public async Task<IActionResult> OnPostPairPhoneAsync(string? code, Guid listenerId)
    {
        if (string.IsNullOrWhiteSpace(code))
            PhoneError = "Paste the code shown by the phone.";
        else
        {
            var (paired, error) = await Api.PairBlindPhoneAsync(code.Trim(), listenerId);
            Paired = paired;
            PhoneError = error;
            if (paired != null) CallCodeQr = ConnectDeviceModel.GenerateQrPngDataUri(paired.CallCode);
        }
        await LoadAsync();
        return Page();
    }

    private async Task<IActionResult> ShowAsync(BlindNodeError? error, string? code)
    {
        ShowError(error);
        Code = code;
        await LoadAsync();
        return Page();
    }
}
