using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace BeeMemoryBank.Crypto;

/// <summary>The open part of an Android blind node's backup file.</summary>
/// <param name="NodeId">The phone's blind NodeId.</param>
/// <param name="BackupKeyName">The sealed secret that holds the backup key (<c>android-backup:&lt;node id&gt;</c>).</param>
/// <param name="RecoverySet">The recovery set (<c>bmb-recovery-set-v1</c>) as JSON — what a restore opens first, with the master password.</param>
public sealed record AndroidBackupHeader(
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("node_id")] Guid NodeId,
    [property: JsonPropertyName("backup_key_name")] string BackupKeyName,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("chunk_size")] int ChunkSize,
    [property: JsonPropertyName("nonce_prefix")] string NoncePrefix,
    [property: JsonPropertyName("recovery_set")] JsonObject RecoverySet);

/// <summary>
/// The backup file of an Android blind node (plan section 10): an OPEN header — the recovery set,
/// so Windows can open it with the master password (plan 6.8) — followed by the blind package
/// encrypted with the phone's backup key. Unencrypted, a backup saved to the cloud through "Save to…"
/// would carry the metadata of the whole network.
///
/// <para>Layout: <c>"BMBABK01"</c>, header length (uint32 BE), header JSON, then chunks, each
/// <c>length (uint32 BE) || AES-256-GCM(ciphertext || tag)</c>. Chunk i uses the nonce
/// <c>prefix(7) || i (uint32 BE) || final(1)</c> and, as associated data, SHA-256 of everything
/// before the first chunk. So a changed header, a reordered, dropped or repeated chunk, or a file cut
/// short (no chunk marked final) all fail to open — never decrypt to something else.</para>
///
/// <para>Chunks make the writer resumable (<see cref="AndroidBackupWriter"/>): a backup interrupted
/// by a lost charger or Wi-Fi continues from its last whole chunk.</para>
/// </summary>
public static class AndroidBackupFile
{
    public const string FormatV1 = "bmb-android-backup-v1";
    public const int DefaultChunkSize = 1024 * 1024;

    internal static readonly byte[] Magic = "BMBABK01"u8.ToArray();

    /// <summary>True if <paramref name="firstBytes"/> is the file's 8-byte magic.</summary>
    public static bool IsMagic(ReadOnlySpan<byte> firstBytes) => firstBytes.SequenceEqual(Magic);
    internal const int NoncePrefixSize = 7;
    internal const int TagSize = 16;
    private const int MaxHeaderBytes = 32 * 1024 * 1024;
    private const int MaxChunkSize = 16 * 1024 * 1024;

    internal static readonly JsonSerializerOptions Json = new();

    /// <summary>Reads the open header — no key needed. Throws <see cref="InvalidDataException"/> on anything malformed.</summary>
    public static async Task<AndroidBackupHeader> ReadHeaderAsync(Stream file, CancellationToken ct = default) =>
        (await ReadPreambleAsync(file, ct)).Header;

    /// <summary>
    /// The open header and the SHA-256 of everything before the first chunk — what every chunk is bound
    /// to. Pass the digest to <see cref="DecryptAsync"/> so it opens exactly the header that was checked.
    /// </summary>
    public static async Task<(AndroidBackupHeader Header, byte[] PreambleSha256)> ReadHeaderWithDigestAsync(
        Stream file, CancellationToken ct = default) =>
        await ReadPreambleAsync(file, ct);

