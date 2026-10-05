using System;
using System.Diagnostics;
using BeeMemoryBank.Desktop.MacOS.Interop;
using BeeMemoryBank.Desktop.Services;
using BeeMemoryBank.Platforms.Apple.Interop;

namespace BeeMemoryBank.Desktop.MacOS;

/// <summary>IOPMAssertionCreateWithName / IOPMAssertionRelease, as the service uses them. A seam: the real calls only on a Mac.</summary>
internal interface IPowerAssertions
{
    /// <summary>Takes a "no idle system sleep" assertion; false with the reason when the system refused.</summary>
    bool TryCreate(string name, out uint assertionId, out string? error);

    void Release(uint assertionId);
}

/// <summary>Starts <c>caffeinate -i -w &lt;pid&gt;</c>, the fallback when the assertion cannot be taken. A seam.</summary>
internal interface ICaffeinateLauncher
{
    /// <summary>The running child, or null with the reason. Disposing the handle stops the child.</summary>
    IDisposable? Start(int watchedProcessId, out string? error);
}

/// <summary>
/// Keeps the Mac from falling asleep on its own while the setting is on: a power assertion of the kind <c>caffeinate -i</c> takes
/// (<c>PreventUserIdleSystemSleep</c>: the display may still turn off, closing the lid or choosing Sleep still sleeps), created through
/// IOKit and released when the setting goes off, on exit, or when the process ends (the system drops a dead process's assertions). Only
/// if the assertion is refused does it start <c>/usr/bin/caffeinate -i -w &lt;this pid&gt;</c>, which ends by itself when the app does.
/// Same setting key and same semantics as the Windows service; when neither way works it says so on stderr and
/// <see cref="IsActive"/> stays false, so the claim "sleep prevention is on" is never silently untrue.
/// </summary>
public sealed class MacOsPreventSleep : IPreventSleepService, IDisposable
{
    private const string SettingKey = "preventSleep";
    private const string AssertionName = "Bee Memory Bank is keeping this Mac awake while the node is shared";

    private readonly DesktopSettingsStore _settings;
    private readonly IPowerAssertions _assertions;
    private readonly ICaffeinateLauncher _caffeinate;
    private readonly int _processId;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private bool _isEnabled;
    private uint _assertionId;
    private bool _hasAssertion;
    private IDisposable? _caffeinateChild;

    public MacOsPreventSleep(DesktopSettingsStore? settings = null)
        : this(settings ?? new DesktopSettingsStore(), new IoKitPowerAssertions(), new CaffeinateLauncher(), Environment.ProcessId, Console.Error.WriteLine) { }

    internal MacOsPreventSleep(DesktopSettingsStore settings, IPowerAssertions assertions, ICaffeinateLauncher caffeinate, int processId, Action<string> log)
    {
        _settings = settings;
        _assertions = assertions;
        _caffeinate = caffeinate;
        _processId = processId;
        _log = log;
        _isEnabled = _settings.GetBool(SettingKey, defaultValue: false);
    }

    public bool IsEnabled
    {
        get { lock (_gate) return _isEnabled; }
        set
        {
            lock (_gate)
            {
                if (_isEnabled == value) return;
                _isEnabled = value;
                try { _settings.SetBool(SettingKey, _isEnabled); }
                catch (Exception ex) { _log($"[MacOsPreventSleep] Error saving settings: {ex.Message}"); }
                ApplyState();
            }
        }
    }

    /// <summary>True while something really holds the Mac awake (the assertion, or the fallback child).</summary>
    public bool IsActive
    {
        get { lock (_gate) return _hasAssertion || _caffeinateChild != null; }
    }

    /// <summary>"assertion", "caffeinate" or null: what holds the Mac awake.</summary>
    internal string? Method
    {
        get { lock (_gate) return _hasAssertion ? "assertion" : _caffeinateChild != null ? "caffeinate" : null; }
    }

    public void ApplyState()
    {
        lock (_gate)
        {
            if (_isEnabled) Hold();
            else Release();
        }
    }

