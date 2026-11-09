namespace BeeMemoryBank.Core.Models;

/// <summary>
/// One folder rule together with who it belongs to and the folder's CURRENT path, read in a single
/// query so the "which folders carry a rule" overview (<c>GET /api/access/folder-marks</c>) costs a
/// fixed number of queries however many rules there are.
/// <para>
/// Only live data reaches this row: the folder is not deleted, a user subject is an active
/// non-superadmin account and a role subject is not the superadmin role (superadmins bypass folder
/// rules, so a stale row left over from before a promotion is never enforced and must not be shown
/// as if it were).
/// </para>
/// </summary>
public sealed class FolderAclMarkRow
{
    public const string UserSubject = "user";
    public const string RoleSubject = "role";

    /// <summary><see cref="UserSubject"/> or <see cref="RoleSubject"/>.</summary>
    public string SubjectType { get; set; } = "";

    /// <summary>The user id (as text) or the role name: what the Users and Roles pages key a subject by.</summary>
    public string SubjectId { get; set; } = "";

    /// <summary>The username of a user, or the display name of a role.</summary>
    public string SubjectName { get; set; } = "";

    public Guid FolderId { get; set; }

    /// <summary>The folder's current path (after any rename or move), never a stale copy.</summary>
    public string FolderPath { get; set; } = "";

    public AclEffect Effect { get; set; }

    /// <summary>Only meaningful on an allow rule: true = read-only, false = read and write.</summary>
    public bool IsReadOnly { get; set; }
}
