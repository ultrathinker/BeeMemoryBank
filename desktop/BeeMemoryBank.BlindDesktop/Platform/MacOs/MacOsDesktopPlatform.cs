using System.Security.Cryptography;
using System.Text;
using BeeMemoryBank.BlindDesktop.MacOS;
using BeeMemoryBank.BlindMobile.Services.Blind;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.BlindDesktop.Platform;

/// <summary>
/// The macOS platform: only compiled into a macOS build (see the project file). The seams are the macOS adapters (Keychain secrets, the
/// atomic state file, Application Support paths, power and network, a per-user LaunchAgent); "only one copy runs" is a file lock plus a
/// Unix domain socket in the app's private folder (.NET has no named mutex or event on Unix); the menu-bar icon is a monochrome template
/// image; the error log is in <c>~/Library/Logs</c>.
///
/// <para>With <c>--data-dir</c> (a scratch folder for a check or a test) everything that lives outside that folder gets a name of its
/// own, derived from the folder: the Keychain service and the LaunchAgent label. So a scratch run can never read, overwrite or remove
/// the secrets or the login item of the real app.</para>
/// </summary>
internal sealed class MacOsDesktopPlatform : IBlindDesktopPlatform
{
    private readonly MacOsBlindHostOptions _options;
    private readonly IBlindSecretStore? _secretStore;
    private readonly bool _customFolder;

    public MacOsDesktopPlatform(string? dataDirectory) : this(dataDirectory, null) { }

    /// <param name="secretStore">Replaces the Keychain store (tests of the host pass an in-memory one so that they never touch a keychain).</param>
    internal MacOsDesktopPlatform(string? dataDirectory, IBlindSecretStore? secretStore)
    {
        _secretStore = secretStore;
        _customFolder = !string.IsNullOrWhiteSpace(dataDirectory);
        var suffix = _customFolder ? "." + FolderKey(dataDirectory!) : "";
        _options = new MacOsBlindHostOptions
        {
            DataDirectory = _customFolder ? dataDirectory : null,
            KeychainService = MacOsKeychainSecretStore.DefaultService + suffix,
            AutostartLabel = MacOsBlindAutostartOptions.DefaultLabel + suffix,
        };
    }

    /// <summary>The real app's data folder (~/Library/Application Support/BeeMemoryBankBlind); nothing is created.</summary>
    internal static string DefaultDataDirectory() => Path.Combine(MacOsBlindPaths.DefaultApplicationSupport(), MacOsBlindPaths.FolderName);

    public string Name => "macOS";

    public IBlindPaths Paths => _options.Paths;

    public string StatusAreaName => "menu bar";

    // 36 px (18 pt at 2x): the same monochrome image the full app uses in its menu bar. The 128 px one was far larger than a menu-bar item.
    public string TrayIconAsset => "avares://BeeMemoryBank.BlindDesktop/Assets/tray-template@2x.png";

    public bool TrayIconIsTemplate => true;

    /// <summary><c>~/Library/Logs/BeeMemoryBankBlind/error.log</c> (Console.app lists that folder); a scratch run keeps the default file in the temp folder.</summary>
    public string? ErrorLogPath => _customFolder
        ? null
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Logs", "BeeMemoryBankBlind", "error.log");

    public void AddSeams(IServiceCollection services)
    {
        if (_secretStore is not null) services.AddSingleton(_secretStore);
        services.AddMacOsBlindSeams(_options);
    }

    public IInstanceGuard? TryAcquireInstance()
    {
        var instance = FileLockSingleInstance.TryAcquire(Paths.DataDirectory);
        return instance is null ? null : new FileLockInstanceGuard(instance);
    }

    public bool SignalRunningInstance() => FileLockSingleInstance.SignalRunningInstance(Paths.DataDirectory);

    public IReadOnlyList<SelfCheckItem> SelfCheck()
    {
        var items = new List<SelfCheckItem>();
        var folder = Paths.DataDirectory;

        var mode = File.GetUnixFileMode(folder);
        var expected = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        items.Add(new SelfCheckItem("private folder", mode == expected, $"{folder} has mode {Convert.ToString((int)mode, 8)} (expected 700)"));

        var socket = FileLockSingleInstance.SocketPathFor(folder);
        items.Add(new SelfCheckItem("activation socket", socket is not null,
            socket is null ? "no socket path is short enough" : $"{socket} ({Encoding.UTF8.GetByteCount(socket)} bytes, a file, no network port)"));

        // The seams the platform registers are the macOS ones (constructing them touches nothing: no keychain call, no file).
        var services = new ServiceCollection();
        AddSeams(services);
        using var provider = services.BuildServiceProvider();
        var expectedTypes = new (Type Seam, Type Implementation)[]
        {
            (typeof(IBlindPaths), typeof(MacOsBlindPaths)),
            (typeof(IBlindStateStore), typeof(MacOsBlindStateStore)),
            (typeof(IBlindAutostart), typeof(MacOsBlindAutostart)),
        };
        foreach (var (seam, implementation) in expectedTypes)
        {
            var actual = provider.GetRequiredService(seam).GetType();
            items.Add(new SelfCheckItem("seam " + seam.Name, actual == implementation, actual.Name));
        }
        if (_secretStore is null)
        {
            // Registered as a factory: resolving it builds the Keychain adapter object (RequireMacOS only), it makes no keychain call.
            var secrets = provider.GetRequiredService<IBlindSecretStore>();
            items.Add(new SelfCheckItem("seam IBlindSecretStore", secrets is MacOsKeychainSecretStore,
                $"{secrets.GetType().Name}, Keychain service '{_options.KeychainService}' (not opened)"));
        }

        var autostart = provider.GetRequiredService<MacOsBlindAutostart>();
        var expectedAgents = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents");
        items.Add(new SelfCheckItem("login item", autostart.PlistPath.StartsWith(expectedAgents, StringComparison.Ordinal),
            $"label '{autostart.Label}', file {autostart.PlistPath}, program {string.Join(' ', autostart.ProgramArguments)}"));
        return items;
    }

    /// <summary>A short, stable key for a scratch folder: 8 hex characters of the hash of its full path (a trailing separator does not matter).</summary>
    internal static string FolderKey(string dataDirectory) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory)))))[..8].ToLowerInvariant();

    /// <summary>Adapts the Unix file-lock instance to the host's interface; a failing handler is written to the error log.</summary>
    private sealed class FileLockInstanceGuard(FileLockSingleInstance inner) : IInstanceGuard
    {
        public void Listen(Action onActivate) =>
            inner.Listen(onActivate, ex => ErrorLog.Write("Showing the window for a second start failed", ex));

        public void Dispose() => inner.Dispose();
    }
}