    /// <summary>
    /// Decrypts the body of <paramref name="file"/> into <paramref name="output"/>. Throws
    /// <see cref="InvalidDataException"/> for a wrong key or any tampering or truncation; what was
    /// written to <paramref name="output"/> before that point must then be discarded.
    /// </summary>
    /// <param name="expectedPreambleSha256">
    /// When given, the file must still start with exactly that preamble (see <see cref="ReadHeaderWithDigestAsync"/>);
    /// otherwise <see cref="InvalidDataException"/> — the file changed after its header was checked.
    /// </param>
    /// <param name="maxPlaintextBytes">When given, a body that decrypts to more than this is refused as it streams —
    /// before more than that has been written.</param>
    public static async Task DecryptAsync(Stream file, byte[] backupKey, Stream output, CancellationToken ct = default,
        byte[]? expectedPreambleSha256 = null, long? maxPlaintextBytes = null)
    {
        var (header, aad) = await ReadPreambleAsync(file, ct);
        if (expectedPreambleSha256 != null && !CryptographicOperations.FixedTimeEquals(aad, expectedPreambleSha256))
            throw new InvalidDataException("The backup changed after its header was checked.");
        var prefix = Convert.FromBase64String(header.NoncePrefix);
        using var aes = new AesGcm(backupKey, TagSize);
        var lengthBytes = new byte[4];
        long written = 0;

        for (uint index = 0; ; index++)
        {
            if (await file.ReadAtLeastAsync(lengthBytes, 4, throwOnEndOfStream: false, ct) < 4)
                throw new InvalidDataException("The backup is cut short: its last part is missing.");
            var length = BinaryPrimitives.ReadUInt32BigEndian(lengthBytes);
            if (length < TagSize || length > header.ChunkSize + TagSize)
                throw new InvalidDataException("The backup has a damaged part.");

            var sealedChunk = new byte[length];
            try { await file.ReadExactlyAsync(sealedChunk, ct); }
            catch (EndOfStreamException ex) { throw new InvalidDataException("The backup is cut short inside a part.", ex); }
            var plain = new byte[length - TagSize];
            written += plain.Length;
            if (written > maxPlaintextBytes)
                throw new InvalidDataException("The backup's body is larger than a restore takes.") { Data = { [TooLargeMark] = true } };

            // Try as a middle chunk first, then as the final one: the flag is inside the nonce, so
            // only the writer's own choice authenticates.
            var isFinal = !TryOpen(aes, Nonce(prefix, index, false), sealedChunk, plain, aad);
            if (isFinal && !TryOpen(aes, Nonce(prefix, index, true), sealedChunk, plain, aad))
                throw new InvalidDataException("The backup does not open with this key, or it was changed.");

            await output.WriteAsync(plain, ct);
            if (isFinal)
            {
                if (file.CanSeek ? file.Position != file.Length : await file.ReadAsync(new byte[1], ct) != 0)
                    throw new InvalidDataException("The backup has data after its last part.");
                return;
            }
        }
    }

    private const string TooLargeMark = "bmb.android-backup.too-large";

    /// <summary>
    /// True for the refusal of a body larger than the caller takes. Only a right key gets that far (every chunk
    /// before it authenticated), so unlike every other <see cref="InvalidDataException"/> here it is no reason
    /// to try another key.
    /// </summary>
    public static bool IsBodyTooLarge(InvalidDataException ex) => ex.Data.Contains(TooLargeMark);

