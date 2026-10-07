using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace BeeMemoryBank.Core.IO;

/// <summary>
/// Files that hold a secret in clear (the blind node's TLS key, its identity seed, backup credentials): created for
/// their owner only from the first byte, and written so that the final name never exists half-written.
///
/// <para>"Owner only" per platform. Linux and macOS: mode 0600, set at creation. Windows (review release-a #7): a
/// protected DACL - nothing inherited from the folder - without the entries that let every account of the machine in
/// (Everyone, Users, Authenticated Users, Interactive, Guests and the like), with the current account and SYSTEM given
/// full control. Entries for named accounts and Administrators are kept: a folder an operator granted to a service
/// account must not lock that account out of its own node, and an administrator can take any file anyway.</para>
///
/// <para>Existing files are repaired in place (<see cref="TryTighten"/>), never refused: every Windows install before
/// this has these files with the folder's inherited ACL, and refusing them would stop every one of those nodes at its
/// next start.</para>
/// </summary>
public static class OwnerOnlyFile
{
    /// <summary>
    /// Creates <paramref name="path"/> for writing, failing if anything exists there (a link included: CreateNew does
    /// not follow one). On Linux and macOS the mode is 0600 from creation, never chmod-ed afterwards, so there is no
    /// moment the bytes are on disk with the umask's default permissions. On Windows the owner-only DACL is set through
    /// the new, still empty file's own handle, before a byte is written.
    /// </summary>
    public static FileStream CreateNew(string path) => CreateNew(path, VolumeKeepsAcls);

