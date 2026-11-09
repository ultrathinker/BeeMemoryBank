using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Services;

/// <summary>One user or role named by a folder rule, as the tree marks show it.</summary>
/// <param name="Type"><see cref="FolderAclMarkRow.UserSubject"/> or <see cref="FolderAclMarkRow.RoleSubject"/>.</param>
/// <param name="Id">The user id (as text) or the role name: the key the Users and Roles pages open a subject by.</param>
/// <param name="Name">The username of a user, or the display name of a role.</param>
public sealed record FolderMarkSubject(string Type, string Id, string Name);

/// <summary>
/// One folder that carries at least one rule set DIRECTLY on it, with everybody those rules name.
/// Subfolders that merely inherit a rule are not described here: a rule applies to the subtree by
/// path prefix, but the mark belongs to the folder it was set on.
/// </summary>
/// <param name="FolderId">The folder.</param>
/// <param name="Path">The folder's current path.</param>
/// <param name="HiddenFrom">Subjects with a deny rule on the folder.</param>
/// <param name="ReadOnly">Subjects with a read-only allow rule on the folder.</param>
/// <param name="OpenTo">Subjects with a read-and-write allow rule on the folder.</param>
public sealed record FolderMark(
    Guid FolderId,
    string Path,
    IReadOnlyList<FolderMarkSubject> HiddenFrom,
    IReadOnlyList<FolderMarkSubject> ReadOnly,
    IReadOnlyList<FolderMarkSubject> OpenTo);

/// <summary>
/// Turns the flat list of folder rules (user rules and role rules together) into one
/// <see cref="FolderMark"/> per folder that has a rule set directly on it. There is one kind of mark: any folder with a rule of
/// its own has "custom permissions", and the three lists say what they are (hidden from, read only, open to).
/// </summary>
public static class FolderMarks
{
    public static List<FolderMark> Build(IEnumerable<FolderAclMarkRow> rows)
    {
        var result = new List<FolderMark>();

        foreach (var group in rows.GroupBy(r => r.FolderId))
        {
            var hidden = new List<FolderMarkSubject>();
            var readOnly = new List<FolderMarkSubject>();
            var open = new List<FolderMarkSubject>();

            foreach (var row in group)
            {
                var subject = new FolderMarkSubject(row.SubjectType, row.SubjectId, row.SubjectName);
                if (row.Effect == AclEffect.Deny) hidden.Add(subject);
                else if (row.IsReadOnly) readOnly.Add(subject);
                else open.Add(subject);
            }

            result.Add(new FolderMark(
                group.Key, group.First().FolderPath,
                SortSubjects(hidden), SortSubjects(readOnly), SortSubjects(open)));
        }

        return result.OrderBy(m => m.Path, StringComparer.Ordinal).ThenBy(m => m.FolderId).ToList();
    }

    // Users first, then roles; each group alphabetical (ignoring case), the id as the tie-break so
    // the order never depends on how the database happened to return the rows.
    private static List<FolderMarkSubject> SortSubjects(List<FolderMarkSubject> subjects) =>
        subjects
            .OrderBy(s => s.Type == FolderAclMarkRow.UserSubject ? 0 : 1)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Id, StringComparer.Ordinal)
            .ToList();
}
