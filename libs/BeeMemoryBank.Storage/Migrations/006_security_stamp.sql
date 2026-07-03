-- W3 (Option A): per-user security stamp for session revocation.
-- security_stamp is NODE-LOCAL: it is read/written only by the local API and must NOT
-- enter any sync event payload, EventApplier, or tbl_event. Bumped on every
-- identity-affecting change (password change, role change, IsActive flip, deletion) so a
-- stale cookie is rejected on the next revalidation (see Web OnValidatePrincipal).
-- SQLite cannot default a column to a per-row random value, so add then backfill.

ALTER TABLE tbl_user ADD COLUMN security_stamp TEXT NOT NULL DEFAULT '';

UPDATE tbl_user SET security_stamp = lower(hex(randomblob(16))) WHERE security_stamp = '';