    /// <param name="volumeKeepsAcls">Whether the volume of the path keeps a per-file ACL at all (test seam).</param>
    internal static FileStream CreateNew(string path, Func<string, bool> volumeKeepsAcls)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
            });
        }

        // The handle may read and change the file's own ACL (READ_CONTROL, WRITE_DAC) besides writing it, so the
        // owner-only DACL replaces the inherited one through it while the file is still empty.
        var stream = new FileInfo(path).Create(
            FileMode.CreateNew,
            FileSystemRights.Write | FileSystemRights.ReadPermissions | FileSystemRights.ChangePermissions | FileSystemRights.Synchronize,
            FileShare.None, 4096, FileOptions.None, fileSecurity: null);
        try
        {
            // A FAT32 or exFAT disk (a USB stick, an SD card) has no ACLs to set: the owner-only guarantee does not exist there, and failing
            // would stop every snapshot and a blind node's first start on such a disk. Any other failure still refuses the file.
            if (volumeKeepsAcls(path))
            {
                var current = stream.GetAccessControl();
                if (!IsOwnerOnly(current)) stream.SetAccessControl(Tightened(current));
            }
        }
        catch
        {
            stream.Dispose();
            TryDelete(path);
            throw;
        }
        return stream;
    }

    /// <summary>
    /// Whether the volume <paramref name="path"/> is on keeps an ACL per file: false for the FAT family (FAT, FAT32, exFAT), true for
    /// everything else, and when the volume cannot be told (a network path): then the ACL step runs, and fails the way it did before.
    /// </summary>
    private static bool VolumeKeepsAcls(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return true;
            var format = new DriveInfo(root).DriveFormat;
            return !format.StartsWith("FAT", StringComparison.OrdinalIgnoreCase)
                && !format.Equals("exFAT", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// Writes <paramref name="content"/> as the new file <paramref name="path"/>: into a random-named temp file beside
    /// it (<see cref="CreateNew"/>), flushed to the disk, read back and compared, and only then renamed onto the final
    /// name, without replacing anything. The final name therefore appears complete or not at all: a crash, a full
    /// volume or a power cut leaves at most a temp file, which is removed when the write fails in-process. A rename
    /// within the folder keeps the temp file's owner-only permissions.
    /// </summary>
    /// <param name="beforeRename">Test seam: called with the temp path once it holds the checked bytes.</param>
    /// <exception cref="IOException">Something already exists at <paramref name="path"/> (another start won), or the
    /// bytes did not reach the disk as written.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="path"/> is a link.</exception>
    public static void WriteNew(string path, ReadOnlySpan<byte> content, Action<string>? beforeRename = null)
    {
        RefuseLink(path);
        var temp = path + "." + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant() + ".tmp";
        // Outside the try: a temp name that already exists is not ours to remove.
        var file = CreateNew(temp);
        try
        {
            using (file)
            {
                file.Write(content);
                // On the platter before the rename: a rename that survives a power cut while the bytes are still in
                // the page cache is the empty final file this method exists to make impossible.
                file.Flush(flushToDisk: true);
            }

            // Read back before it can become the real file: a short write (a full volume) is caught while the temp
            // file is still the only thing to lose.
            var written = File.ReadAllBytes(temp);
            try
            {
                if (!written.AsSpan().SequenceEqual(content))
                    throw new IOException($"{temp} does not hold what was written; {path} was not created.");
            }
            finally
            {
                Array.Clear(written);
            }

            beforeRename?.Invoke(temp);
            File.Move(temp, path, overwrite: false);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>
    /// Repairs an existing secret's permissions in place (Windows; elsewhere the mode set at creation is the rule, and
    /// there is nothing to do here). True when the file was changed, false when it is missing or already owner-only.
    /// A failure is returned in <paramref name="problem"/>, never thrown: the caller logs it and the node starts anyway.
    /// </summary>
    public static bool TryTighten(string path, out string? problem)
    {
        problem = null;
        if (!OperatingSystem.IsWindows() || !File.Exists(path)) return false;
        try
        {
            var info = new FileInfo(path);
            if (info.LinkTarget is not null) return false; // not followed; the readers refuse a link themselves
            var current = info.GetAccessControl();
            if (IsOwnerOnly(current)) return false;
            info.SetAccessControl(Tightened(current));
            return true;
        }
        catch (Exception ex)
        {
            // Whatever it is (no right to change the ACL, a file system without ACLs, ...): reported, never fatal.
            problem = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Whether <paramref name="path"/> is owner-only in the sense above: always true off Windows (the mode is set at
    /// creation) and for a missing file. For tests and diagnostics.
    /// </summary>
    public static bool IsOwnerOnly(string path) =>
        !OperatingSystem.IsWindows() || !File.Exists(path) || IsOwnerOnly(new FileInfo(path).GetAccessControl());

    /// <summary>
    /// A link at a secret's path is refused, not followed: a write through it lands wherever it points, and a read
    /// through it adopts a file from outside the data folder.
    /// </summary>
    public static void RefuseLink(string path)
    {
        if (new FileInfo(path).LinkTarget is not null)
            throw new InvalidOperationException(
                $"{path} is a symlink or junction; refusing to use it. It belongs in the data folder as a regular file.");
    }

    // Every account of the machine, or anyone at all: what an owner-only secret must not be readable by.
    private static readonly string[] BroadSids =
    [
        "S-1-1-0",      // Everyone
        "S-1-2-0",      // LOCAL
        "S-1-2-1",      // CONSOLE LOGON
        "S-1-5-2",      // NETWORK
        "S-1-5-3",      // BATCH
        "S-1-5-4",      // INTERACTIVE
        "S-1-5-6",      // SERVICE
        "S-1-5-7",      // ANONYMOUS LOGON
        "S-1-5-11",     // Authenticated Users
        "S-1-5-32-545", // BUILTIN\Users
        "S-1-5-32-546", // BUILTIN\Guests
        "S-1-5-113",    // Local account (every local account of the machine)
        "S-1-15-2-1",   // ALL APPLICATION PACKAGES (every packaged app; a folder under Program Files grants them read)
        "S-1-15-2-2"    // ALL RESTRICTED APPLICATION PACKAGES
    ];

    private const string SystemSid = "S-1-5-18";

    [SupportedOSPlatform("windows")]
    private static bool IsOwnerOnly(FileSecurity security)
    {
        if (!security.AreAccessRulesProtected) return false;
        var me = CurrentUser().Value;
        var meHasFullControl = false;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            var sid = rule.IdentityReference.Value;
            if (rule.AccessControlType == AccessControlType.Allow && BroadSids.Contains(sid)) return false;
            if (rule.AccessControlType == AccessControlType.Allow && sid == me
                && (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl)
                meHasFullControl = true;
        }
        return meHasFullControl;
    }

    /// <summary>
    /// <paramref name="current"/> without inheritance (what was inherited is copied as explicit entries, so named
    /// accounts and Administrators keep their access), without the broad groups, with full control for the current
    /// account and SYSTEM.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static FileSecurity Tightened(FileSecurity current)
    {
        var result = new FileSecurity();
        result.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (FileSystemAccessRule rule in current.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow && BroadSids.Contains(rule.IdentityReference.Value))
                continue;
            result.AddAccessRule(new FileSystemAccessRule(
                rule.IdentityReference, rule.FileSystemRights, InheritanceFlags.None, PropagationFlags.None, rule.AccessControlType));
        }
        foreach (var sid in new[] { CurrentUser(), new SecurityIdentifier(SystemSid) })
            result.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        return result;
    }

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier CurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User!;
    }

    private static void TryDelete(string temp)
    {
        try
        {
            File.Delete(temp);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
