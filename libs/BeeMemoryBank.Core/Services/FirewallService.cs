using System;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace BeeMemoryBank.Core.Services;

/// <summary>
/// Manages an inbound Windows Firewall rule to allow LAN devices to reach an opt-in HTTPS
/// listener. Windows-only, mirroring <c>AutostartService</c> / <c>LocalCaService</c>'s guard
/// convention: every method returns a safe default (false) on non-Windows and never throws.
/// </summary>
/// <remarks>
/// <para><b>Implementation choice — <c>netsh</c> vs COM interop.</b> The firewall can be driven
/// either by shelling out to <c>netsh advfirewall firewall</c> or via the COM
/// <c>INetFwPolicy2</c> interface. <c>netsh</c> is chosen here as the more robust option:</para>
/// <list type="bullet">
/// <item><description>COM interop requires either embedding the legacy <c>NetFwTypeLib</c> type
/// library (fragile across Windows versions, needs an explicit interop assembly or
/// <c>dynamic</c>/<c>Type.GetTypeFromProgID</c> reflection) or hand-authorizing a large block of
/// COM interface declarations. <c>netsh</c> is the documented, stable Windows CLI for firewall
/// management and absorbs all of that COM complexity internally.</description></item>
/// <item><description><c>netsh</c> is present on every supported Windows version and produces a
/// human-readable, scriptable result that is easy to diagnose.</description></item>
/// </list>
/// <para><b>Elevation.</b> Adding an <i>inbound</i> firewall rule genuinely requires
/// administrator privileges — there is no CurrentUser-scope escape hatch for inbound rules the
/// way there is for the CurrentUser certificate/registry stores used elsewhere. Calling
/// <see cref="EnsureInboundTcpRule"/> from an unelevated process therefore fails (and returns
/// false); the caller is expected to log the failure and keep the HTTPS listener running
/// without the rule (the documented degraded outcome). The listener itself never depends on
/// this rule to start.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public class FirewallService
{
    /// <summary>The default display name of the rule created for the HTTPS listener.</summary>
    public const string DefaultRuleName = "BeeMemoryBank Node HTTPS (5311)";

    private readonly string _ruleName;

    public FirewallService(string ruleName = DefaultRuleName)
    {
        _ruleName = ruleName;
    }

    /// <summary>
    /// Ensures an inbound allow-rule for TCP <paramref name="port"/> exists, recreating it
    /// idempotently (delete-then-add) so its definition always matches the current call. Returns
    /// true only if the rule was added successfully; returns false (never throws) on non-Windows,
    /// bad input, missing elevation, or any other failure.
    /// </summary>
    public bool EnsureInboundTcpRule(int port, string? appName = null)
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (port <= 0 || port > 65535) return false;

        try
        {
            // Delete first so re-runs always re-add with current parameters; ignore the exit
            // code here (a missing rule is not an error condition).
            RunNetsh($"advfirewall firewall delete rule name=\"{_ruleName}\" protocol=TCP localport={port}");

            var label = string.IsNullOrWhiteSpace(appName) ? "BeeMemoryBank" : appName;
            var exitCode = RunNetsh(
                $"advfirewall firewall add rule name=\"{_ruleName}\" dir=in action=allow " +
                $"protocol=TCP localport={port} description=\"Inbound HTTPS listener for {label}\"");

            return exitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Removes the inbound rule if present. Idempotent; never throws.
    /// </summary>
    public bool RemoveRule(int port)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            RunNetsh($"advfirewall firewall delete rule name=\"{_ruleName}\" protocol=TCP localport={port}");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int RunNetsh(string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "netsh",
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi);
        if (proc == null)
        {
            return -1;
        }

        if (!proc.WaitForExit(15000))
        {
            try { proc.Kill(); } catch { /* best-effort */ }
            return -1;
        }

        return proc.ExitCode;
    }
}
