namespace BeeMemoryBank.Core.Exceptions;

/// <summary>
/// A DEK rotation could not start because a precondition that must hold BEFORE the destructive
/// rewrap failed — today, a mandatory <see cref="Services.IDekRotationHook"/> (for example, chat.db
/// rows that could not all be moved off the outgoing master DEK). Nothing has been changed when this
/// is thrown: the rewrap transaction was never opened and the session is still on the old DEK, so
/// the rotation can simply be retried once the cause is fixed.
/// </summary>
/// <remarks>
/// Apply paths treat it as "not yet", not as "failed": the rotation state stays Committing — a peer
/// retries it (next unlock, bounded automatic retry) and the initiator can accept the same commit
/// again — instead of becoming Failed, which nothing retries.
/// Derives from <see cref="InvalidOperationException"/> so existing endpoint handlers keep mapping it
/// to a 400 with its message.
/// </remarks>
public class DekRotationPreconditionException : InvalidOperationException
{
    public DekRotationPreconditionException(string message) : base(message)
    {
    }

    public DekRotationPreconditionException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
