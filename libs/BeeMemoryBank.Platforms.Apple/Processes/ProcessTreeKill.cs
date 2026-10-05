using System.ComponentModel;
using System.Diagnostics;

namespace BeeMemoryBank.Platforms.Apple.Processes;

/// <summary>
/// Kills a started process tree and then, bounded, waits for the kill to land. On macOS and Linux the
/// SIGKILLs of <see cref="Process.Kill(bool)"/> are only sent, not waited for: the call returns while the
/// kernel may still be tearing the tree down, and a caller that moves on at once races a process that is
/// still dying (it can still hold a port, a file or a child for a moment). Windows terminates
/// near-synchronously, so there the wait costs nothing. Both macOS apps kill hanging tools this way, so
/// the rule lives here once instead of twice (the same rule the solution learned the hard way in CI).
/// </summary>
public static class ProcessTreeKill
{
    /// <summary>How long the kernel gets to finish after the kill. Generous on purpose: SIGKILL is immediate when it lands.</summary>
    public static readonly TimeSpan DefaultPostKillWait = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Kills the whole tree of <paramref name="process"/> and then waits, bounded, for the main process to
    /// actually exit. Never throws: a process that already exited, was never started, or cannot be signalled
    /// needs no wait - and none of that is a failure the caller could report anyway.
    /// </summary>
    public static void KillTreeAndWait(Process process, TimeSpan? postKillWait = null)
    {
        ArgumentNullException.ThrowIfNull(process);
        var milliseconds = (int)Math.Min((postKillWait ?? DefaultPostKillWait).TotalMilliseconds, int.MaxValue);
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return;   // already exited, or the OS refused the signal: there is nothing left to wait for
        }
        try
        {
            process.WaitForExit(milliseconds);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // gone between the kill and the wait: the goal was reached
        }
    }
}
