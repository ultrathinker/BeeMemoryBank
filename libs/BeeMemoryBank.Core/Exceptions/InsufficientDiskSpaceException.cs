namespace BeeMemoryBank.Core.Exceptions;

/// <summary>
/// A pre-flight free-space check refused the operation. Snapshot create and snapshot restore both
/// need room for a second copy of the vault, and the network-restore flow treats running out of it
/// as an operator decision ("continue without a pre-restore backup?") rather than a plain failure —
/// which is why it has to be told apart from every other refusal raised on the same path.
/// </summary>
/// <remarks>
/// The message names the path and the shortfall and is shown verbatim to the operator, so keep it
/// specific. Derives from <see cref="InvalidOperationException"/> for the migration reason
/// described on <see cref="ConflictException"/>.
/// </remarks>
public class InsufficientDiskSpaceException : InvalidOperationException
{
    public InsufficientDiskSpaceException(string message) : base(message)
    {
    }
}