    public void DisableSleepPreventionOnly()
    {
        lock (_gate) Release();
    }

    public void Dispose() => DisableSleepPreventionOnly();

    private void Hold()
    {
        if (_hasAssertion || _caffeinateChild != null) return;   // already held: applying twice does not take a second one

        if (_assertions.TryCreate(AssertionName, out var id, out var assertionError))
        {
            _assertionId = id;
            _hasAssertion = true;
            _log("[MacOsPreventSleep] Enabled sleep prevention (power assertion).");
            return;
        }

        _log($"[MacOsPreventSleep] The power assertion was refused ({assertionError}); falling back to caffeinate.");
        var child = _caffeinate.Start(_processId, out var caffeinateError);
        if (child != null)
        {
            _caffeinateChild = child;
            _log("[MacOsPreventSleep] Enabled sleep prevention (caffeinate).");
            return;
        }

        _log($"[MacOsPreventSleep] Sleep prevention is NOT actually active: no power assertion ({assertionError}) and caffeinate did not start ({caffeinateError}).");
    }

    private void Release()
    {
        if (_hasAssertion)
        {
            _hasAssertion = false;
            try { _assertions.Release(_assertionId); }
            catch (Exception ex) { _log($"[MacOsPreventSleep] Error releasing the power assertion: {ex.Message}"); }
            _log("[MacOsPreventSleep] Disabled sleep prevention.");
        }
        if (_caffeinateChild != null)
        {
            var child = _caffeinateChild;
            _caffeinateChild = null;
            try { child.Dispose(); }
            catch (Exception ex) { _log($"[MacOsPreventSleep] Error stopping caffeinate: {ex.Message}"); }
            _log("[MacOsPreventSleep] Disabled sleep prevention.");
        }
    }
}

internal sealed class IoKitPowerAssertions : IPowerAssertions
{
    public bool TryCreate(string name, out uint assertionId, out string? error)
    {
        assertionId = 0;
        error = null;
        try
        {
            NativeLibraries.RequireMacOS();
            using var scope = new CfScope();
            var type = scope.NewString(IOKitPower.PreventUserIdleSystemSleep);
            var cfName = scope.NewString(name);
            var status = IOKitPower.IOPMAssertionCreateWithName(type, IOKitPower.AssertionLevelOn, cfName, out assertionId);
            if (status == IOKitPower.Success && assertionId != 0) return true;
            error = $"IOPMAssertionCreateWithName returned 0x{status:X}";
            assertionId = 0;
            return false;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException or InvalidOperationException)
        {
            error = ex.Message;
            assertionId = 0;
            return false;
        }
    }

    public void Release(uint assertionId)
    {
        NativeLibraries.RequireMacOS();
        var status = IOKitPower.IOPMAssertionRelease(assertionId);
        if (status != IOKitPower.Success) throw new InvalidOperationException($"IOPMAssertionRelease returned 0x{status:X}.");
    }
}

internal sealed class CaffeinateLauncher : ICaffeinateLauncher
{
    public IDisposable? Start(int watchedProcessId, out string? error)
    {
        error = null;
        if (!OperatingSystem.IsMacOS())
        {
            error = "not macOS";
            return null;
        }
        try
        {
            var info = new ProcessStartInfo(MacTools.Caffeinate)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            // -i: prevent idle sleep; -w: exit when the watched process (this app) exits - so nothing is left behind if the app dies.
            info.ArgumentList.Add("-i");
            info.ArgumentList.Add("-w");
            info.ArgumentList.Add(watchedProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var process = Process.Start(info);
            if (process is null)
            {
                error = "caffeinate did not start";
                return null;
            }
            return new Child(process);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            error = ex.Message;
            return null;
        }
    }

    private sealed class Child(Process process) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                if (!process.HasExited) process.Kill();   // the child this class started, and no other process
                process.WaitForExit(2000);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            finally { process.Dispose(); }
        }
    }
}
