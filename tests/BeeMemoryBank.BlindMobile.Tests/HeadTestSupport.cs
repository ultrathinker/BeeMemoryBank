using System.Security.Cryptography;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Services.BlindPhone;

namespace BeeMemoryBank.BlindMobile.Tests;

/// <summary>
/// A software stand-in for the AndroidKeyStore: one random AES-256 key per alias (made on first use, like <c>GetOrCreateKey</c>), AES-GCM with the associated data, the blob laid out as
/// <see cref="BlindKeystoreBlobLayout"/> says (IV, ciphertext, tag). The "Keystore" itself is an object the test hands to every factory it makes, so it survives a "restart". It records
/// what the store asked of it - aliases, associated data, deletions - which is what the naming tests read.
/// </summary>
internal sealed class SoftwareKeyWrap : IBlindKeyWrap
{
    private readonly object _gate = new();

    public Dictionary<string, byte[]> Keys { get; } = new(StringComparer.Ordinal);
    public List<(string Alias, string Aad)> Wraps { get; } = [];
    public List<string> Deleted { get; } = [];

    /// <summary>When set, one key serves every alias (so that only the associated data can tell two accounts' blobs apart).</summary>
    public bool OneKeyForAllAliases { get; init; }

    public byte[] Wrap(string alias, byte[] aad, byte[] secret)
    {
        lock (_gate)
        {
            Wraps.Add((alias, System.Text.Encoding.UTF8.GetString(aad)));
            var iv = RandomNumberGenerator.GetBytes(BlindKeystoreBlobLayout.IvLength);
            var cipher = new byte[secret.Length];
            var tag = new byte[BlindKeystoreBlobLayout.TagLength];
            using var gcm = new AesGcm(KeyOf(alias), tag.Length);
            gcm.Encrypt(iv, secret, cipher, tag, aad);
            return BlindKeystoreBlobLayout.Join(iv, [.. cipher, .. tag]);
        }
    }

    public byte[] Unwrap(string alias, byte[] aad, byte[] blob)
    {
        lock (_gate)
        {
            var (iv, offset, length) = BlindKeystoreBlobLayout.Split(blob);
            var body = blob.AsSpan(offset, length - BlindKeystoreBlobLayout.TagLength);
            var tag = blob.AsSpan(offset + length - BlindKeystoreBlobLayout.TagLength, BlindKeystoreBlobLayout.TagLength);
            var secret = new byte[body.Length];
            using var gcm = new AesGcm(KeyOf(alias), tag.Length);
            gcm.Decrypt(iv, body, tag, secret, aad);
            return secret;
        }
    }

    public void DeleteKey(string alias)
    {
        lock (_gate)
        {
            Deleted.Add(alias);
            Keys.Remove(alias);
        }
    }

    private byte[] KeyOf(string alias)
    {
        var name = OneKeyForAllAliases ? "*" : alias;
        if (!Keys.TryGetValue(name, out var key)) Keys[name] = key = RandomNumberGenerator.GetBytes(32);
        return key;
    }
}

/// <summary>
/// The SharedPreferences files of the app as a dictionary of dictionaries: what a phone keeps between two runs, shared by every store a test makes. It answers through <see cref="IBlindPreferencesFile"/> like the
/// phone does, so a test can make the platform say "not written" (<see cref="CommitSucceeds"/>, <see cref="DeleteSucceeds"/>): then nothing is changed, as on a phone whose commit failed.
/// </summary>
internal sealed class FakePreferences
{
    private readonly object _gate = new();

    public Dictionary<string, Dictionary<string, string>> Files { get; } = new(StringComparer.Ordinal);

    /// <summary>False: <c>commit()</c> returns false (and writes nothing) for every put, remove and clear.</summary>
    public volatile bool CommitSucceeds = true;

    /// <summary>False: <c>deleteSharedPreferences</c> returns false (and the file stays).</summary>
    public volatile bool DeleteSucceeds = true;

    /// <summary>True: clearing the keys THROWS (a platform exception with a path and a class name in its text), the way a failing disk or a dead preferences service does.</summary>
    public volatile bool ClearThrows;

    /// <summary>How many times the file was asked to be deleted: a test sees that an erase went on to the delete although the clear threw.</summary>
    public int DeleteCalls;

    public PreferencesBlindStore Open(string name) => new(new File(this, name));

    public PreferencesBlindStateStoreFactory Factory() => new(Open);

    private sealed class File(FakePreferences owner, string name) : IBlindPreferencesFile
    {
        public string? GetString(string key)
        {
            lock (owner._gate) return owner.Files.TryGetValue(name, out var file) && file.TryGetValue(key, out var value) ? value : null;
        }

        public bool PutString(string key, string value)
        {
            lock (owner._gate)
            {
                if (!owner.CommitSucceeds) return false;
                if (!owner.Files.TryGetValue(name, out var file)) owner.Files[name] = file = [];
                file[key] = value;
                return true;
            }
        }

        public bool RemoveKey(string key)
        {
            lock (owner._gate)
            {
                if (!owner.CommitSucceeds) return false;
                if (owner.Files.TryGetValue(name, out var file)) file.Remove(key);
                return true;
            }
        }

        public bool Clear()
        {
            lock (owner._gate)
            {
                if (owner.ClearThrows) throw new InvalidOperationException("android.content.SharedPreferences: cannot write /data/user/0/com.beememorybank.blind/shared_prefs/bmb_blind_state.xml");
                if (!owner.CommitSucceeds) return false;
                if (owner.Files.TryGetValue(name, out var file)) file.Clear();
                return true;
            }
        }

        public bool Delete()
        {
            lock (owner._gate)
            {
                owner.DeleteCalls++;
                if (!owner.DeleteSucceeds) return false;
                owner.Files.Remove(name);
                return true;
            }
        }
    }
}

/// <summary>
/// A <see cref="SoftwareKeyWrap"/> whose <see cref="IBlindKeyWrap.DeleteKey"/> can be made to fail the way a Keystore that is locked or not answering does: with the platform's own words (an alias in them),
/// and the entry is NOT deleted. What it was asked is counted, so a test can see that every role was tried.
/// </summary>
internal sealed class FlakyKeyWrap : IBlindKeyWrap
{
    public SoftwareKeyWrap Inner { get; } = new();

    public volatile bool FailDelete;

    public int DeleteAttempts;

    public byte[] Wrap(string alias, byte[] aad, byte[] secret) => Inner.Wrap(alias, aad, secret);

    public byte[] Unwrap(string alias, byte[] aad, byte[] blob) => Inner.Unwrap(alias, aad, blob);

    public void DeleteKey(string alias)
    {
        Interlocked.Increment(ref DeleteAttempts);
        if (FailDelete) throw new InvalidOperationException($"android.security.KeyStoreException: could not delete {alias} (the keystore is locked)");
        Inner.DeleteKey(alias);
    }
}

/// <summary>A temporary app root that is deleted with the test (the test's own files only).</summary>
internal sealed class TempRoot : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bmb-blind-head-tests", Guid.NewGuid().ToString("N"));

    public TempRoot() => Directory.CreateDirectory(Path);

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Path, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the temp folder is cleaned by the system sooner or later */ }
    }
}
