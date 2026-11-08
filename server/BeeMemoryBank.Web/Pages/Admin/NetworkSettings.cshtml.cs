using BeeMemoryBank.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace BeeMemoryBank.Web.Pages.Admin;

/// <summary>
/// <c>/Admin/NetworkSettings</c> (docs/design/lan-peer-sync.md 15.1): what this node does on the networks of this computer. "Sync with your devices on this network"
/// (the sync connection on the trusted networks, the Windows category, the firewall, the port), "Show this node's web page to devices on my network" (what was
/// "Devices on my network", with the CA certificate for browsers), "Use my VPN for sync", and Invisible Mode. On a node that is not a desktop node (Docker) it says
/// what decides instead.
/// </summary>
public class NetworkSettingsModel(ApiClient api, IHttpClientFactory httpClientFactory, Microsoft.Extensions.Configuration.IConfiguration configuration) : AdminPageModel(api)
{
    /// <summary>The client of the node's <c>/node/lan</c> endpoints; a test gives the page one that talks to a stand-in for the node.</summary>
    protected virtual NodeLanClient Client => NodeLanClient.For(httpClientFactory, configuration);

    /// <summary>What the node says about its LAN listener (unavailable outside the desktop node).</summary>
    public NodeLanClient.LanStatus Lan { get; private set; } = NodeLanClient.LanStatus.Unavailable;

    /// <summary>The card "Show this node's web page to devices on my network": what it offers on this system.</summary>
    public DevicesOnMyNetworkCard DevicesCard { get; private set; } = DevicesOnMyNetworkCard.From(NodeLanClient.LanStatus.Unavailable, []);

    /// <summary>The sync surface of the LAN listener ("Sync with your devices on this network"); null where the node does not have one.</summary>
    public NodeLanClient.LanSyncStatus? LanSync => Lan.IsAvailable ? Lan.LanSync : null;

    /// <summary>"windows", "macos" or "other".</summary>
    public string Platform => Lan.Platform;

    /// <summary>Invisible Mode: this node calls nobody and answers nobody. Null when the node did not say.</summary>
    public bool? IsInvisible { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        var status = Client.GetAsync(ct);
        var invisible = Api.GetInvisibleModeAsync();
        await Task.WhenAll(status, invisible);
        Lan = status.Result;
        DevicesCard = DevicesOnMyNetworkCard.From(Lan, LanAddresses.BrowserUrls(Lan.Port, LanAddresses.GetLanIPv4Addresses()));
        IsInvisible = invisible.Result;
    }

    /// <summary>Switches "Sync with your devices on this network": the sync surface on the trusted networks, no restart.</summary>
    public async Task<IActionResult> OnPostSetLanSyncAsync(bool enabled, CancellationToken ct)
    {
        var answer = await Client.SetLanSyncAsync(enabled, ct);
        if (answer.Error != null) return Back(err: answer.Error);
        return Back(msg: enabled
            ? "Direct sync with your devices is on: your paired devices on a trusted network reach this node directly. The web page stays private."
            : "Direct sync with your devices is off: they sync with this node through your server or blind node, or when they call it.");
    }

    /// <summary>Moves the LAN port.</summary>
    public async Task<IActionResult> OnPostSetLanPortAsync(int port, CancellationToken ct)
    {
        var answer = await Client.SetLanPortAsync(port, ct);
        if (answer.Error != null) return Back(err: answer.Error);
        return Back(msg: $"The LAN port is {port} now. Your devices that run this version follow by themselves; one of an older version keeps the old address until it is updated.");
    }

    /// <summary>"Sync with your paired devices on this network?": yes, not this network, or forget the answer. The same for a VPN ("Use my VPN for sync").</summary>
    public async Task<IActionResult> OnPostAnswerNetworkAsync(string fingerprint, string answer, CancellationToken ct)
    {
        bool? trusted = answer switch { "yes" => true, "no" => false, _ => null };
        var result = await Client.AnswerNetworkAsync(fingerprint, trusted, ct);
        if (result.Error != null) return Back(err: result.Error);
        return Back(msg: trusted switch
        {
            true => "This network is trusted: your paired devices sync with this node directly here.",
            false => "Not this network: this node does not answer your devices directly here.",
            _ => "The answer for this network is forgotten; it is asked again when this computer is on it.",
        });
    }

    /// <summary>Switches "Show this node's web page to devices on my network": the node opens or closes its HTTPS listener, no restart, the vault stays unlocked.</summary>
    public async Task<IActionResult> OnPostSetDevicesOnMyNetworkAsync(bool enabled, CancellationToken ct)
    {
        var answer = await Client.SetDevicesOnMyNetworkAsync(enabled, ct);
        if (answer.Error != null)
            return Back(err: answer.Error);
        return Back(msg: enabled
            ? "The web page of this node is shown on this network: other devices here can now reach it."
            : "The web page of this node is private again: this node answers this computer only.");
    }

    /// <summary>The Windows firewall step: add the rule, or remove it. Each asks Windows for administrator permission.</summary>
    public async Task<IActionResult> OnPostDevicesFirewallAsync(string action, CancellationToken ct)
    {
        var add = string.Equals(action, "add", StringComparison.Ordinal);
        var answer = add ? await Client.AddFirewallRuleAsync(ct) : await Client.RemoveFirewallRuleAsync(ct);
        if (answer.Error != null)
            return Back(err: answer.Error);
        var done = add ? answer.FirewallRule : !answer.FirewallRule;
        return done
            ? Back(msg: add ? "The Windows Firewall rule is in place." : "The Windows Firewall rule is removed.")
            : Back(err: "Windows did not change the firewall (the permission prompt was declined or failed).");
    }

    /// <summary>Invisible Mode on or off (superadmin, like the page). On, this node rejects incoming sync calls and stops its own; the sync icon shows a badge.</summary>
    public async Task<IActionResult> OnPostSetInvisibleModeAsync(bool enabled)
    {
        var (ok, error) = await Api.SetInvisibleModeAsync(enabled);
        if (!ok) return Back(err: error ?? "Could not change Invisible Mode.");
        return Back(msg: enabled
            ? "Invisible Mode is on: this node calls nobody and rejects incoming sync calls. The sync icon shows a badge while it is on."
            : "Invisible Mode is off: this node syncs with its peers again.");
    }
}
