using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>
/// Gives each account its own <see cref="IBlindSecretStore"/>. The platform implementation (Keystore aliases, DPAPI entropy, Keychain accounts) belongs to
/// the head; the names it must use are <see cref="BlindAccountNames"/>. The CONTRACT, which every head's tests run (<c>BlindSecretStoreContract</c> in
/// tests/shared): the stores of two accounts never see each other's secrets, clearing one leaves the other, what was saved is there after a restart, and a
/// stored blob that is copied into another account's slot does not open there. The legacy account's store is today's store, under today's names.
/// </summary>
public interface IBlindSecretStoreFactory
{
    /// <summary>The secrets of <paramref name="account"/>. May return a new adapter on every call: they hold no state of their own, the platform does.</summary>
    IBlindSecretStore For(BlindAccountEntry account);
}

/// <summary>
/// Gives each account its own <see cref="IBlindStateStore"/> (the non-secret key/value state: call code, times, schedule). The CONTRACT (<c>BlindStateStoreContract</c>):
/// two accounts never share a key, <c>Erase</c> removes this account's state and nothing else, and what was set is there after a restart.
/// </summary>
public interface IBlindStateStoreFactory
{
    IBlindStateStore For(BlindAccountEntry account);
}

/// <summary>What the contract tests need of a secret store factory that keeps blobs: to move one raw blob from an account's slot to another's.</summary>
public interface IBlindSecretBlobAccess
{
    /// <summary>The raw persisted blob of the role (as the platform stores it), or null when nothing is stored.</summary>
    byte[]? ReadBlob(BlindAccountEntry account, BlindAccountNames.Role role);

    /// <summary>Puts a raw blob into the account's slot, as copying the file or the item would.</summary>
    void WriteBlob(BlindAccountEntry account, BlindAccountNames.Role role, byte[] blob);
}

/// <summary>
/// A secret store factory that keeps everything in memory: the reference for the contract and what the tests of the account host use. Each stored blob is
/// <c>version(1) | SHA-256(domain | slot | 0 | secret)(32) | secret</c>, the envelope the iOS and macOS stores use, where the slot is the Keychain account name
/// of the account and role: a blob moved to another account's slot fails the digest and is refused, never used. NOT a secret store for a real device: the
/// secret is not protected from the process's own memory.
/// </summary>
public sealed class InMemoryBlindSecretStoreFactory : IBlindSecretStoreFactory, IBlindSecretBlobAccess
{
    private const byte EnvelopeVersion = 1;
    private const int DigestLength = 32;
    private static readonly byte[] Domain = "bmb-blind-memory-secret-v1\0"u8.ToArray();

    private readonly object _gate = new();
    private readonly Dictionary<string, byte[]> _blobs = new(StringComparer.Ordinal);

    public IBlindSecretStore For(BlindAccountEntry account) => new Store(this, account);

    public byte[]? ReadBlob(BlindAccountEntry account, BlindAccountNames.Role role)
    {
        lock (_gate) return _blobs.TryGetValue(Slot(account, role), out var blob) ? [.. blob] : null;
    }

    public void WriteBlob(BlindAccountEntry account, BlindAccountNames.Role role, byte[] blob)
    {
        lock (_gate) _blobs[Slot(account, role)] = [.. blob];
    }

    /// <summary>How many blobs are stored under any account (a test's check that a removal left none behind for its account).</summary>
    public int BlobCount(BlindAccountEntry account)
    {
        lock (_gate)
            return Enum.GetValues<BlindAccountNames.Role>().Count(role => _blobs.ContainsKey(Slot(account, role)));
    }

    private static string Slot(BlindAccountEntry account, BlindAccountNames.Role role) => BlindAccountNames.KeychainAccount(account, role);

    private static byte[] Wrap(string slot, byte[] secret)
    {
        var blob = new byte[1 + DigestLength + secret.Length];
        blob[0] = EnvelopeVersion;
        Digest(slot, secret).CopyTo(blob, 1);
        secret.CopyTo(blob, 1 + DigestLength);
        return blob;
    }

    private static byte[] Unwrap(string slot, byte[] blob)
    {
        if (blob.Length < 1 + DigestLength || blob[0] != EnvelopeVersion)
            throw new CryptographicException("The stored secret is damaged.");
        var secret = blob[(1 + DigestLength)..];
        if (!CryptographicOperations.FixedTimeEquals(blob.AsSpan(1, DigestLength), Digest(slot, secret)))
        {
            CryptographicOperations.ZeroMemory(secret);
            throw new CryptographicException("The stored secret does not belong to this account.");
        }
        return secret;
    }

    private static byte[] Digest(string slot, byte[] secret)
    {
        var slotBytes = Encoding.UTF8.GetBytes(slot);
        var input = new byte[Domain.Length + slotBytes.Length + 1 + secret.Length];
        Domain.CopyTo(input, 0);
        slotBytes.CopyTo(input, Domain.Length);
        // input[Domain.Length + slotBytes.Length] stays 0: the separator between the slot and the secret.
        secret.CopyTo(input, Domain.Length + slotBytes.Length + 1);
        try { return SHA256.HashData(input); }
        finally { CryptographicOperations.ZeroMemory(input); }
    }

    private sealed class Store(InMemoryBlindSecretStoreFactory owner, BlindAccountEntry account) : IBlindSecretStore
    {
        public void SaveIdentitySeed(byte[] seed) => Save(BlindAccountNames.Role.IdentitySeed, seed);
        public byte[]? LoadIdentitySeed() => Load(BlindAccountNames.Role.IdentitySeed);
        public void SaveBackupKey(byte[] key) => Save(BlindAccountNames.Role.BackupKey, key);
        public byte[]? LoadBackupKey() => Load(BlindAccountNames.Role.BackupKey);
        public void SavePairingSecret(byte[] secret) => Save(BlindAccountNames.Role.PairingSecret, secret);
        public byte[]? LoadPairingSecret() => Load(BlindAccountNames.Role.PairingSecret);
        public void ClearPairingSecret() => Remove(BlindAccountNames.Role.PairingSecret);

