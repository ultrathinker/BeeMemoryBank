using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace BeeMemoryBank.Desktop.Services;

/// <summary>
/// Service to prevent Windows from entering sleep mode while enabled.
/// </summary>
[SupportedOSPlatform("windows")]
public class PreventSleepService
{
    private const uint ES_CONTINUOUS = 0x80000000;
    private const uint ES_SYSTEM_REQUIRED = 0x00000001;
    private const uint ES_AWAYMODE_REQUIRED = 0x00000040;

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern uint SetThreadExecutionState(uint esFlags);

    private const string SettingKey = "preventSleep";
    private readonly DesktopSettingsStore _settings;
    private bool _isEnabled;

    public PreventSleepService(DesktopSettingsStore? settings = null)
    {
        _settings = settings ?? new DesktopSettingsStore();
        LoadSettings();
    }

    /// <summary>
    /// Gets or sets a value indicating whether sleep prevention is enabled.
    /// </summary>
    public bool IsEnabled
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return false;
            return _isEnabled;
        }
        set
        {
            if (!OperatingSystem.IsWindows()) return;
            if (_isEnabled == value) return;
            _isEnabled = value;
            SaveSettings();
            ApplyState();
        }
    }

    /// <summary>
    /// Applies the current sleep prevention state using SetThreadExecutionState.
    /// </summary>
    public void ApplyState()
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            if (_isEnabled)
            {
                // ES_SYSTEM_REQUIRED: Keeps the system awake
                // ES_AWAYMODE_REQUIRED: Allows display to sleep while keeping the system running (laptop server mode)
                // A return value of 0 means the call FAILED (not merely "no prior state") — check
                // it, since some systems reject ES_AWAYMODE_REQUIRED; fall back to system-required
                // alone rather than silently leaving the machine free to sleep while the tray
                // toggle claims sleep prevention is on.
                var result = SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_AWAYMODE_REQUIRED);
                if (result == 0)
                {
                    Console.Error.WriteLine("[PreventSleepService] SetThreadExecutionState with away mode failed; retrying without it.");
                    result = SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED);
                    if (result == 0)
                        Console.Error.WriteLine("[PreventSleepService] SetThreadExecutionState failed entirely — sleep prevention is NOT actually active.");
                }
                Console.WriteLine($"[PreventSleepService] Enabled sleep prevention. Result: 0x{result:X}");
            }
            else
            {
                // Clears prior sleep prevention requirements
                var result = SetThreadExecutionState(ES_CONTINUOUS);
                if (result == 0)
                    Console.Error.WriteLine("[PreventSleepService] SetThreadExecutionState (clear) failed.");
                Console.WriteLine($"[PreventSleepService] Disabled sleep prevention. Result: 0x{result:X}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PreventSleepService] Error applying execution state: {ex.Message}");
        }
    }

    /// <summary>
    /// Clears the thread execution state on application exit without modifying settings.
    /// </summary>
    public void DisableSleepPreventionOnly()
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            var result = SetThreadExecutionState(ES_CONTINUOUS);
            Console.WriteLine($"[PreventSleepService] Reset ThreadExecutionState on exit. Result: 0x{result:X}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PreventSleepService] Error resetting execution state on exit: {ex.Message}");
        }
    }

    private void LoadSettings()
    {
        _isEnabled = _settings.GetBool(SettingKey, defaultValue: false);
    }

    private void SaveSettings()
    {
        try
        {
            _settings.SetBool(SettingKey, _isEnabled);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PreventSleepService] Error saving settings: {ex.Message}");
        }
    }
}