    /// <summary>SHA-256 of the decrypted body — to check a backup against its source without keeping the plaintext.</summary>
    public static async Task<byte[]> DecryptedSha256Async(Stream file, byte[] backupKey, CancellationToken ct = default)
    {
        using var sha = SHA256.Create();
        await using (var hashing = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write, leaveOpen: true))
            await DecryptAsync(file, backupKey, hashing, ct);
        return sha.Hash!;
    }

    internal static byte[] Nonce(byte[] prefix, uint index, bool final)
    {
        var nonce = new byte[12];
        prefix.CopyTo(nonce, 0);
        BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(NoncePrefixSize), index);
        nonce[11] = final ? (byte)1 : (byte)0;
        return nonce;
    }

    internal static byte[] Preamble(AndroidBackupHeader header)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(header, Json);
        var preamble = new byte[Magic.Length + 4 + json.Length];
        Magic.CopyTo(preamble, 0);
        BinaryPrimitives.WriteUInt32BigEndian(preamble.AsSpan(Magic.Length), (uint)json.Length);
        json.CopyTo(preamble, Magic.Length + 4);
        return preamble;
    }

    private static bool TryOpen(AesGcm aes, byte[] nonce, byte[] sealedChunk, byte[] plain, byte[] aad)
    {
        try
        {
            aes.Decrypt(nonce, sealedChunk.AsSpan(0, plain.Length), sealedChunk.AsSpan(plain.Length), plain, aad);
            return true;
        }
        catch (AuthenticationTagMismatchException) { return false; }
    }

    internal static async Task<(AndroidBackupHeader Header, byte[] Aad)> ReadPreambleAsync(Stream file, CancellationToken ct)
    {
        var fixedPart = new byte[Magic.Length + 4];
        if (await file.ReadAtLeastAsync(fixedPart, fixedPart.Length, throwOnEndOfStream: false, ct) < fixedPart.Length
            || !fixedPart.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException("This is not a BeeMemoryBank phone backup.");

        var length = BinaryPrimitives.ReadUInt32BigEndian(fixedPart.AsSpan(Magic.Length));
        if (length == 0 || length > MaxHeaderBytes) throw new InvalidDataException("The backup header is damaged.");
        var json = new byte[length];
        try { await file.ReadExactlyAsync(json, ct); }
        catch (EndOfStreamException ex) { throw new InvalidDataException("The backup is cut short inside its header.", ex); }

        AndroidBackupHeader? header;
        try { header = JsonSerializer.Deserialize<AndroidBackupHeader>(json, Json); }
        catch (JsonException ex) { throw new InvalidDataException("The backup header is damaged.", ex); }
        if (header is null || header.Format != FormatV1 || header.RecoverySet is null
            || header.ChunkSize <= 0 || header.ChunkSize > MaxChunkSize
            || !TryBase64(header.NoncePrefix, out var prefix) || prefix.Length != NoncePrefixSize)
            throw new InvalidDataException("The backup header is damaged or of an unknown version.");

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(fixedPart);
        sha.AppendData(json);
        return (header, sha.GetHashAndReset());
    }

    private static bool TryBase64(string? s, out byte[] bytes)
    {
        bytes = [];
        if (s is null) return false;
        try { bytes = Convert.FromBase64String(s); return true; }
        catch (FormatException) { return false; }
    }
}

/// <summary>
/// Writes an <see cref="AndroidBackupFile"/> from a source file (the phone's copy of its blind
/// package), resumably: the file grows as <c>&lt;output&gt;.part</c> with a small state file next to it,
/// and <see cref="WriteAsync"/> on the same output continues where an interrupted run stopped —
/// after its last whole chunk — provided the source is byte-for-byte the same one.
///
/// <para>Why the source is hashed rather than named by the caller: a resumed chunk is encrypted
/// again under the SAME nonce. With the same plaintext that yields the same bytes; with different
/// plaintext it would break AES-GCM outright. So only a source with the recorded SHA-256 continues a
/// file; anything else starts a new one with a fresh nonce prefix.</para>
/// </summary>
public static class AndroidBackupWriter
{
    // Only bookkeeping lives here. What the resumed file IS — node, key name, recovery set, chunk size,
    // nonce prefix — is read back from the partial file's own header, never taken from this state.
    private sealed record State(
        [property: JsonPropertyName("source_sha256")] string SourceSha256,
        [property: JsonPropertyName("source_length")] long SourceLength,
        [property: JsonPropertyName("preamble_sha256")] string PreambleSha256,
        [property: JsonPropertyName("preamble_length")] int PreambleLength,
        [property: JsonPropertyName("chunks_done")] uint ChunksDone,
        [property: JsonPropertyName("bytes_done")] long BytesDone);

    public static string PartPath(string outputPath) => outputPath + ".part";
    public static string StatePath(string outputPath) => outputPath + ".part.json";

