namespace BeeMemoryBank.Core.Exceptions;

/// <summary>
/// This node cannot write sealed metadata yet: a DEK rotation it started created the next metadata key, and the
/// <c>metadata_key_set</c> event that tells the other nodes about it is not logged yet (docs/design/step-a-spec.md 4.4). Sealing
/// with the previous generation in the meantime would let a node the rotation left out read what is written now, so the write waits.
/// Deferrable: the publication is retried every minute and at every unlock, and the caller may retry the same request.
/// Maps to HTTP 503 with <c>Retry-After</c>.
/// </summary>
/// <remarks>
/// Derives from <see cref="InvalidOperationException"/> for the migration reason described on <see cref="ConflictException"/>.
/// </remarks>
public sealed class MetadataKeyPublishPendingException()
    : InvalidOperationException("The new metadata key of the last key rotation is not published yet; writing is paused for a moment. Try again shortly.")
{
    /// <summary>The machine-readable code of the error body.</summary>
    public const string ErrorCode = "metadata_key_publish_pending";

    /// <summary>The publication is retried this often, so a caller asking again after this many seconds meets the next attempt.</summary>
    public const int RetryAfterSeconds = 60;
}
