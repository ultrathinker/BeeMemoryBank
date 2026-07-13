using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;

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

    private readonly string _settingsFilePath;
    private bool _isEnabled;

    public PreventSleepService()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(dataDir);
        _settingsFilePath = Path.Combine(dataDir, "desktop-settings.json");
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
                var result = SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_AWAYMODE_REQUIRED);
                Console.WriteLine($"[PreventSleepService] Enabled sleep prevention. Result: 0x{result:X}");
            }
            else
            {
                // Clears prior sleep prevention requirements
                var result = SetThreadExecutionState(ES_CONTINUOUS);
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
        try
        {
            if (File.Exists(_settingsFilePath))
            {
                var json = File.ReadAllText(_settingsFilePath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("preventSleep", out var prop))
                {
                    _isEnabled = prop.GetBoolean();
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PreventSleepService] Error loading settings: {ex.Message}");
            _isEnabled = false;
        }
    }

    private void SaveSettings()
    {
        try
        {
            var options = new JsonWriterOptions { Indented = true };
            using var stream = new FileStream(_settingsFilePath, FileMode.Create, FileAccess.Write);
            using var writer = new Utf8JsonWriter(stream, options);
            writer.WriteStartObject();
            writer.WriteBoolean("preventSleep", _isEnabled);
            writer.WriteEndObject();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PreventSleepService] Error saving settings: {ex.Message}");
        }
    }
}
