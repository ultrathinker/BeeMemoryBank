using BeeMemoryBank.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace BeeMemoryBank.Web.Pages.Admin;

/// <summary>
/// "Devices on my network": what the desktop node says about its network listener, turned into what the page offers. On a node that is not a desktop
/// node (Docker) it says so, and the page explains what decides instead.
/// </summary>
public class DevicesOnMyNetworkModel(ApiClient api, IHttpClientFactory httpClientFactory) : AdminPageModel(api)
{
    public DevicesOnMyNetworkCard DevicesCard { get; private set; } =
        DevicesOnMyNetworkCard.From(NodeLanClient.LanStatus.Unavailable, []);

    public async Task OnGetAsync()
    {
        var status = await new NodeLanClient(httpClientFactory.CreateClient()).GetAsync();
        DevicesCard = DevicesOnMyNetworkCard.From(status, LanAddresses.BrowserUrls(status.Port, LanAddresses.GetLanIPv4Addresses()));
    }

    /// <summary>Switches "Devices on my network": the node opens or closes its HTTPS listener, no restart, the vault stays unlocked.</summary>
    public async Task<IActionResult> OnPostSetDevicesOnMyNetworkAsync(bool enabled, CancellationToken ct)
    {
        var client = new NodeLanClient(httpClientFactory.CreateClient());
        var answer = await client.SetDevicesOnMyNetworkAsync(enabled, ct);
        if (answer.Error != null)
            return Back(err: answer.Error);
        return Back(msg: enabled
            ? "Devices on my network is on: other devices on this network can now reach this node."
            : "Devices on my network is off: this node answers this computer only.");
    }

    /// <summary>The Windows firewall step of the page: add the rule, or remove it. Each asks Windows for administrator permission.</summary>
    public async Task<IActionResult> OnPostDevicesFirewallAsync(string action, CancellationToken ct)
    {
        var client = new NodeLanClient(httpClientFactory.CreateClient());
        var add = string.Equals(action, "add", StringComparison.Ordinal);
        var answer = add ? await client.AddFirewallRuleAsync(ct) : await client.RemoveFirewallRuleAsync(ct);
        if (answer.Error != null)
            return Back(err: answer.Error);
        var done = add ? answer.FirewallRule : !answer.FirewallRule;
        return done
            ? Back(msg: add ? "The Windows Firewall rule is in place." : "The Windows Firewall rule is removed.")
            : Back(err: "Windows did not change the firewall (the permission prompt was declined or failed).");
    }
}
