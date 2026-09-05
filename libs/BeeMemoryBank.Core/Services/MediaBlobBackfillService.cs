using System.Data;
using BeeMemoryBank.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeeMemoryBank.Core.Services;

/// <summary>
/// Result of one backfill pass. <paramref name="Stored"/> media whose bytes were moved into the
/// blob store this pass; <paramref name="AlreadyDone"/> rows already carrying a hash (skipped);
/// <paramref name="MissingFile"/> rows with neither a hash nor a readable <c>.enc</c> file (their
/// bytes live only on a peer — nothing to backfill locally).
/// </summary>
public readonly record struct MediaBlobBackfillResult(int Stored, int AlreadyDone, int MissingFile);

/// <summary>
/// Item 16b, phase 1: move every media ciphertext that still lives ONLY as an <c>.enc</c> file on
/// disk into the content-addressed blob store, and stamp its hash onto the row.
///
/// <para>Why a disk pass and not a SQL migration: migration 023 backfills the hash from surviving
/// <c>media_create</c> events, but those events are compacted away on a long-lived node, so on a
/// real deployment it backfills nothing and every legacy media row is left with a null hash and no
/// blob — its bytes exist only in the <c>.enc</c> file. This pass reads that file and is the only
/// thing that makes the blob store the complete home for media, which every later step of 16b
/// (stop writing <c>.enc</c>, then delete the files) depends on.</para>
///
/// <para>Safety: purely additive and idempotent. It reads ciphertext and writes it to the blob
/// store under its own SHA-256 (<see cref="IBlobRepository.StoreAsync"/> is idempotent) and sets a
/// column that was null — it never deletes a file, never decrypts (the <c>.enc</c> IS the
/// ciphertext, so it runs on a locked vault), and re-running finds nothing left to do. A crash
/// between storing the blob and stamping the row is harmless: the blob is content-addressed and the
/// row is re-picked next pass.</para>
///
/// <para>GUID-case trap: <c>tbl_media.id</c> is stored upper-case, but the <c>.enc</c> file is named
/// from <c>Guid.ToString()</c> (lower-case), and on a case-sensitive filesystem those differ. The
/// file path is therefore built from the canonical lower-case form, while the UPDATE matches the
/// exact stored id string so it always hits the right row.</para>
/// </summary>
public class MediaBlobBackfillService(
    IDbConnectionFactory connFactory,
    IBlobRepository blobRepo,
    MediaStorageOptions options,
    ILogger<MediaBlobBackfillService>? logger = null)
{
    private readonly ILogger<MediaBlobBackfillService> logger = logger ?? NullLogger<MediaBlobBackfillService>.Instance;

    public async Task<MediaBlobBackfillResult> BackfillAsync(CancellationToken ct = default)
    {
        // No ACL scoping here on purpose: this is a maintenance pass over EVERY media row, not a
        // caller-facing read. Selecting the raw id string (not a mapped Guid) keeps the exact stored
        // casing for the UPDATE below. Raw ADO.NET rather than Dapper: this service lives in Core,
        // which does not reference Dapper.
        var rawIds = new List<string>();
        using (var conn = connFactory.CreateConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id FROM tbl_media WHERE ciphertext_sha256 IS NULL";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                rawIds.Add(reader.GetString(0));
        }

        if (rawIds.Count == 0)
            return new MediaBlobBackfillResult(0, 0, 0);

        int stored = 0, missing = 0;
        foreach (var rawId in rawIds)
        {
            ct.ThrowIfCancellationRequested();

            // Build the file path from the canonical lower-case GUID, which is how the create path
            // named it — not from the upper-case DB string, which would miss on Linux.
            string fileName;
            try { fileName = Guid.Parse(rawId).ToString() + ".enc"; }
            catch (FormatException)
            {
                missing++;
                this.logger.LogWarning("Media id {Id} is not a valid GUID — skipping backfill.", rawId);
                continue;
            }

            var path = Path.Combine(options.MediaDir, fileName);
            if (!File.Exists(path))
            {
                missing++;
                this.logger.LogWarning(
                    "Media {Id}: no ciphertext hash and no .enc file at {Path} — cannot backfill. Its bytes " +
                    "exist only on a peer; a sync pull will populate the blob, or the row is stale.", rawId, path);
                continue;
            }

            byte[] ciphertext;
            try { ciphertext = await File.ReadAllBytesAsync(path, ct); }
            catch (IOException ex)
            {
                missing++;
                this.logger.LogWarning(ex, "Media {Id}: .enc file at {Path} could not be read — skipping.", rawId, path);
                continue;
            }

            // Store first (idempotent, content-addressed), then stamp the row. If we crash between
            // the two, the blob is harmless and the row is retried next pass. The guard
            // `ciphertext_sha256 IS NULL` keeps a concurrent create/apply that already set it from
            // being clobbered.
            var hash = await blobRepo.StoreAsync(ciphertext);
            using (var conn = connFactory.CreateConnection())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "UPDATE tbl_media SET ciphertext_sha256 = @hash WHERE id = @id AND ciphertext_sha256 IS NULL";
                AddParam(cmd, "@hash", hash);
                AddParam(cmd, "@id", rawId);
                cmd.ExecuteNonQuery();
            }
            stored++;
        }

        var already = rawIds.Count - stored - missing;
        this.logger.LogInformation(
            "Media blob backfill: {Stored} moved into the blob store, {Missing} with no local file, {Already} already done.",
            stored, missing, already);

        return new MediaBlobBackfillResult(stored, already, missing);
    }

    /// <summary>
    /// Item 16b, phase 2: delete the now-redundant <c>.enc</c> files. A file is removed ONLY when
    /// its media row carries a hash AND that blob is actually present in the store — so the blob is
    /// always the surviving copy and the last copy of a media is never deleted. A file whose row is
    /// gone, whose row has no hash, or whose blob is missing is left untouched (a sync pull or the
    /// orphan sweep deals with those). Idempotent: once the files are gone it finds nothing.
    ///
    /// <para>Runs after <see cref="BackfillAsync"/>, which is what guarantees the blob exists for
    /// every file this could delete.</para>
    /// </summary>
    public async Task<int> SweepRedundantEncFilesAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(options.MediaDir))
            return 0;

        var deleted = 0;
        foreach (var path in Directory.GetFiles(options.MediaDir, "*.enc"))
        {
            ct.ThrowIfCancellationRequested();

            // File name is the lower-case GUID; the row id is stored upper-case — match case-
            // insensitively. A file that is not a media-id.enc is left alone.
            if (!Guid.TryParse(Path.GetFileNameWithoutExtension(path), out var id))
                continue;

            string? hash = null;
            using (var conn = connFactory.CreateConnection())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT ciphertext_sha256 FROM tbl_media WHERE id = @id COLLATE NOCASE";
                AddParam(cmd, "@id", id.ToString());
                hash = cmd.ExecuteScalar() as string;
            }

            // No row, or a row still without a hash → keep the file: it may be the only copy.
            if (string.IsNullOrEmpty(hash))
                continue;

            // Confirm the blob is really present before removing the file — never delete the last copy.
            if (!(await blobRepo.GetExistingAsync(new[] { hash })).Contains(hash))
            {
                this.logger.LogWarning(
                    "Media {Id}: row has hash {Hash} but the blob is absent — keeping the .enc file.", id, hash);
                continue;
            }

            try
            {
                File.Delete(path);
                deleted++;
            }
            catch (IOException ex)
            {
                this.logger.LogWarning(ex, "Could not delete redundant .enc file {Path} — will retry next start.", path);
            }
        }

        if (deleted > 0)
            this.logger.LogInformation("Media .enc sweep: deleted {Deleted} redundant file(s) now served from the blob store.", deleted);

        return deleted;
    }

    private static void AddParam(IDbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}