    /// <summary>
    /// Writes (or resumes) the backup of <paramref name="sourcePath"/> to <paramref name="outputPath"/>.
    /// Cancelling leaves the partial file to resume from. <paramref name="progress"/> gets the fraction done.
    ///
    /// <para>A resume is trusted only if everything checks against the partial file itself: its header
    /// (parsed from the file) is exactly the one these inputs would write — node, key name, recovery set,
    /// chunk size — the source is byte-for-byte the one it started from, and every chunk already written
    /// decrypts under THIS key with its own nonce and that header as associated data. Anything else — a
    /// flipped bit, a stale or edited state file, a changed recovery set — starts a new file with a fresh
    /// nonce prefix.</para>
    ///
    /// <para>Before the file replaces <paramref name="outputPath"/> it is decrypted once more and matched
    /// with the source as it was read, and the live source is hashed again: a source edited while it was
    /// being backed up — even in a part already read — throws <see cref="InvalidDataException"/>.</para>
    /// </summary>
    public static async Task WriteAsync(
        string sourcePath, string outputPath, byte[] backupKey,
        Guid nodeId, string backupKeyName, string recoverySetJson,
        int chunkSize = AndroidBackupFile.DefaultChunkSize,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var recoverySet = JsonNode.Parse(recoverySetJson) as JsonObject
            ?? throw new ArgumentException("The recovery set must be a JSON object.", nameof(recoverySetJson));
        var (sourceSha, sourceLength) = await HashAsync(sourcePath, ct);

        var resumed = Resumable(outputPath, sourceSha, sourceLength, nodeId, backupKeyName, recoverySet, chunkSize, backupKey);
        State state;
        AndroidBackupHeader header;
        if (resumed is { } r)
        {
            (state, header) = r;
        }
        else
        {
            header = new AndroidBackupHeader(AndroidBackupFile.FormatV1, nodeId, backupKeyName, DateTimeOffset.UtcNow,
                chunkSize, Convert.ToBase64String(RandomNumberGenerator.GetBytes(AndroidBackupFile.NoncePrefixSize)), recoverySet);
            var preamble = AndroidBackupFile.Preamble(header);
            await File.WriteAllBytesAsync(PartPath(outputPath), preamble, ct);
            state = new State(sourceSha, sourceLength, Convert.ToHexString(SHA256.HashData(preamble)), preamble.Length, 0, preamble.Length);
            SaveState(outputPath, state);
        }

        var aad = Convert.FromHexString(state.PreambleSha256);
        var prefix = Convert.FromBase64String(header.NoncePrefix);
        var chunkCount = ChunkCount(sourceLength, header.ChunkSize);

        // Shared for writing on purpose: sharing is only advisory on Android, so the guarantee against a
        // source that changes mid-backup is the check at the end, the same on every platform.
        await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        await using (var output = new FileStream(PartPath(outputPath), FileMode.Open, FileAccess.Write))
        {
            // Anything past the last recorded chunk is a half-written one from an interrupted run.
            output.SetLength(state.BytesDone);
            output.Position = state.BytesDone;
            source.Position = (long)state.ChunksDone * header.ChunkSize;

            using var aes = new AesGcm(backupKey, AndroidBackupFile.TagSize);
            var plain = new byte[header.ChunkSize];
            var lengthBytes = new byte[4];
            for (var index = state.ChunksDone; index < chunkCount; index++)
            {
                ct.ThrowIfCancellationRequested();
                var read = await source.ReadAtLeastAsync(plain, header.ChunkSize, throwOnEndOfStream: false, ct);
                var sealedChunk = new byte[read + AndroidBackupFile.TagSize];
                aes.Encrypt(AndroidBackupFile.Nonce(prefix, index, index == chunkCount - 1), plain.AsSpan(0, read),
                    sealedChunk.AsSpan(0, read), sealedChunk.AsSpan(read), aad);

                BinaryPrimitives.WriteUInt32BigEndian(lengthBytes, (uint)sealedChunk.Length);
                await output.WriteAsync(lengthBytes, ct);
                await output.WriteAsync(sealedChunk, ct);
                await output.FlushAsync(ct);

                state = state with { ChunksDone = index + 1, BytesDone = output.Position };
                SaveState(outputPath, state);
                progress?.Report((double)(index + 1) / chunkCount);
            }
        }

        // The whole file, once more, and the source as it is NOW, before this counts as a backup.
        string written;
        await using (var finished = File.OpenRead(PartPath(outputPath)))
        {
            try { written = Convert.ToHexString(await AndroidBackupFile.DecryptedSha256Async(finished, backupKey, ct)); }
            catch (InvalidDataException) { written = ""; }
        }
        var (liveSha, liveLength) = await HashAsync(sourcePath, ct);
        if (written != sourceSha || liveSha != sourceSha || liveLength != sourceLength)
        {
            File.Delete(PartPath(outputPath));
            File.Delete(StatePath(outputPath));
            throw new InvalidDataException("The source changed while it was backed up, or the written backup does not match it; it was discarded.");
        }

        File.Move(PartPath(outputPath), outputPath, overwrite: true);
        File.Delete(StatePath(outputPath));
    }

