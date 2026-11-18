using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Web.Models;
using BeeMemoryBank.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace BeeMemoryBank.Web.Pages.Admin;

/// <summary>
/// <c>/Admin/Devices</c> (docs/design/lan-peer-sync.md 15.1): this device, the devices a restore left waiting, and one row per peer with the sentence that says how
/// this node reaches it. It also holds the "Connect a device" dialog: on the desktop node (Windows app, Mac app, Windows service) the button opens the LAN join
/// door on demand (no restart, so the vault stays unlocked), shows one join code per LAN IPv4 address, and in the Windows app adds the firewall rule when the
/// person asks for it.
/// </summary>
/// <remarks>
/// Opening the page changes nothing: the door is opened by the button of the dialog (a POST), never by a GET. The LAN addresses come from
/// <see cref="LanAddresses"/>, which must agree with the leaf certificate's SAN list.
/// </remarks>
public class DevicesModel(ApiClient api, IHttpClientFactory httpClientFactory, Microsoft.Extensions.Configuration.IConfiguration configuration) : AdminPageModel(api)
{
    /// <summary>Opens the dialog of the join code when the address names it (<c>#connect-device</c>), and copies a key.</summary>
    public override IReadOnlyList<string> PageScripts => ["/js/pages/devices.js"];

    /// <summary>The client of the node's <c>/node/lan</c> endpoints; a test gives the page one that talks to a stand-in for the node.</summary>
    protected virtual NodeLanClient Client => NodeLanClient.For(httpClientFactory, configuration);

    /// <summary>The network as this node knows it; null when the node did not answer.</summary>
    public NetworkDto? Network { get; private set; }

    public NodeIdentityDto? Identity { get; private set; }

    public List<WhitelistEntryDto> Whitelist { get; private set; } = [];

    public List<UserDto> Users { get; private set; } = [];

    public SharedDoorsStatusDto? SharedDoors { get; private set; }

    /// <summary>Devices a restore kept inactive until a superadmin confirms them (no anchor vouched for them).</summary>
    public List<RestoredPeerDto> RestoredPeers { get; private set; } = [];

    /// <summary>One row per peer, alarms first.</summary>
    public IReadOnlyList<DeviceRow> Rows { get; private set; } = [];

    /// <summary>What the node says about its LAN listener (the sync surface, the join door).</summary>
    public NodeLanClient.LanStatus Lan { get; private set; } = NodeLanClient.LanStatus.Unavailable;

    /// <summary>The sync surface of the LAN listener; null where the node does not have one (Docker).</summary>
    public NodeLanClient.LanSyncStatus? LanSync => Lan.IsAvailable ? Lan.LanSync : null;

    /// <summary>One entry per reachable LAN IPv4 address of this machine, while the door is open.</summary>
    public IReadOnlyList<JoinCodeEndpoint> Endpoints { get; private set; } = [];

    /// <summary>True when at least one LAN IPv4 address could be enumerated.</summary>
    public bool HasLanAddress => Endpoints.Count > 0;

    /// <summary>The page answers an action of the join dialog: the dialog is drawn open.</summary>
    public bool ConnectOpen { get; private set; }

    /// <summary>The one-time notice "Direct sync with your devices on this network is on" (D9) is shown until it is dismissed.</summary>
    public bool ShowLanNotice => LanSync is { Setting: "on", NoticePending: true };

    /// <summary>The devices the card "Grant superadmin to devices joined with the password" offers (BMB-42); the card is shown only while there are any.</summary>
    public IReadOnlyList<WhitelistEntryDto> PasswordPeersToPromote => CandidatesOf(Whitelist);

    /// <summary>
    /// Active peers that are not superadmins and not blind nodes (a blind node never is one): devices that joined before "whoever knows the master password is a
    /// superadmin" and are still recorded as plain peers, so their hard deletes and password notices are refused by the mesh.
    /// </summary>
    public static IReadOnlyList<WhitelistEntryDto> CandidatesOf(IEnumerable<WhitelistEntryDto>? whitelist) =>
        (whitelist ?? [])
            .Where(e => e.Status == "A" && !e.IsSuperadmin && !BlindNodeId.IsBlind(e.NodeId))
            .ToList();

