namespace BeeMemoryBank.Infrastructure.Secrets;

public enum UserSecretStoreFailureKind
{
    Denied,
    Locked,
    Malformed,
    Unavailable
}

/// <summary>Non-secret diagnostic for an OS secret-store failure.</summary>
public sealed class UserSecretStoreException(UserSecretStoreFailureKind failureKind, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public UserSecretStoreFailureKind FailureKind { get; } = failureKind;
}
