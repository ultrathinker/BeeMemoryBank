using BeeMemoryBank.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Api.Services;

// The database copy a baseline is made from (docs/design/step-a-spec.md 7.2). It shares the allow-list filter and the final compaction with
// the peer snapshot; what a baseline adds is in FilterForBaseline. Not linked into the BlindNode host: a blind node never produces one.
public partial class SnapshotService
{
    /// <summary>Kept on top of <c>SnapshotTables.Replicated</c>, as the blind package keeps it (spec 7.2).</summary>
    private static readonly string[] BaselineKeepTables = ["tbl_hard_delete_audit"];

    /// <summary>
    /// Turns <paramref name="stagedDbPath"/>, a <c>VACUUM INTO</c> copy of the live database, into the plaintext a baseline seals: the
    /// secret and node-local tables dropped or emptied exactly as for a peer snapshot (allow-list, hard-delete audit kept), the blob store
    /// emptied (blobs travel and are listed apart), and the derived columns set to NULL (a restored node recomputes them). Every change
    /// comes before the final compaction, so the file holds no trace of what was removed.
    /// </summary>
    public void FilterForBaseline(string stagedDbPath)
    {
        using (var conn = OpenStaged(stagedDbPath))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"PRAGMA secure_delete = ON;
                UPDATE tbl_article SET embedding_projection = NULL, embedding_model_version = NULL;
                UPDATE tbl_concept_tag SET embedding = NULL, embedding_model_version = NULL;
                DELETE FROM tbl_blob;";
            cmd.ExecuteNonQuery();
        }

        FilterSecretsFrom(stagedDbPath, BaselineKeepTables);
    }

    /// <summary>Signs with this node's identity key through the key operations the service was given (every identity version; a locked vault cannot sign a v=1 one).</summary>
    public byte[] SignWithNodeIdentity(BeeMemoryBank.Core.Models.NodeIdentity identity, byte[] payload) => SignWithIdentity(identity, payload);

    /// <summary>The size of the live database file: a baseline needs twice that free in the staging folder and in the baselines folder.</summary>
    public long LiveDatabaseBytes()
    {
        var path = Path.Combine(_dataPath, DbFileName);
        return File.Exists(path) ? new FileInfo(path).Length : 0;
    }
}
