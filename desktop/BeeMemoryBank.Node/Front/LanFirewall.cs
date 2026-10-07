using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace BeeMemoryBank.Node;

/// <summary>The inbound firewall rule the LAN listeners need to be reachable from another device.</summary>
public interface ILanFirewall
{
    /// <summary>
    /// True where the app can offer to change this computer's firewall for the user: the Windows desktop app. False on a Mac
    /// (the system asks the user itself when the first connection arrives; the app changes nothing), under the Windows
    /// service (no desktop to ask on; the installer's firewall option opens the ports) and everywhere else.
    /// </summary>
    bool CanManage { get; }

    /// <summary>True if the rule is already in place (reading needs no elevation).</summary>
    bool RuleExists(int port);

    /// <summary>
    /// Adds the rule through a UAC prompt. Only ever called because the user pressed the button that
    /// says it will ask for administrator rights; false if they declined or it failed.
    /// </summary>
    Task<bool> AddWithConsentAsync(int port);

    /// <summary>
    /// Removes the rule the same way, for the same reason: the user pressed a button that says it asks for
    /// administrator rights. False if they declined or it failed.
    /// </summary>
    Task<bool> RemoveWithConsentAsync(int port);
}

/// <summary>
/// The firewall where the app does not touch it (a Mac, the Windows service, Linux): nothing to add, nothing to remove. The
/// pages read <see cref="ILanFirewall.CanManage"/> and say what applies on that system instead of offering a button.
/// </summary>
public sealed class NoLanFirewall : ILanFirewall
{
    public bool CanManage => false;
    public bool RuleExists(int port) => false;
    public Task<bool> AddWithConsentAsync(int port) => Task.FromResult(false);
    public Task<bool> RemoveWithConsentAsync(int port) => Task.FromResult(false);
}

/// <summary>
/// <see cref="ILanFirewall"/> over <c>netsh advfirewall</c>. The node runs unelevated, and an inbound
/// rule has no per-user variant, so the rule is added (and removed) by an elevated <c>netsh</c> the user approves
/// in the UAC dialog — once: afterwards <see cref="RuleExists"/> finds it and nobody is asked again.
/// The rule admits the local subnet only; the listener is for devices in the same room.
/// </summary>
public sealed class NetshLanFirewall : ILanFirewall
{
    public const string RuleName = "BeeMemoryBank device connect";

    private const int ErrorCancelled = 1223; // ERROR_CANCELLED: the user said no in the UAC dialog

    public bool CanManage => OperatingSystem.IsWindows();

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

    public Task<bool> AddWithConsentAsync(int port) => RunElevatedAsync(AddRuleArguments(port), () => RuleExists(port));

    public Task<bool> RemoveWithConsentAsync(int port) => RunElevatedAsync(RemoveRuleArguments(), () => !RuleExists(port));

    private static async Task<bool> RunElevatedAsync(string arguments, Func<bool> succeeded)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var psi = new ProcessStartInfo(NetshPath, arguments)
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
            return proc.ExitCode == 0 && succeeded();
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
        "description=\"Lets another device on this network reach BeeMemoryBank: Connect a device, and Devices on my network when it is switched on.\"";

    /// <summary>The <c>netsh</c> command line that removes it again — public for the test that pins it.</summary>
    public static string RemoveRuleArguments() => $"advfirewall firewall delete rule name=\"{RuleName}\"";

    private static string ShowRuleArguments() => $"advfirewall firewall show rule name=\"{RuleName}\"";

    // Fully qualified so an elevated run cannot pick up a netsh.exe planted earlier on PATH.
    private static string NetshPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "netsh.exe");
}
