-- Security fix (finding H6): every agent row used to wrap the SAME master DEK regardless of who
-- owned it (encrypted_dek/dek_iv were NOT NULL), which made every agent key -- including a
-- self-service one an ordinary, folder-restricted user minted for themselves (limit 20 per
-- owner) -- cryptographically a key to the ENTIRE vault. The folder restriction and read-only
-- flag on an agent are enforced only in software, over already-decrypted content; the key
-- material itself unwrapped the master DEK no matter what its owner's ACL said. Anyone holding
-- such a key, plus any copy of the database file (a backup, a decommissioned disk), could
-- decrypt every article in the vault -- including folders that key's own owner has no web access
-- to.
--
-- Decision: hybrid. Only an agent owned by a superadmin may carry a wrapped master DEK and
-- auto-unlock the vault (AgentAuthMiddleware) -- a superadmin can already unlock the vault
-- through the web UI (SessionEndpoints), so an agent doing it on their behalf adds no new
-- capability. An ordinary user cannot unlock the vault through the web UI at all (login there
-- returns 403 "Server is locked" for them), so an agent that could was a bug, not a feature. An
-- ordinary user's agent keeps authenticating and working exactly as before whenever the vault is
-- ALREADY unlocked by someone else; it simply can no longer unlock it by itself, and a stolen
-- database file yields nothing usable from its row alone.
--
-- This migration does two things:
--
--  1. Clears the wrapped DEK (encrypted_dek, dek_iv, salt, and resets kdf_version to 0) from
--     every EXISTING agent row whose owner is not currently a superadmin (including a row whose
--     owner_user_id no longer resolves to any user -- treated the same as non-superadmin, the
--     safer default). key_prefix/key_hash are left untouched: these keys stay valid for
--     authentication, they just stop being vault keys. This is irreversible by design --
--     re-wrapping would need the plaintext API key, which was only ever shown once at creation
--     and is not recoverable from key_hash.
--
--  2. Relaxes encrypted_dek/dek_iv from NOT NULL to nullable, because new agents created for a
--     non-superadmin owner (AgentEndpoints.MapPost "/", AgentCommand.HandleCreateAsync) never
--     populate them at all.
--
-- SQLite cannot relax a NOT NULL constraint in place -- it needs the standard
-- rename/rebuild/copy/drop dance (same pattern discussed in 009_custom_roles.sql's
-- tbl_role_folder_acl_entry comment, and see MigrationRunner's needsFkOff handling of
-- RENAME TO / DROP TABLE, which this migration relies on).

ALTER TABLE tbl_agent RENAME TO tbl_agent_pre013;

CREATE TABLE tbl_agent (
    id               INTEGER PRIMARY KEY AUTOINCREMENT,
    name             TEXT NOT NULL,
    description      TEXT,
    key_prefix       TEXT NOT NULL,
    key_hash         TEXT NOT NULL UNIQUE,
    -- Nullable from this migration on. NULL means "no wrapped master DEK" -- this agent belongs
    -- to a non-superadmin owner (or its superadmin owner has since been demoted, or deleted) and
    -- cannot auto-unlock the vault. See Agent.CanAutoUnlock / AgentAuthMiddleware.
    encrypted_dek    BLOB,
    dek_iv           BLOB,
    kdf_version      INTEGER NOT NULL DEFAULT 0,
    salt             BLOB,
    status           TEXT NOT NULL DEFAULT 'A',
    created_at       TEXT NOT NULL,
    last_accessed_at TEXT,
    request_count    INTEGER NOT NULL DEFAULT 0,
    owner_user_id    INTEGER NOT NULL REFERENCES tbl_user(id) ON DELETE RESTRICT
);

INSERT INTO tbl_agent (
    id, name, description, key_prefix, key_hash,
    encrypted_dek, dek_iv, kdf_version, salt,
    status, created_at, last_accessed_at, request_count, owner_user_id
)
SELECT
    a.id, a.name, a.description, a.key_prefix, a.key_hash,
    CASE WHEN u.role = 'superadmin' THEN a.encrypted_dek ELSE NULL END,
    CASE WHEN u.role = 'superadmin' THEN a.dek_iv        ELSE NULL END,
    CASE WHEN u.role = 'superadmin' THEN a.kdf_version    ELSE 0    END,
    CASE WHEN u.role = 'superadmin' THEN a.salt           ELSE NULL END,
    a.status, a.created_at, a.last_accessed_at, a.request_count, a.owner_user_id
FROM tbl_agent_pre013 a
LEFT JOIN tbl_user u ON u.id = a.owner_user_id;

DROP TABLE tbl_agent_pre013;
