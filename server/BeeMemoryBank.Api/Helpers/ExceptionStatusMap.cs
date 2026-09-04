using BeeMemoryBank.Core.Exceptions;

namespace BeeMemoryBank.Api.Helpers;

/// <summary>
/// The one place that decides which HTTP status an escaping exception becomes. Extracted from the
/// <c>UseExceptionHandler</c> block in Program.cs so the mapping can be asserted in a test: the
/// point of the typed exceptions is that a reworded message provably cannot move a status code,
/// and that is only provable if something checks the type→status pairs directly.
/// </summary>
public static class ExceptionStatusMap
{
    /// <summary>
    /// Returns the status code and the operator-facing message for <paramref name="ex"/>.
    /// Anything unrecognised is a 500 with a generic message — internals must not leak.
    /// </summary>
    public static (int StatusCode, string Message) Map(Exception? ex) => ex switch
    {
        // Derived arms MUST stay above their base types. SessionLockedException,
        // InsufficientDiskSpaceException and ConflictException all derive from
        // InvalidOperationException (see ConflictException's remarks for why), so the generic
        // InvalidOperationException arm below would otherwise swallow all three and hand back 409.
        SessionLockedException e => (403, e.Message),
        InsufficientDiskSpaceException e => (507, e.Message),
        ConflictException e => (409, e.Message),

        KeyNotFoundException e => (404, e.Message),
        ArgumentException e => (400, e.Message),
        UnauthorizedAccessException e => (403, e.Message),
        InvalidOperationException e => (409, e.Message),
        _ => (500, "Internal server error")
    };
}
