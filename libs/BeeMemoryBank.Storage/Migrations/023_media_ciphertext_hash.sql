-- Item 16a, phase 1: give a media row the content-address of its own ciphertext, so the read
-- path can resolve the bytes from the content-addressed blob store (tbl_blob) instead of only
-- from the .enc file on disk.
--
-- Media ciphertext has ALWAYS been stored in tbl_blob at create time (LogMediaCreateAsync →
-- EnsureBlobAsync), but the hash needed to find it again lived only in the media_create EVENT
-- payload, never on the media row. So the row could not, by itself, point at its blob — the read
-- path had to go to the file. This column closes that gap. It is additive and nullable; NULL means
-- "no known blob for this row, read the .enc file" (media created before the blob store, or media
-- whose create event was already compacted away), and the read path keeps that fallback.
--
-- This phase deliberately does NOT stop writing .enc files and does NOT read any file: it is fully
-- reversible and cannot lose a byte. Dropping the double storage and the disk-reading backfill are
-- later, gated steps.

ALTER TABLE tbl_media ADD COLUMN ciphertext_sha256 TEXT;

-- Backfill from the media_create events still present in the log. The hash recorded in the payload
-- IS the blob hash (both come from BlobHash.Compute → lowercase hex), so no recomputation is needed.
--
-- CASE MATTERS: System.Text.Json serializes the payload's media_id as a lowercase Guid, while
-- tbl_media.id is stored uppercase. Comparing them directly matches nothing (SQLite TEXT compares
-- case-sensitively) — the same trap that silently emptied every article's tag list before it was
-- found. upper() on the extracted id is what makes the join actually match.
UPDATE tbl_media
SET ciphertext_sha256 = (
    SELECT json_extract(e.payload, '$.ciphertext_sha256')
    FROM tbl_event e
    WHERE e.event_type = 'media_create'
      AND upper(json_extract(e.payload, '$.media_id')) = tbl_media.id
      AND json_extract(e.payload, '$.ciphertext_sha256') IS NOT NULL
    ORDER BY e.sequence_num DESC
    LIMIT 1
)
WHERE ciphertext_sha256 IS NULL;
