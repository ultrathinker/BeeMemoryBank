using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace BeeMemoryBank.Node;

/// <summary>The inbound firewall rule the LAN join listener needs to be reachable from a phone.</summary>
public interface ILanFirewall
{
    /// <summary>True if the rule is already in place (reading needs no elevation).</summary>
    bool RuleExists(int port);

    /// <summary>
    /// Adds the rule through a UAC prompt. Only ever called because the user pressed the button that
    /// says it will ask for administrator rights; false if they declined or it failed.
    /// </summary>
    Task<bool> AddWithConsentAsync(int port);
}

/// <summary>
/// <see cref="ILanFirewall"/> over <c>netsh advfirewall</c>. The node runs unelevated, and an inbound
/// rule has no per-user variant, so the rule is added by an elevated <c>netsh</c> the user approves
/// in the UAC dialog — once: afterwards <see cref="RuleExists"/> finds it and nobody is asked again.
/// The rule admits the local subnet only; the listener is for devices in the same room.
/// </summary>
public sealed class NetshLanFirewall : ILanFirewall
{
    public const string RuleName = "BeeMemoryBank device connect";

    private const int ErrorCancelled = 1223; // ERROR_CANCELLED: the user said no in the UAC dialog

    public bool RuleExists(int port)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var psi = new ProcessStartInfo(NetshPath, ShowRuleArguments())
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi);
        if (proc == null) return false;
        var output = proc.StandardOutput.ReadToEnd();
        if (!proc.WaitForExit(15000)) { try { proc.Kill(); } catch { } return false; }
        // netsh exits 1 with "No rules match" when absent; the port check guards a same-named rule
        // someone edited by hand to point elsewhere.
        return proc.ExitCode == 0 && output.Contains(port.ToString(), StringComparison.Ordinal);
    }

    public async Task<bool> AddWithConsentAsync(int port)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var psi = new ProcessStartInfo(NetshPath, AddRuleArguments(port))
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };
        try
        {
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            await proc.WaitForExitAsync();
            return proc.ExitCode == 0 && RuleExists(port);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return false;
        }
    }

    /// <summary>The <c>netsh</c> command line that adds the rule — public for the test that pins it.</summary>
    public static string AddRuleArguments(int port) =>
        $"advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow protocol=TCP " +
        $"localport={port} remoteip=localsubnet " +
        "description=\"Lets a phone on this network join BeeMemoryBank while Connect a device is open.\"";

    private static string ShowRuleArguments() => $"advfirewall firewall show rule name=\"{RuleName}\"";

    // Fully qualified so an elevated run cannot pick up a netsh.exe planted earlier on PATH.
    private static string NetshPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "netsh.exe");
}