    /// <summary>The rows whose ownership a superadmin is asked to confirm. Never the accounts node: it is shared by definition.</summary>
    public IReadOnlyList<WhitelistEntryDto> OwnershipReviewEntries =>
        Whitelist.Where(e => e.Status == "A" && e.NeedsReview && e.NodeId != Identity?.NodeId && e.NodeId != SharedDoors?.AccountsHomeNodeId).ToList();

    public bool IsSingleSuperadminBank =>
        Users.Count(u => string.Equals(u.Role, UserRoles.Superadmin, StringComparison.OrdinalIgnoreCase)) <= 1;

    public bool ShowOwnershipReviewCard => OwnershipReviewEntries.Count > 0 && !IsSingleSuperadminBank;

    public WhitelistEntryDto? WhitelistEntryFor(Guid nodeId) =>
        Whitelist.FirstOrDefault(e => e.NodeId == nodeId);

    public static string OwnershipChoiceValue(WhitelistEntryDto entry)
    {
        if (entry.Ownership == OwnershipKinds.Shared) return OwnershipKinds.Shared;
        if (entry.Ownership == OwnershipKinds.Personal && entry.OwnerPersonId is { } pid) return $"personal:{pid}";
        if (entry.SuggestedOwnership == OwnershipKinds.Personal && entry.SuggestedOwnerPersonId is { } sugPid)
            return $"personal:{sugPid}";
        return OwnershipKinds.Shared;
    }

    public static (bool Ok, string Ownership, Guid? OwnerPersonId) ParseOwnershipChoice(string? choice)
    {
        if (string.Equals(choice, OwnershipKinds.Shared, StringComparison.OrdinalIgnoreCase))
            return (true, OwnershipKinds.Shared, null);
        if (choice is not null && choice.StartsWith("personal:", StringComparison.OrdinalIgnoreCase)
            && Guid.TryParse(choice["personal:".Length..], out var pid) && pid != Guid.Empty)
            return (true, OwnershipKinds.Personal, pid);
        return (false, "", null);
    }

    public async Task OnGetAsync(CancellationToken ct) => await LoadAsync(await Client.GetAsync(ct));

    // ---- the join dialog ---------------------------------------------------------------------------------------------------------------------------------

    /// <summary>Opens the door for 15 minutes (or until one device has joined) and shows the join codes. Only this button does it.</summary>
    public async Task OnPostEnableAsync(CancellationToken ct) => await ShowDialogAsync(await Client.EnableAsync(ct));

    public async Task OnPostDisableAsync(CancellationToken ct) => await ShowDialogAsync(await Client.DisableAsync(ct));

    public async Task OnPostFirewallAsync(CancellationToken ct) => await ShowDialogAsync(await Client.AddFirewallRuleAsync(ct));

    private async Task ShowDialogAsync(NodeLanClient.LanStatus status)
    {
        await LoadAsync(status);
        ConnectOpen = true;
    }

