namespace BeeMemoryBank.Web.Services;

/// <summary>Which card the Admin &gt; Nodes page shows for "Devices on my network".</summary>
public enum DevicesCardKind
{
    /// <summary>Not a desktop node (Docker, a standalone Api+Web): there is nothing to switch, the card says what decides instead.</summary>
    NotADesktopNode,

    /// <summary>The desktop node: a switch.</summary>
    Switchable,

    /// <summary><c>BMB_HTTPS_ENABLED=1</c> is set for this run: the node is open and the switch cannot change that.</summary>
    ForcedByEnvironment,
}

/// <summary>What the card says about the firewall, by system.</summary>
public enum DevicesFirewallStep
{
    /// <summary>Nothing to say.</summary>
    None,

    /// <summary>Windows app, listener on, no rule yet: offer "Allow in Windows Firewall" (a UAC prompt).</summary>
    AddRule,

    /// <summary>Windows app, listener on, rule in place.</summary>
    RuleInPlace,

    /// <summary>Windows app, listener off but the rule is still there: offer to remove it (a UAC prompt).</summary>
    RemoveRule,

    /// <summary>Mac: the system asks when the first connection arrives; the app changes nothing.</summary>
    MacAsksOnFirstConnection,

    /// <summary>Windows service: no desktop to ask on; the installer's firewall option opens the ports.</summary>
    ServiceInstaller,
}

/// <summary>
/// Everything the "Devices on my network" card decides from the node's answer, in one place so the page only renders it and the
/// rules (what to offer where) are testable without a page.
/// </summary>
public sealed record DevicesOnMyNetworkCard(
    DevicesCardKind Kind,
    bool On,
    bool ListenerFailed,
    DevicesFirewallStep Firewall,
    IReadOnlyList<string> Urls,
    string? Error)
{
    public static DevicesOnMyNetworkCard From(NodeLanClient.LanStatus status, IReadOnlyList<string> browserUrls)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (!status.IsAvailable)
            return new(DevicesCardKind.NotADesktopNode, false, false, DevicesFirewallStep.None, [], status.Error);

        var forced = status.Setting == "environment";
        // The node reports "permanent" only while the listener is really up. "on" in the saved setting without that: the node
        // could not open the port (another program has it) or has no certificate.
        var listening = status.Mode == "permanent" && status.Active;
        var failed = status.Setting == "on" && !listening;
        var on = forced || (status.Setting == "on" && listening);

        return new(
            forced ? DevicesCardKind.ForcedByEnvironment : DevicesCardKind.Switchable,
            on,
            failed,
            FirewallStepFor(status, on, failed),
            on ? browserUrls : [],
            status.Error);
    }

    private static DevicesFirewallStep FirewallStepFor(NodeLanClient.LanStatus status, bool on, bool failed)
    {
        var wantsOpen = on || failed;
        if (status.FirewallManaged)
        {
            if (wantsOpen) return status.FirewallRule ? DevicesFirewallStep.RuleInPlace : DevicesFirewallStep.AddRule;
            return status.FirewallRule ? DevicesFirewallStep.RemoveRule : DevicesFirewallStep.None;
        }
        if (!wantsOpen) return DevicesFirewallStep.None;
        return status.Platform switch
        {
            "macos" => DevicesFirewallStep.MacAsksOnFirstConnection,
            "windows" => DevicesFirewallStep.ServiceInstaller,
            _ => DevicesFirewallStep.None,
        };
    }
}
