using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace BeeMemoryBank.TestSupport;

/// <summary>
/// A Windows-ACL fact (review release-a #7): skipped on any other system, where the owner-only rule is the 0600 mode
/// the platform's own tests check.
/// </summary>
public sealed class WindowsAclFactAttribute : FactAttribute
{
    public WindowsAclFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows ACLs only; on Linux and macOS the file mode 0600 is checked instead";
    }
}

/// <summary>The Linux and macOS twin of <see cref="WindowsAclFactAttribute"/>: a fact on the 0600 file mode, skipped on Windows.</summary>
public sealed class UnixModeFactAttribute : FactAttribute
{
    public UnixModeFactAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "Unix file modes only; on Windows the owner-only ACL is checked instead";
    }
}

/// <summary>Builds the situation the owner-only rule exists for, and reads back what a file's ACL allows.</summary>
[SupportedOSPlatform("windows")]
public static class WindowsAcl
{
    public static readonly SecurityIdentifier BuiltinUsers = new("S-1-5-32-545");

    /// <summary>
    /// Adds the generic inherit-only entries present on the root of a normal non-system NTFS drive. They do not apply
    /// to <paramref name="dir"/> itself, but become rules on its children; CREATOR OWNER is substituted with the
    /// child's owner there. SDDL is used because <see cref="FileSystemAccessRule"/> deliberately refuses generic
    /// access masks such as GENERIC_ALL.
    /// </summary>
    public static void AddGenericInheritOnlyRules(string dir)
    {
        var info = new DirectoryInfo(dir);
        var security = info.GetAccessControl();
        var descriptor = security.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        security.SetSecurityDescriptorSddlForm(
            $"{descriptor}(A;OICIIO;GA;;;CO)(A;OICIIO;GRGX;;;BU)", AccessControlSections.Access);
        info.SetAccessControl(security);
    }

    /// <summary>
    /// A data folder outside the user's profile (C:\BMB\data, a service install): every account of the computer may read
    /// what is created in it, through an inheritable BUILTIN\Users entry.
    /// </summary>
    public static void MakeReadableByAllUsers(string dir)
    {
        var info = new DirectoryInfo(dir);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(BuiltinUsers, FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    /// <summary>Whether BUILTIN\Users (every local account) is allowed anything on the file.</summary>
    public static bool UsersCanRead(string path) =>
        Rules(path).Any(r => r.AccessControlType == AccessControlType.Allow && r.IdentityReference.Equals(BuiltinUsers));

    /// <summary>Whether the account running the test has full control of the file.</summary>
    public static bool OwnerHasFullControl(string path)
    {
        using var me = WindowsIdentity.GetCurrent();
        return Rules(path).Any(r => r.AccessControlType == AccessControlType.Allow && r.IdentityReference.Equals(me.User)
            && (r.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl);
    }

    public static bool IsProtected(string path) => new FileInfo(path).GetAccessControl().AreAccessRulesProtected;

    public static bool DirectoryUsersCanRead(string path) =>
        DirectoryRules(path).Any(r => r.AccessControlType == AccessControlType.Allow && r.IdentityReference.Equals(BuiltinUsers));

    public static bool IsDirectoryProtected(string path) => new DirectoryInfo(path).GetAccessControl().AreAccessRulesProtected;

    public static bool DirectoryOwnerHasFullControl(string path)
    {
        using var me = WindowsIdentity.GetCurrent();
        return DirectoryRules(path).Any(r => r.AccessControlType == AccessControlType.Allow && r.IdentityReference.Equals(me.User)
            && (r.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl);
    }

    private static IEnumerable<FileSystemAccessRule> Rules(string path) =>
        new FileInfo(path).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>();

    private static IEnumerable<FileSystemAccessRule> DirectoryRules(string path) =>
        new DirectoryInfo(path).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>();
}