    private async Task LoadAsync(NodeLanClient.LanStatus status)
    {
        Lan = status;
        Endpoints = JoinCodes.BuildEndpoints(status, LanAddresses.GetLanIPv4Addresses());

        var network = Api.GetNetworkAsync();
        var identity = Quietly(Api.GetIdentityAsync());
        var whitelist = Quietly(Api.GetWhitelistAsync());
        var users = Quietly(Api.GetUsersAsync());
        var restored = Api.GetRestoredPeersAsync();
        var blind = Api.ListBlindNodesAsync();
        var sharedDoors = Quietly(Api.GetSharedDoorsStatusAsync());
        await Task.WhenAll(network, identity, whitelist, users, restored, blind, sharedDoors);

        Network = network.Result;
        Identity = identity.Result;
        Whitelist = whitelist.Result ?? [];
        Users = users.Result ?? [];
        RestoredPeers = restored.Result;
        SharedDoors = sharedDoors.Result;
        var alarms = (blind.Result ?? []).ToDictionary(b => b.NodeId, b => (IReadOnlyCollection<string>)b.Alarms);
        var now = DateTime.UtcNow;
        Rows = (Network?.Peers ?? [])
            .Select(p => DeviceRows.For(p, alarms.GetValueOrDefault(p.NodeId), now))
            .OrderByDescending(r => r.Tone)
            .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static async Task<T?> Quietly<T>(Task<T?> task) where T : class
    {
        try { return await task; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { return null; }
    }

    // ---- the rest ----------------------------------------------------------------------------------------------------------------------------------------

    public async Task<IActionResult> OnPostConfirmRestoredPeerAsync(Guid nodeId) =>
        await Api.ConfirmRestoredPeerAsync(nodeId)
            ? Back(msg: "Device confirmed: it syncs with this one again. Promote it separately if it should be a superadmin.")
            : Back(err: "Could not confirm the device.");

    /// <summary>The one-time notice about direct sync on this network (D9) was read.</summary>
    public async Task<IActionResult> OnPostDismissLanNoticeAsync(CancellationToken ct)
    {
        await Client.DismissLanNoticeAsync(ct);
        return Back();
    }

    /// <summary>
    /// BMB-42: promotes, on an explicit confirm, the devices ticked on the card - each through the ordinary <c>PUT /api/whitelist/{nodeId}/superadmin</c>, one
    /// <c>whitelist_update</c> per device, so the rest of the mesh agrees. There is no silent migration: a row recorded content-only by the old default looks
    /// exactly like one an admin demoted on purpose, so a person decides.
    /// </summary>
    public async Task<IActionResult> OnPostPromotePasswordPeersAsync(List<Guid> nodeIds)
    {
        // Promote only what is STILL a candidate: a device revoked, or promoted elsewhere, since the
        // list was shown is skipped rather than acted on from a stale form.
        var current = CandidatesOf(await Api.GetWhitelistAsync()).Select(c => c.NodeId).ToHashSet();
        var failed = new List<string>();
        var promoted = 0;
        foreach (var nodeId in nodeIds.Where(current.Contains))
        {
            var (ok, error) = await Api.SetPeerSuperadminAsync(nodeId, isSuperadmin: true);
            if (ok) promoted++;
            else failed.Add($"{nodeId.ToString()[..8]}: {error}");
        }

        return failed.Count == 0
            ? Back(msg: $"{promoted} device(s) granted superadmin.")
            : Back(err: $"{promoted} granted; failed: {string.Join("; ", failed)}");
    }

    public async Task<IActionResult> OnPostConfirmOwnershipAsync(Guid nodeId, string? choice)
    {
        var (okChoice, ownership, pid) = ParseOwnershipChoice(choice);
        if (!okChoice)
            return Back(err: "Select shared infrastructure or a person for this device.");
        var (ok, error) = await Api.SetPeerOwnershipAsync(nodeId, ownership, pid);
        return ok
            ? Back(msg: "Device ownership confirmed.")
            : Back(err: error ?? "Failed to update device ownership.");
    }

    public async Task<IActionResult> OnPostConfirmAllOwnershipAsync()
    {
        var (ok, updated, skipped, error) = await Api.ReviewOwnershipAsync(confirmAll: true);
        if (!ok)
            return Back(err: error ?? "Failed to confirm device ownership.");
        if (skipped.Count > 0)
        {
            var reasons = string.Join("; ", skipped.Select(s => s.Reason).Where(r => !string.IsNullOrWhiteSpace(r)).Distinct());
            return Back(msg: $"Confirmed ownership for {updated} device(s); skipped {skipped.Count}"
                + (string.IsNullOrEmpty(reasons) ? "." : $" ({reasons})."));
        }
        return Back(msg: $"Confirmed ownership for {updated} device(s).");
    }

    public async Task<IActionResult> OnPostAcknowledgeAccountsHomeChangeAsync()
    {
        var (ok, error) = await Api.AcknowledgeAccountsHomeChangeAsync();
        return ok
            ? Back(msg: "Acknowledged: the change of the accounts node is no longer flagged.")
            : Back(err: error ?? "The change could not be acknowledged.");
    }

    public async Task<IActionResult> OnPostSetAccountsHomeAsync(Guid nodeId)
    {
        var (ok, error) = await Api.SetAccountsHomeAsync(nodeId);
        return ok
            ? Back(msg: "Accounts node updated.")
            : Back(err: error ?? "Failed to update the accounts node.");
    }
}
