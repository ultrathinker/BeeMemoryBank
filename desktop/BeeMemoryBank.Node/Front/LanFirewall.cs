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

    /// <summary>True if the rule an older version added (for every network profile) is still there: the card offers to replace it.</summary>
    bool LegacyRuleExists() => false;

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
/// The rule admits the local subnet only, and only on private and domain networks (docs/design/lan-peer-sync.md 6.2): even if the
/// listener's own idea of a trusted network were wrong, Windows keeps the LAN port closed on a Public network.
/// <para>The rule of an older version ("BeeMemoryBank device connect", every profile) is replaced in the same elevated step.</para>
/// </summary>
public sealed class NetshLanFirewall : ILanFirewall
{
    public const string RuleName = "BeeMemoryBank LAN";

    /// <summary>The name of the rule up to 2.7.5, which admitted the local subnet on every network profile.</summary>
    public const string LegacyRuleName = "BeeMemoryBank device connect";

    private const int ErrorCancelled = 1223; // ERROR_CANCELLED: the user said no in the UAC dialog

    public bool CanManage => OperatingSystem.IsWindows();

    public bool RuleExists(int port) => RuleExists(RuleName, port);

    public bool LegacyRuleExists() => RuleExists(LegacyRuleName, null);

    private static bool RuleExists(string name, int? port)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var psi = new ProcessStartInfo(NetshPath, ShowRuleArguments(name))
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
        return proc.ExitCode == 0 && (port is null || output.Contains(port.Value.ToString(), StringComparison.Ordinal));
    }

    // One elevated cmd.exe runs every netsh step, so the person sees ONE permission prompt: the rule of an older version and a rule for another port go,
    // and the new one is added.
    public Task<bool> AddWithConsentAsync(int port) => RunElevatedAsync(
        $"/d /c \"\"{NetshPath}\" {RemoveRuleArguments(LegacyRuleName)} & \"{NetshPath}\" {RemoveRuleArguments()} & \"{NetshPath}\" {AddRuleArguments(port)}\"",
        () => RuleExists(port));

    public Task<bool> RemoveWithConsentAsync(int port) => RunElevatedAsync(
        $"/d /c \"\"{NetshPath}\" {RemoveRuleArguments(LegacyRuleName)} & \"{NetshPath}\" {RemoveRuleArguments()}\"",
        () => !RuleExists(port) && !LegacyRuleExists());

    private static async Task<bool> RunElevatedAsync(string cmdArguments, Func<bool> succeeded)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var psi = new ProcessStartInfo(CmdPath, cmdArguments)
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
            return succeeded();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return false;
        }
    }

    /// <summary>The <c>netsh</c> command line that adds the rule — public for the test that pins it.</summary>
    public static string AddRuleArguments(int port) =>
        $"advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow protocol=TCP " +
        $"localport={port} remoteip=localsubnet profile=private,domain " +
        "description=\"Lets your paired devices on a private network sync with BeeMemoryBank directly, and Connect a device and Devices on my network reach it.\"";

    /// <summary>The <c>netsh</c> command line that removes it again — public for the test that pins it.</summary>
    public static string RemoveRuleArguments() => RemoveRuleArguments(RuleName);

    private static string RemoveRuleArguments(string name) => $"advfirewall firewall delete rule name=\"{name}\"";

    private static string ShowRuleArguments(string name) => $"advfirewall firewall show rule name=\"{name}\"";

    // Fully qualified so an elevated run cannot pick up a netsh.exe planted earlier on PATH.
    private static string NetshPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "netsh.exe");

    private static string CmdPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
}
