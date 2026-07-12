using Microsoft.Win32;
using System;

namespace BeeMemoryBank.Desktop.Services;

/// <summary>
/// Service to manage application autostart on Windows user login.
/// </summary>
public class AutostartService
{
    private const string RegistryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "BeeMemoryBank";

    private readonly string _registryValueName;

    public AutostartService(string registryValueName = AppName)
    {
        _registryValueName = registryValueName;
    }

    /// <summary>
    /// Gets a value indicating whether the application is configured to run at startup.
    /// </summary>
    public bool IsEnabled
    {
        get
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath);
                if (key == null)
                {
                    return false;
                }

                var value = key.GetValue(_registryValueName) as string;
                if (string.IsNullOrEmpty(value))
                {
                    return false;
                }

                var exePath = GetExecutablePath();
                if (string.IsNullOrEmpty(exePath))
                {
                    return false;
                }

                // Check if the registry value contains the current executable path
                return value.Contains(exePath, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Enables autostart by writing the registry value.
    /// </summary>
    public void Enable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var exePath = GetExecutablePath();
        if (string.IsNullOrEmpty(exePath))
        {
            throw new InvalidOperationException("Could not resolve the current executable path.");
        }

        using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, writable: true);
        if (key == null)
        {
            throw new InvalidOperationException("Could not open the autostart registry key for writing.");
        }

        // Write the path wrapped in quotes, with the --minimized flag
        var value = $"\"{exePath}\" --minimized";
        key.SetValue(_registryValueName, value, RegistryValueKind.String);
    }

    /// <summary>
    /// Disables autostart by removing the registry value.
    /// </summary>
    public void Disable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, writable: true);
        if (key == null)
        {
            return;
        }

        key.DeleteValue(_registryValueName, throwOnMissingValue: false);
    }

    private static string? GetExecutablePath()
    {
        return Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
    }
}
