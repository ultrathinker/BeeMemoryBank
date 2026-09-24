namespace BeeMemoryBank.Core.Exceptions;

/// <summary>
/// The master DEK is not in memory, so the operation cannot read or write encrypted content.
/// Maps to HTTP 403 — the same status every endpoint already returns from its own
/// <c>if (!session.IsUnlocked)</c> pre-check, so a lock discovered deeper down reports the same
/// status as the pre-check for the very same condition.
/// </summary>
/// <remarks>
/// Derives from <see cref="InvalidOperationException"/> for the migration reason described on
/// <see cref="ConflictException"/>: handlers that catch only the base type still handle it.
/// </remarks>
public class SessionLockedException : InvalidOperationException
{
    public SessionLockedException(string message) : base(message)
    {
    }
}
