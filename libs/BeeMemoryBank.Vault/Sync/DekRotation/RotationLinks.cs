using System.Data;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync.Recovery;
using Dapper;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Sync.DekRotation;

/// <summary>
/// The retired link a rotation writes itself (release 2.12, plan WS-A3): the OLD master DEK wrapped under the NEW one, in
/// <c>tbl_dek_retired_link</c>, inside the rotation transaction, on the initiator and on every peer. Before 2.12 the link was rebuilt later by
/// <see cref="RecoveryReconciler"/> from the local chain (NEW under OLD); since a rotation keeps that chain only while the node has v1 doors,
/// the rotation writes the link from the two keys it holds. A marker in <c>tbl_migration_marker</c> (same transaction) says the link still has
/// to be published as <c>retired_link_set</c>; the reconciler publishes it after the commit and removes the marker, so a crash between the
/// commit and the publication loses nothing. The reconciler's own chain walk stays for rotations applied before 2.12.
/// </summary>
public static class RotationLinks
{
    /// <summary>Key prefix of the "still to publish" marker; the rest is the rotation's commit id.</summary>
    public const string MarkerPrefix = "retired-link-unpublished:";

    /// <summary>Writes the link (OLD under NEW) of rotation <paramref name="commitId"/> by this node, and the marker. A retry is a no-op.</summary>
    public static void Write(IDbConnection connection, IDbTransaction transaction, string commitId, string nodeId, byte[] oldDek, byte[] newDek)
    {
        var commit = Guid.Parse(commitId).ToString("D");
        var author = Guid.Parse(nodeId).ToString("D");
        var (wrapped, iv) = MasterKeyManager.WrapMasterDek(oldDek, newDek);
        var now = DateTime.UtcNow.ToString("O");
        connection.Execute(
            @"INSERT OR IGNORE INTO tbl_dek_retired_link (commit_id, author_node_id, old_fingerprint, new_fingerprint, wrapped, iv, created_at)
              VALUES (@commit, @author, @old, @new, @wrapped, @iv, @now)",
            new { commit, author, old = DekFingerprint.Of(oldDek), @new = DekFingerprint.Of(newDek), wrapped, iv, now }, transaction);
        connection.Execute(
            "INSERT OR IGNORE INTO tbl_migration_marker (key, value, set_at) VALUES (@key, @author, @now)",
            new { key = MarkerPrefix + commit, author, now }, transaction);
    }

    /// <summary>
    /// Publishes every link this node wrote and has not published yet, then removes its marker. A link whose row is gone (a restore replaced the
    /// table) only loses its marker. Called by the reconciler, which runs after a rotation and at every unlock.
    /// </summary>
    public static async Task PublishPendingAsync(IDbConnectionFactory connFactory, RecoveryEventPublisher publisher, ILogger logger)
    {
        List<(string Key, string Author)> markers;
        using (var conn = connFactory.CreateConnection())
            markers = (await conn.QueryAsync<(string, string)>(
                "SELECT key, value FROM tbl_migration_marker WHERE key LIKE @prefix ORDER BY set_at", new { prefix = MarkerPrefix + "%" })).ToList();

        foreach (var (key, author) in markers)
        {
            var commitId = key[MarkerPrefix.Length..];
            (string Old, string New, byte[] Wrapped, byte[] Iv)? link;
            using (var conn = connFactory.CreateConnection())
                link = await conn.QuerySingleOrDefaultAsync<(string, string, byte[], byte[])?>(
                    @"SELECT old_fingerprint, new_fingerprint, wrapped, iv FROM tbl_dek_retired_link
                       WHERE commit_id = @commitId COLLATE NOCASE AND author_node_id = @author COLLATE NOCASE",
                    new { commitId, author });

            if (link is { } l)
            {
                await publisher.PublishAsync(EventTypes.RetiredLinkSet, new RetiredLinkSetPayload(
                    CommitId: commitId, OldFingerprint: l.Old, NewFingerprint: l.New,
                    Wrapped: Convert.ToBase64String(l.Wrapped), Iv: Convert.ToBase64String(l.Iv)));
                logger.LogInformation("Published the retired link of rotation {CommitId}", commitId);
            }

            using (var conn = connFactory.CreateConnection())
                await conn.ExecuteAsync("DELETE FROM tbl_migration_marker WHERE key = @key", new { key });
        }
    }
}
