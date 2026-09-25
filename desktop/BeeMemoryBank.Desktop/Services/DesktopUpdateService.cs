using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace BeeMemoryBank.Desktop.Services;

/// <summary>
/// Updates the installed desktop app from published GitHub releases (Velopack). This is separate
/// from the node's own update pipeline (/node/update/*, operator feeds, peer-distributed
/// artifacts), which serves servers and networks; a person who installed Setup.exe has none of
/// that configured and must still get updates.
/// </summary>
/// <remarks>
/// Only published (non-draft, non-prerelease) releases of the repository are considered. Until the
/// installer is code-signed, an update is exactly as trustworthy as the Setup.exe downloaded from
/// the same releases page.
/// </remarks>
public sealed class DesktopUpdateService
{
    public const string RepositoryUrl = "https://github.com/ultrathinker/BeeMemoryBank";

    /// <summary>
    /// Test/ops override: a local release folder or an http(s) feed URL used instead of GitHub.
    /// </summary>
    public const string SourceOverrideVariable = "BMB_DESKTOP_UPDATE_SOURCE";

    private readonly UpdateManager? _manager;
    private UpdateInfo? _downloaded;

    public DesktopUpdateService()
    {
        try
        {
            _manager = new UpdateManager(CreateSource());
        }
        catch (Exception ex)
        {
            // Not an installed build (dev run, portable copy without a manifest): updates are off.
            Console.WriteLine($"Desktop updates disabled: {ex.Message}");
        }
    }

    /// <summary>True for an app installed by Setup.exe; dev builds never self-update.</summary>
    public bool IsAvailable => _manager is { IsInstalled: true, IsPortable: false };

    public string? CurrentVersion => _manager?.CurrentVersion?.ToString();

    /// <summary>The version downloaded and waiting for a restart, if any.</summary>
    public string? ReadyVersion => _downloaded?.TargetFullRelease.Version.ToString();

    /// <summary>
    /// Checks the source and downloads a newer release in the background. Returns the downloaded
    /// version, or null when already up to date. Never applies anything by itself.
    /// </summary>
    public async Task<string?> CheckAndDownloadAsync(CancellationToken ct = default)
    {
        if (!IsAvailable || _manager is null) return null;
        if (_downloaded is not null) return ReadyVersion;

        var info = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
        if (info is null || info.IsDowngrade) return null;

        await _manager.DownloadUpdatesAsync(info, null, ct).ConfigureAwait(false);
        _downloaded = info;
        return ReadyVersion;
    }

    /// <summary>
    /// Restarts into the downloaded version. The caller stops the node first, so the database is
    /// closed cleanly before Velopack replaces the files.
    /// </summary>
    public void ApplyAndRestart()
    {
        if (_manager is null || _downloaded is null)
            throw new InvalidOperationException("No downloaded update to apply.");
        _manager.ApplyUpdatesAndRestart(_downloaded.TargetFullRelease);
    }

    private static IUpdateSource CreateSource()
    {
        var overrideSource = Environment.GetEnvironmentVariable(SourceOverrideVariable);
        if (!string.IsNullOrWhiteSpace(overrideSource))
        {
            if (Uri.TryCreate(overrideSource, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                return new SimpleWebSource(uri, null, 30);
            return new SimpleFileSource(new DirectoryInfo(overrideSource));
        }
        return new GithubSource(RepositoryUrl, null, false, null);
    }
}