    private static uint ChunkCount(long sourceLength, int chunkSize) =>
        (uint)Math.Max(1, (sourceLength + chunkSize - 1) / chunkSize);

    private static async Task<(string Sha, long Length)> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var sha = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
        return (sha, stream.Length);
    }

    private static (State, AndroidBackupHeader)? Resumable(string outputPath, string sourceSha, long sourceLength,
        Guid nodeId, string backupKeyName, JsonObject recoverySet, int chunkSize, byte[] key)
    {
        try
        {
            if (!File.Exists(StatePath(outputPath)) || !File.Exists(PartPath(outputPath))) return null;
            var state = JsonSerializer.Deserialize<State>(File.ReadAllBytes(StatePath(outputPath)), AndroidBackupFile.Json);
            if (state == null || state.SourceSha256 != sourceSha || state.SourceLength != sourceLength) return null;

            using var part = File.OpenRead(PartPath(outputPath));
            // The header is read from the partial file itself, and it must be the one these inputs write.
            AndroidBackupHeader header;
            byte[] aad;
            try { (header, aad) = AndroidBackupFile.ReadPreambleAsync(part, CancellationToken.None).GetAwaiter().GetResult(); }
            catch (InvalidDataException) { return null; }
            if (part.Position != state.PreambleLength || Convert.ToHexString(aad) != state.PreambleSha256) return null;
            if (header.NodeId != nodeId || !string.Equals(header.BackupKeyName, backupKeyName, StringComparison.Ordinal)
                || header.ChunkSize != chunkSize || !JsonNode.DeepEquals(header.RecoverySet, recoverySet))
                return null;

            var chunkCount = ChunkCount(sourceLength, header.ChunkSize);
            if (state.ChunksDone > chunkCount) return null;

            // Every chunk it claims must be there, in place, and open under this key.
            var prefix = Convert.FromBase64String(header.NoncePrefix);
            using var aes = new AesGcm(key, AndroidBackupFile.TagSize);
            var lengthBytes = new byte[4];
            for (uint index = 0; index < state.ChunksDone; index++)
            {
                var final = index == chunkCount - 1;
                var plainLength = final ? sourceLength - (long)index * header.ChunkSize : header.ChunkSize;
                part.ReadExactly(lengthBytes);
                if (BinaryPrimitives.ReadUInt32BigEndian(lengthBytes) != plainLength + AndroidBackupFile.TagSize) return null;
                var sealedChunk = new byte[plainLength + AndroidBackupFile.TagSize];
                part.ReadExactly(sealedChunk);
                var plain = new byte[plainLength];
                aes.Decrypt(AndroidBackupFile.Nonce(prefix, index, final), sealedChunk.AsSpan(0, (int)plainLength),
                    sealedChunk.AsSpan((int)plainLength), plain, aad);
            }
            return part.Position == state.BytesDone ? (state, header) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or FormatException or CryptographicException)
        {
            return null;
        }
    }

    private static void SaveState(string outputPath, State state)
    {
        // Write-then-rename: a kill mid-write must not leave a state file that points past the data.
        var tmp = StatePath(outputPath) + ".tmp";
        File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(state, AndroidBackupFile.Json));
        File.Move(tmp, StatePath(outputPath), overwrite: true);
    }
}
