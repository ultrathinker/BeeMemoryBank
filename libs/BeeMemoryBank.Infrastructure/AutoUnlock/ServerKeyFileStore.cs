using System.Security.Cryptography;
using System.Text;
using BeeMemoryBank.Core.IO;

namespace BeeMemoryBank.Infrastructure.AutoUnlock;

/// <summary>
/// The file that holds the server checkbox's secret (docs/design/step-b-spec.md 11.2 part 2): 64 hex characters in a file on every operating
/// system, never in DPAPI or the Keychain. A seam so that a test can prove that a server which never used the checkbox does not touch any
/// key file, and so that the permission rule is one piece of code.
/// </summary>
public interface IServerKeyFileStore
{
    bool Exists(string path);

    /// <summary>
    /// The 32 secret bytes in the file, or null with <paramref name="problem"/> one of <c>missing</c>, <c>link</c>, <c>not_owner_only</c>,
    /// <c>malformed</c>, <c>unreadable</c>. The caller clears the bytes. Never throws for a bad file; never names a path or a byte in a message.
    /// </summary>
    byte[]? ReadSecret(string path, out string? problem);

    /// <summary>Writes a node-created key file: owner-only from the first byte, atomically, never replacing (a leftover is wiped first by the caller).</summary>
    void WriteNew(string path, ReadOnlySpan<byte> secret);

    /// <summary>Overwrites a node-created key file with zeros and removes it. Only ever called for the file the node created itself.</summary>
    void WipeAndDelete(string path);
}

public sealed class PhysicalServerKeyFileStore : IServerKeyFileStore
{
    public bool Exists(string path) => File.Exists(path);

    public byte[]? ReadSecret(string path, out string? problem)
    {
        problem = null;
        try
        {
            if (!File.Exists(path)) { problem = "missing"; return null; }
            if (new FileInfo(path).LinkTarget is not null) { problem = "link"; return null; }
            if (!IsOwnerOnly(path)) { problem = "not_owner_only"; return null; }

            var text = File.ReadAllText(path, Encoding.ASCII).Trim();
            if (text.Length != 64 || !text.All(Uri.IsHexDigit)) { problem = "malformed"; return null; }
            return Convert.FromHexString(text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or FormatException)
        {
            problem = "unreadable";
            return null;
        }
    }

    public void WriteNew(string path, ReadOnlySpan<byte> secret)
    {
        var hex = Encoding.ASCII.GetBytes(Convert.ToHexStringLower(secret));
        try
        {
            OwnerOnlyFile.WriteNew(path, hex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hex);
        }
    }

    public void WipeAndDelete(string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            if (new FileInfo(path).LinkTarget is null)
            {
                var length = (int)Math.Min(new FileInfo(path).Length, 4096);
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
                stream.Write(new byte[length]);
                stream.Flush(flushToDisk: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Deleting is what matters; an overwrite that cannot happen (a file in use) must not keep the file alive.
        }
        File.Delete(path);
    }

    /// <summary>
    /// Owner-only: on Windows the file's ACL has no entry for Everyone, Users, Authenticated Users and the like (<see cref="OwnerOnlyFile.IsOwnerOnly"/>);
    /// on Linux and macOS the mode has no group and no other bit. A file the operator mounted into a container must be chmod 0400 or 0600 for
    /// the user the node runs as: a key that every local account can read is not a key.
    /// </summary>
    internal static bool IsOwnerOnly(string path)
    {
        if (OperatingSystem.IsWindows()) return OwnerOnlyFile.IsOwnerOnly(path);
        const UnixFileMode Others = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        return (File.GetUnixFileMode(path) & Others) == 0;
    }
}
