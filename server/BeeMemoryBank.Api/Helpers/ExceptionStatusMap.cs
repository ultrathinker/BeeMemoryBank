using BeeMemoryBank.Core.Exceptions;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Storage.Sealed;

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
        BeeMemoryBank.Crypto.KdfBusyException e => (503, e.Message),
        MetadataKeyPublishPendingException e => (503, e.Message),
        // A sealed node whose database is closed, held for a file copy, or whose key does not open the file (docs/design/step-b-spec.md 5.6, 5.10):
        // a deferrable 503 that names itself, never a 409 or a 500.
        VaultSealedException e => (503, e.Message),
        VaultBusyException e => (503, e.Message),
        VaultKeyMismatchException => (503, "This node's database could not be opened with its key: the boot store does not belong to this database."),
        InsufficientDiskSpaceException e => (507, e.Message),
        // A job that has to hold a whole database in memory and cannot (BMB-304): the message names the memory limit that would be enough.
        // Like a full disk, not a conflict and not a retry-later: the node needs more room.
        NotEnoughMemoryException e => (507, e.Message),
        // A snapshot whose database entry cannot be opened (damaged, cut short, another vault's): the request is what is wrong, the sentence is fixed.
        BeeMemoryBank.Api.Services.SnapshotContainerException e => (400, e.Message),
        ConflictException e => (409, e.Message),

        KeyNotFoundException e => (404, e.Message),
        ArgumentException e => (400, e.Message),
        UnauthorizedAccessException e => (403, e.Message),
        InvalidOperationException e => (409, e.Message),
        _ => (500, "Internal server error")
    };

    /// <summary>
    /// The machine-readable code and the <c>Retry-After</c> seconds of the few errors a caller is meant to act on, or
    /// <c>(null, null)</c>. Kept apart from <see cref="Map"/> so that its tuple, which several pipelines deconstruct, stays as it is.
    /// </summary>
    public static (string? Code, int? RetryAfterSeconds) Details(Exception? ex) => ex switch
    {
        MetadataKeyPublishPendingException => (MetadataKeyPublishPendingException.ErrorCode, MetadataKeyPublishPendingException.RetryAfterSeconds),
        VaultSealedException => ("vault_sealed", 30),
        VaultBusyException => ("vault_busy", 5),
        VaultKeyMismatchException => ("vault_key_mismatch", 30),
        NotEnoughMemoryException => ("not_enough_memory", null),
        BeeMemoryBank.Api.Services.SnapshotContainerException => ("bad_snapshot", null),
        _ => (null, null)
    };
}
