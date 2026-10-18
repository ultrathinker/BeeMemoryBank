namespace BeeMemoryBank.Core.Services;

/// <summary>
/// THE definition of "this vault has no sentinel", in the shared library so that the blind host, which does not contain the vault code
/// (<c>MasterKeyManager</c> is in BeeMemoryBank.Vault), asks the same question as every other door: NULL and an empty BLOB are the same thing
/// (a vault older than the check has the one, a partly migrated row the other). Nobody decides what a missing sentinel means with <c>== null</c>.
/// </summary>
public static class NodeSentinel
{
    public static bool IsMissing(byte[]? sentinel) => sentinel is not { Length: > 0 };
}
