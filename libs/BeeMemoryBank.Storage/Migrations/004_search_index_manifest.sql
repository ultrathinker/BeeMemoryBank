-- Local-only cache metadata for encrypted-at-rest search index segments (WP-09). Neither table
-- below is ever synced or treated as authoritative: a missing/corrupted row or file simply means
-- the affected segment must be rebuilt from source article content -- the rebuild trigger itself
-- is a later work package's (WP-11) job, this migration only lays the bookkeeping it reads.

-- One row per on-disk encrypted segment file. dek_epoch is recorded at write time so a later
-- reader can cheaply detect "the master DEK rotated since this segment was encrypted" by
-- comparing against the node's current epoch, without ever attempting a doomed decrypt.
CREATE TABLE tbl_search_index_manifest (
    segment_id     TEXT PRIMARY KEY,
    file_path      TEXT NOT NULL,
    doc_count      INTEGER NOT NULL,
    dek_epoch      INTEGER NOT NULL,
    format_version INTEGER NOT NULL,
    created_at     TEXT NOT NULL
);

-- Exactly one row: the current node's "index key" -- a random 32-byte secret wrapped under the
-- master DEK exactly the way BeeMemoryBank.Core.Embeddings.ProjectionMatrix wraps its own matrix
-- bytes (DekManager.WrapDek). Segment bytes are encrypted with this key, not the master DEK
-- directly, so replacing it (e.g. after corruption) never touches the master DEK or anything
-- else that depends on it. dek_epoch records which epoch the key was wrapped under, for the same
-- cheap-mismatch-detection reason as tbl_search_index_manifest.dek_epoch above.
CREATE TABLE tbl_search_index_key (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    wrapped_key BLOB NOT NULL,
    iv          BLOB NOT NULL,
    dek_epoch   INTEGER NOT NULL,
    created_at  TEXT NOT NULL
);
