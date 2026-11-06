using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BeeMemoryBank.Core.IO;

namespace BeeMemoryBank.Storage.PreUnlock;

/// <summary>One hint as the pre-unlock copy holds it: the account it belongs to and the text the person wrote.</summary>
public sealed record PasswordHintEntry(string Username, string Hint);

/// <summary>
/// The pre-unlock copy of the password hints. An encrypted node is closed after a restart, and the sign-in page has to show a
/// hint exactly then (the person forgot the password, so nothing can be unlocked) - while <c>tbl_user</c> is inside the closed
/// database. This file is the derived mirror that answers that one question without a key.
///
/// <para>The database stays the source of truth. The file is rewritten as a whole from <c>tbl_user</c> at every unlock and at
/// every close of the sealed database, and after every operation that changes a user, a password or a hint; a user without a
/// hint is simply absent, and a deleted user is gone with the next rewrite. It is never carried by a backup or a snapshot
/// (see <c>VaultFiles.IsTransient</c>); a restored database rebuilds it at its first unlock.</para>
///
/// <para>It holds no user names: each entry is keyed by HMAC-SHA-256 of the lower-cased name under a random salt stored in the
/// same file. The salt is not a secret (whoever can read the file can read it too), so this only keeps a copied file from
/// listing the node's accounts; the hint text itself is plaintext by design, because the sign-in page must show it before
/// anything is unlocked. Anyone who can read the data folder can read every hint: the UI says so, and no hint may contain a
/// secret.</para>
///
/// <para>The file is written through <see cref="OwnerOnlyFile.ReplaceAsync"/>: owner-only from the first byte, and the final
/// name holds either the old file or the complete new one, never a half write.</para>
/// </summary>
public sealed class PasswordHintFile(string path)
{
    /// <summary>The file beside the boot store; <c>VaultFiles</c> has the same name for the copies that must skip it.</summary>
    public const string FileName = "beememorybank.hints.json";

    private const int FormatVersion = 1;

    /// <summary>A hint is at most 120 characters; a file this large is not one of ours (and is not parsed).</summary>
    private const long MaxBytes = 4 * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public string FilePath { get; } = path;

    /// <summary>Test seam: wraps the temp file's stream, so a test can make the write fail the way a full volume does.</summary>
    internal Func<FileStream, Stream>? WrapWriteForTests { get; set; }

    /// <summary>
    /// The hint for <paramref name="username"/>, or null for an unknown name, a user without a hint, a missing file, a file
    /// that cannot be read and a file that is not one of ours. Never throws: the sign-in page's fallback is the generic
    /// sentence, not an error page.
    /// </summary>
    public async Task<string?> FindAsync(string username, CancellationToken ct = default)
    {
        try
        {
            var info = new FileInfo(FilePath);
            if (info.LinkTarget is not null || !info.Exists || info.Length > MaxBytes) return null;

            var bytes = await File.ReadAllBytesAsync(FilePath, ct).ConfigureAwait(false);
            var file = JsonSerializer.Deserialize<FileModel>(bytes, Json);
            if (file is not { Version: FormatVersion, Salt: { Length: > 0 }, Hints: { } entries }) return null;

            var salt = Convert.FromBase64String(file.Salt);
            var wanted = KeyOf(salt, username);
            foreach (var entry in entries)
                if (entry is { User: { } user, Hint: { } hint } && string.Equals(user, wanted, StringComparison.Ordinal))
                    return hint;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException
            or ArgumentException or CryptographicException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Replaces the whole file with the accounts that have a hint. A fresh random salt is made every time, so an old copy of
    /// the file cannot be matched against the new one; the write is atomic and owner-only.
    /// </summary>
    public async Task ReplaceAsync(IReadOnlyList<PasswordHintEntry> entries, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var salt = RandomNumberGenerator.GetBytes(32);
        var model = new FileModel
        {
            Version = FormatVersion,
            Salt = Convert.ToBase64String(salt),
            Hints = entries
                .Where(e => !string.IsNullOrWhiteSpace(e.Username) && !string.IsNullOrEmpty(e.Hint))
                .OrderBy(e => e.Username, StringComparer.OrdinalIgnoreCase)
                .Select(e => new EntryModel { User = KeyOf(salt, e.Username), Hint = e.Hint })
                .ToList()
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(model, Json);
        await OwnerOnlyFile.ReplaceAsync(FilePath, bytes, ct, wrapWrite: WrapWriteForTests).ConfigureAwait(false);
    }

    private static string KeyOf(byte[] salt, string username) =>
        Convert.ToBase64String(HMACSHA256.HashData(salt, Encoding.UTF8.GetBytes((username ?? "").Trim().ToLowerInvariant())));

    private sealed class FileModel
    {
        [JsonPropertyName("version")] public int Version { get; set; }
        [JsonPropertyName("salt")] public string? Salt { get; set; }
        [JsonPropertyName("hints")] public List<EntryModel>? Hints { get; set; }
    }

    private sealed class EntryModel
    {
        [JsonPropertyName("user")] public string? User { get; set; }
        [JsonPropertyName("hint")] public string? Hint { get; set; }
    }
}