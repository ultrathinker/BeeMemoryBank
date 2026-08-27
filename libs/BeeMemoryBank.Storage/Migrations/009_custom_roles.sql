-- Custom roles carrying folder ACLs of their own. Node-local, exactly like tbl_user and
-- tbl_folder_acl_entry: never synchronised, never part of a sync event payload.
--
-- Problem this solves: folder restrictions were per-user only, so hiding one folder from every
-- regular user meant editing every user by hand -- and remembering to repeat it for each new
-- hire, or the folder leaked. Rules now also live on the role, and every user holding that role
-- inherits them live (resolved on read, never copied at assignment time -- copying would defeat
-- the whole point, since editing the role later would not reach the users).
--
-- Two rows are seeded with is_system = 1 and cannot be created, renamed or deleted through the
-- API: 'superadmin' and 'user'. Everything else is a custom role. Custom roles sit at exactly the
-- same privilege tier as 'user': every authorization check in this codebase tests for the literal
-- "superadmin" and treats every other role string as unprivileged, so a custom role grants no
-- administrative capability by construction.
--
-- base_policy makes "this role has no allow rows" explicit instead of implicit:
--   'open'   -> no allow rows means the whole vault is visible (minus deny rows). This is the
--               historical behaviour of the built-in roles and stays their value.
--   'closed' -> no allow rows means nothing is visible. New custom roles default to this so a
--               role created before anyone has configured its rules cannot expose the vault.
-- It has an effect ONLY when the role has zero allow rows; a non-empty allow list is a whitelist
-- under either policy.
CREATE TABLE IF NOT EXISTS tbl_role (
    name         TEXT PRIMARY KEY COLLATE NOCASE,
    display_name TEXT NOT NULL,
    description  TEXT,
    is_system    INTEGER NOT NULL DEFAULT 0,
    base_policy  TEXT NOT NULL DEFAULT 'closed' CHECK(base_policy IN ('open', 'closed')),
    created_at   TEXT NOT NULL,
    updated_at   TEXT NOT NULL
);

-- COLLATE NOCASE on the primary key is a security control, not a convenience: CallerIdentity
-- compares the forwarded X-User-Role header against "superadmin" with an ordinal ==, while the
-- Web layer's RequireRole/IsInRole matching is case-insensitive. A role literally named
-- "SuperAdmin" would therefore be unprivileged in one layer and privileged in the other. The
-- NOCASE key makes that row impossible to insert; RoleService additionally rejects reserved
-- names and anything outside [a-z0-9_-].
INSERT OR IGNORE INTO tbl_role (name, display_name, description, is_system, base_policy, created_at, updated_at)
VALUES ('superadmin', 'Superadmin',
        'Full administrative and vault access. Bypasses every folder restriction.',
        1, 'open', strftime('%Y-%m-%dT%H:%M:%SZ', 'now'), strftime('%Y-%m-%dT%H:%M:%SZ', 'now'));

INSERT OR IGNORE INTO tbl_role (name, display_name, description, is_system, base_policy, created_at, updated_at)
VALUES ('user', 'User',
        'Default non-privileged role. Rules added here apply to every user who has no other role.',
        1, 'open', strftime('%Y-%m-%dT%H:%M:%SZ', 'now'), strftime('%Y-%m-%dT%H:%M:%SZ', 'now'));

-- Mirrors tbl_folder_acl_entry one-for-one, keyed by role instead of user. Kept as a separate
-- table rather than making tbl_folder_acl_entry.user_id nullable: SQLite cannot relax NOT NULL
-- in place (it needs a full table rebuild on a live production database), and NULLs inside a
-- PRIMARY KEY compare as distinct in SQLite, which would silently permit duplicate role rows.
-- role_name repeats tbl_role.name's COLLATE NOCASE deliberately: SQLite compares a column
-- with its OWN declared collation, so a BINARY role_name would let 'user' and 'User' be two
-- distinct rows in this table's primary key, and would make a DELETE or an is_read_only toggle
-- issued with different casing silently match nothing.
CREATE TABLE IF NOT EXISTS tbl_role_folder_acl_entry (
    role_name    TEXT    NOT NULL COLLATE NOCASE REFERENCES tbl_role(name) ON DELETE CASCADE ON UPDATE CASCADE,
    folder_id    TEXT    NOT NULL REFERENCES tbl_folder(id) ON DELETE CASCADE,
    effect       TEXT    NOT NULL CHECK(effect IN ('allow', 'deny')),
    -- Same semantics as tbl_folder_acl_entry: meaningful only on an allow row, ignored on deny.
    is_read_only INTEGER NOT NULL DEFAULT 0,
    created_at   TEXT    NOT NULL,
    PRIMARY KEY(role_name, folder_id, effect)
);

-- Cache invalidation walks folder -> roles -> users whenever a folder is moved, renamed or
-- deleted, so the folder_id lookup is on the hot path of every folder mutation.
CREATE INDEX IF NOT EXISTS idx_role_folder_acl_folder ON tbl_role_folder_acl_entry(folder_id);

-- Same reason on the user side: invalidating a role fans out to every user holding it. The
-- COLLATE NOCASE is load-bearing, not cosmetic -- tbl_user.role was created with the default
-- BINARY collation, and SQLite cannot use a BINARY index to satisfy the explicit
-- `WHERE role = @role COLLATE NOCASE` that UserRepository.GetUserIdsByRoleAsync issues.
CREATE INDEX IF NOT EXISTS idx_user_role ON tbl_user(role COLLATE NOCASE);
