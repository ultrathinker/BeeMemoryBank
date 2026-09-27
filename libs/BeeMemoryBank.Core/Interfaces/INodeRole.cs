namespace BeeMemoryBank.Core.Interfaces;

/// <summary>
/// What kind of node this process runs (BMB-43). A blind node (BMB_ROLE=blind) never holds the master
/// DEK: it stores and relays ciphertext and makes backups. Services that need the DEK, the notes UI,
/// search, embeddings and AI stay off in that role.
/// </summary>
public interface INodeRole
{
    bool IsBlind { get; }
}

/// <summary>Reads the role from BMB_ROLE ("blind"; anything else or unset = a full node).</summary>
public sealed class EnvironmentNodeRole : INodeRole
{
    public EnvironmentNodeRole() : this(Environment.GetEnvironmentVariable("BMB_ROLE")) { }

    public EnvironmentNodeRole(string? role) =>
        IsBlind = string.Equals(role?.Trim(), "blind", StringComparison.OrdinalIgnoreCase);

    public bool IsBlind { get; }
}
