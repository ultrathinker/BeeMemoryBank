using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace BeeMemoryBank.AppPaths;

/// <summary>An exact lock file identity supplied by the process that currently owns the lock.</summary>
public sealed record HeldLockProof(string Path, ulong Volume, ulong Index)
{
    public FileIdentityInfo Identity => new(Volume, Index, 1);
}

/// <summary>
/// The only authority that permits the plain-copy scanner to leave an unreadable runtime lock alone.
/// In-process owners register their handle here; bmbd passes its own proof to the Api child in the
/// environment.  A path spelling without a matching file identity is never a proof.
/// </summary>
public static class HeldLockProofs
{
    public const string EnvironmentVariable = "BMB_HELD_LOCK_PROOFS";

    private static readonly ConcurrentDictionary<string, HeldLockProof> InProcess = new(StringComparer.Ordinal);

    /// <summary>Creates a proof from the held handle. Null means the host cannot prove its lock, so sealing fails closed.</summary>
    public static HeldLockProof? FromHeldHandle(string path, SafeFileHandle handle)
    {
        var identity = FileIdentity.TryGet(handle);
        return identity is { } id ? new HeldLockProof(Path.GetFullPath(path), id.Volume, id.Index) : null;
    }

    /// <summary>Registers an in-process lock for its lifetime.</summary>
    public static IDisposable? Register(HeldLockProof? proof)
    {
        if (proof is null) return null;
        var path = Path.GetFullPath(proof.Path);
        var normalized = proof with { Path = path };
        InProcess[path] = normalized;
        return new Registration(path, normalized);
    }

    /// <summary>Serializes a held parent lock for a child process.</summary>
    public static string? ForChildEnvironment(HeldLockProof? proof) => proof is null ? null : JsonSerializer.Serialize(new[] { proof });

    /// <summary>
    /// True only for the exact registered/handed-down lock object. The caller has already rejected
    /// links and directories; link count one prevents a hard-linked plaintext file borrowing a lock identity.
    /// </summary>
    public static bool Matches(string path, FileIdentityInfo identity)
    {
        var full = Path.GetFullPath(path);
        if (identity.LinkCount != 1) return false;
        return Proofs().Any(proof => string.Equals(proof.Path, full, StringComparison.Ordinal)
            && proof.Volume == identity.Volume && proof.Index == identity.Index);
    }

    private static IEnumerable<HeldLockProof> Proofs()
    {
        foreach (var proof in InProcess.Values) yield return proof;

        var serialized = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(serialized)) yield break;
        HeldLockProof[]? inherited;
        try { inherited = JsonSerializer.Deserialize<HeldLockProof[]>(serialized); }
        catch (JsonException) { yield break; }
        if (inherited is null) yield break;
        foreach (var proof in inherited)
        {
            if (string.IsNullOrWhiteSpace(proof.Path)) continue;
            HeldLockProof normalized;
            try { normalized = proof with { Path = Path.GetFullPath(proof.Path) }; }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { continue; }
            yield return normalized;
        }
    }

    private sealed class Registration(string path, HeldLockProof proof) : IDisposable
    {
        public void Dispose()
        {
            if (InProcess.TryGetValue(path, out var current) && current == proof)
                InProcess.TryRemove(path, out _);
        }
    }
}
