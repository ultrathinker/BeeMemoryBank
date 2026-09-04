using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Hosting.AspNetCore;

/// <summary>
/// Persists the ASP.NET Core data-protection key ring under the node's data directory.
///
/// <para>
/// Left alone, ASP.NET Core picks the storage location itself and picks it outside the data
/// directory: a user-profile path when the host has one, and nothing at all — an in-memory ring,
/// regenerated on every start — when it doesn't. Either way the ring does not live with the rest
/// of the node's state, so it goes away exactly when the deployment changes: every antiforgery
/// token and auth cookie minted before the restart is then rejected after it, one baffling "the
/// form expired" for anyone who had a page already open. Under Docker the data directory is a
/// mounted volume, so putting the ring there makes it survive recreating the container too.
/// </para>
///
/// <para>
/// The ring is deliberately NOT encrypted with the vault's master DEK. That is the
/// obvious-looking idea and it deadlocks: the ring is needed to render and post the login form,
/// and that happens while the vault is still locked, so the one screen that can unlock the vault
/// would be waiting on the vault. It is likewise not wrapped with DPAPI — that is Windows-only
/// (Linux and Docker run this same code, and <c>OsAutoUnlockService</c> shows what guarding a
/// Windows-only path costs), and it would bind the ring to a single Windows account, so running
/// the node as a different service user would resurrect the exact failure this fixes. The keys
/// rest on the filesystem permissions of the data directory, the same protection the
/// <c>.internal-key</c> file sitting next to them already relies on.
/// </para>
/// </summary>
public static class DataProtectionExtensions
{
    /// <summary>Sub-directory of the data path that holds the key-ring XML files.</summary>
    public const string KeyRingDirectoryName = "dataprotection-keys";

    /// <summary>Absolute path of the key ring for a given data directory.</summary>
    public static string KeyRingPath(string dataPath) => Path.Combine(dataPath, KeyRingDirectoryName);

    /// <summary>
    /// Registers data protection with the key ring persisted at
    /// <c>&lt;dataPath&gt;/dataprotection-keys</c>, creating the directory if it does not exist
    /// (a fresh container starts with only the empty volume).
    /// </summary>
    /// <param name="applicationName">
    /// Pinned explicitly rather than left to the default, which is derived from the content root
    /// path: a published bundle and a <c>dotnet run</c> resolve different content roots, so the
    /// same ring would be read under two different purposes and look empty to one of them.
    /// </param>
    public static IServiceCollection AddPersistedDataProtection(
        this IServiceCollection services, string dataPath, string applicationName)
    {
        var keyRingPath = KeyRingPath(dataPath);
        Directory.CreateDirectory(keyRingPath);

        services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(keyRingPath))
            .SetApplicationName(applicationName);

        return services;
    }
}
