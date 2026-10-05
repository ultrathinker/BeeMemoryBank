using System.Security.Cryptography;

namespace BeeMemoryBank.Infrastructure.Secrets;

/// <summary>Deterministic test double. It copies values at both API boundaries and is safe to call from several threads.</summary>
public sealed class InMemoryUserSecretStore : IUserSecretStore
{
    private readonly Dictionary<(string Purpose, string Account), byte[]> _values = new();
    private readonly object _gate = new();

    public bool IsSupported { get; set; } = true;
    public UserSecretStoreException? ReadFailure { get; set; }
    public UserSecretStoreException? WriteFailure { get; set; }
    public UserSecretStoreException? DeleteFailure { get; set; }

    public byte[]? Read(string purpose, string account)
    {
        Validate(purpose, account);
        if (!IsSupported) throw Unavailable();
        if (ReadFailure is not null) throw ReadFailure;
        lock (_gate) return _values.TryGetValue((purpose, account), out var value) ? value.ToArray() : null;
    }

    public void Write(string purpose, string account, ReadOnlySpan<byte> value)
    {
        Validate(purpose, account);
        if (!IsSupported) throw Unavailable();
        if (WriteFailure is not null) throw WriteFailure;
        var copy = value.ToArray();
        lock (_gate)
        {
            if (_values.TryGetValue((purpose, account), out var previous)) CryptographicOperations.ZeroMemory(previous);
            _values[(purpose, account)] = copy;
        }
    }

    public void Delete(string purpose, string account)
    {
        Validate(purpose, account);
        if (!IsSupported) throw Unavailable();
        if (DeleteFailure is not null) throw DeleteFailure;
        lock (_gate)
        {
            if (_values.Remove((purpose, account), out var value)) CryptographicOperations.ZeroMemory(value);
        }
    }

    public void DisposeValues()
    {
        lock (_gate)
        {
            foreach (var value in _values.Values) CryptographicOperations.ZeroMemory(value);
            _values.Clear();
        }
    }

    private static void Validate(string purpose, string account)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
    }

    private static UserSecretStoreException Unavailable() =>
        new(UserSecretStoreFailureKind.Unavailable, "The user secret store is unavailable.");
}
