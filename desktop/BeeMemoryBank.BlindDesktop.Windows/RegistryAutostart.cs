using System.Reflection;
using BeeMemoryBank.BlindMobile.Services.Blind;
using Microsoft.Win32;

namespace BeeMemoryBank.BlindDesktop.Windows;

/// <summary>
/// "Start with Windows": a string value in the current user's <c>Run</c> key (<c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>),
/// named <see cref="ValueName"/>, holding <c>"&lt;this exe&gt;" --minimized</c> so that the app starts in the tray. It is NOT the full app's
/// <c>BeeMemoryBank</c> value and never reads, writes or removes it. Per user, so it needs no administrator rights.
/// </summary>
public sealed class RegistryAutostart : IBlindAutostart
{
    public const string ValueName = "BeeMemoryBankBlind";
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string MinimizedArgument = "--minimized";

    private readonly string _valueName;
    private readonly string _runKeyPath;
    private readonly Func<string> _executablePath;

    /// <param name="valueName">The Run value; tests pass a test name, never the real one.</param>
    /// <param name="runKeyPath">The key under HKCU; tests may point it at a scratch key.</param>
    /// <param name="executablePath">The exe to start; by default this process's own apphost.</param>
    public RegistryAutostart(string valueName = ValueName, string runKeyPath = RunKeyPath, Func<string>? executablePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(valueName);
        ArgumentException.ThrowIfNullOrWhiteSpace(runKeyPath);
        _valueName = valueName;
        _runKeyPath = runKeyPath;
        _executablePath = executablePath ?? CurrentExecutable;
    }

    /// <summary>The value written for <paramref name="executable"/>: quoted path, then the minimized flag.</summary>
    public static string CommandFor(string executable) => $"\"{executable}\" {MinimizedArgument}";

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            using var key = Registry.CurrentUser.CreateSubKey(_runKeyPath, writable: true);
            key.SetValue(_valueName, CommandFor(_executablePath()), RegistryValueKind.String);
            return;
        }

        using var existing = Registry.CurrentUser.OpenSubKey(_runKeyPath, writable: true);
        if (existing?.GetValue(_valueName) is not null) existing.DeleteValue(_valueName, throwOnMissingValue: false);
    }

    /// <summary>True when the value points at this exe; false when it is absent or points elsewhere (a moved install); null when the registry cannot be read.</summary>
    public bool? IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(_runKeyPath, writable: false);
                return key?.GetValue(_valueName) is string command
                    && string.Equals(command, CommandFor(_executablePath()), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// This app's own executable. When the app was started as <c>dotnet BeeMemoryBank.BlindDesktop.dll</c> the process is dotnet.exe,
    /// which must never go into a Run key; the apphost next to the assembly is used instead.
    /// </summary>
    private static string CurrentExecutable()
    {
        var process = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(process)
            && !string.Equals(Path.GetFileNameWithoutExtension(process), "dotnet", StringComparison.OrdinalIgnoreCase))
            return process;

        var assembly = Assembly.GetEntryAssembly()?.Location;
        var apphost = string.IsNullOrEmpty(assembly) ? null : Path.ChangeExtension(assembly, ".exe");
        return apphost is not null && File.Exists(apphost)
            ? apphost
            : throw new InvalidOperationException("The app was not started from its own executable, so there is nothing to register to start with Windows.");
    }
}
