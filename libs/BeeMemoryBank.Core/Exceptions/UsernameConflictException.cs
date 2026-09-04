namespace BeeMemoryBank.Core.Exceptions;

/// <summary>
/// A UNIQUE violation on <c>tbl_user.username</c>. It gets its own type rather than reusing
/// <see cref="ConflictException"/> because user deletion retries on exactly this failure — it
/// renames the row to a random <c>_del_xxx</c> suffix that can collide with an earlier one — and
/// must not silently retry any other conflict that happens to surface from the same call.
/// </summary>
public class UsernameConflictException : ConflictException
{
    public UsernameConflictException(string message) : base(message)
    {
    }

    public UsernameConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