        public void Clear()
        {
            foreach (var role in Enum.GetValues<BlindAccountNames.Role>()) Remove(role);
        }

        private void Save(BlindAccountNames.Role role, byte[] secret)
        {
            var slot = Slot(account, role);
            var blob = Wrap(slot, secret);
            lock (owner._gate) owner._blobs[slot] = blob;
        }

        private byte[]? Load(BlindAccountNames.Role role)
        {
            var slot = Slot(account, role);
            byte[]? blob;
            lock (owner._gate) blob = owner._blobs.GetValueOrDefault(slot);
            return blob is null ? null : Unwrap(slot, blob);
        }

        private void Remove(BlindAccountNames.Role role)
        {
            lock (owner._gate) owner._blobs.Remove(Slot(account, role));
        }
    }
}

/// <summary>A state store factory that keeps each account's key/value pairs in memory.</summary>
public sealed class InMemoryBlindStateStoreFactory : IBlindStateStoreFactory
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Dictionary<string, string?>> _accounts = new(StringComparer.Ordinal);

    public IBlindStateStore For(BlindAccountEntry account) => new Store(this, account.Id);

    /// <summary>A copy of every key and value the account has stored (a test's check that nothing of another account is in it).</summary>
    public IReadOnlyDictionary<string, string?> Snapshot(BlindAccountEntry account)
    {
        lock (_gate)
            return _accounts.TryGetValue(account.Id, out var values) ? new Dictionary<string, string?>(values) : new Dictionary<string, string?>();
    }

    /// <summary>True while the account has any value stored (a test's check that a removal left no state behind).</summary>
    public bool HasState(BlindAccountEntry account)
    {
        lock (_gate) return _accounts.TryGetValue(account.Id, out var values) && values.Values.Any(v => v is not null);
    }

    private sealed class Store(InMemoryBlindStateStoreFactory owner, string id) : IBlindStateStore
    {
        public string? Get(string key)
        {
            lock (owner._gate) return owner._accounts.TryGetValue(id, out var values) ? values.GetValueOrDefault(key) : null;
        }

        public void Set(string key, string? value)
        {
            lock (owner._gate)
            {
                if (!owner._accounts.TryGetValue(id, out var values)) owner._accounts[id] = values = new Dictionary<string, string?>(StringComparer.Ordinal);
                if (value is null) values.Remove(key);
                else values[key] = value;
            }
        }

        public void Erase()
        {
            lock (owner._gate) owner._accounts.Remove(id);
        }
    }
}

/// <summary>
/// A state store factory that keeps each account's state in <c>state.json</c> inside the account's folder (<see cref="BlindAccountNames.DataDirectory"/>): a JSON
/// object of strings, written whole to a temporary file and renamed into place, so a crash leaves the old file or the new one. Nothing in it is secret.
/// For the desktop and Apple heads, which already keep their state in a file of that name; the legacy account's file is the one it always was.
/// </summary>
public sealed class FileBlindStateStoreFactory(string root) : IBlindStateStoreFactory
{
    // One lock per state FILE, shared by every store object made for it (the host asks for an account's store more than once in its life: the runtime's, the removal's, the
    // adoption's): two objects over one file would otherwise replace it under each other's reads. Between processes the root lock of the host decides.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> FileLocks =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public IBlindStateStore For(BlindAccountEntry account) =>
        new Store(Path.Combine(BlindAccountNames.DataDirectory(root, account), BlindAccountNames.StateFile));

    private sealed class Store(string path) : IBlindStateStore
    {
        private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
        private readonly object _gate = FileLocks.GetOrAdd(Path.GetFullPath(path), _ => new object());

        public string? Get(string key)
        {
            lock (_gate) return Read().GetValueOrDefault(key);
        }

        public void Set(string key, string? value)
        {
            lock (_gate)
            {
                var values = Read();
                if (value is null) values.Remove(key);
                else values[key] = value;
                Write(values);
            }
        }

        public void Erase()
        {
            lock (_gate)
            {
                if (File.Exists(path)) File.Delete(path);
                var folder = Path.GetDirectoryName(path)!;
                if (!Directory.Exists(folder)) return;
                // What a write that died between its two steps left (a name of its own each time, and the one the first version used).
                var name = Path.GetFileName(path);
                foreach (var pattern in new[] { name + ".tmp", name + ".*.tmp" })
                    foreach (var leftover in Directory.EnumerateFiles(folder, pattern)) File.Delete(leftover);
            }
        }

        private Dictionary<string, string> Read()
        {
            if (!File.Exists(path)) return new Dictionary<string, string>(StringComparer.Ordinal);
            // A damaged file is an error with a sentence, not an empty state: an empty one would read as "not paired" and invite a second pairing over a live copy.
            try { return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllBytes(path)) ?? throw new InvalidDataException("empty"); }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                throw new InvalidDataException("The state file of this account is damaged.", ex);
            }
        }

        private void Write(Dictionary<string, string> values)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // A name of its own each time: two writers (two processes, if the root lock were ever bypassed) never share a temporary file, and a leftover never gets in the way.
            var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    stream.Write(JsonSerializer.SerializeToUtf8Bytes(values, Json));
                File.Move(tmp, path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                try { File.Delete(tmp); } catch (Exception inner) when (inner is IOException or UnauthorizedAccessException) { }
                throw;
            }
        }
    }
}
