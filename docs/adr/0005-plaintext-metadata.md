# ADR 0005: Plaintext Folder Paths and Article Titles as Metadata

## Status
Accepted

## Context
BeeMemoryBank encrypts article content at rest using envelope encryption: each article body has
its own DEK, wrapped by the node's master DEK, itself unlocked by a user's master password. The
open question this ADR settles is what happens to *metadata* — article titles, folder paths and
names, concept tag names, and timestamps — used for navigation, search, and access control. Does
it get the same encryption treatment as content, or does it stay in plaintext?

The schema (`libs/BeeMemoryBank.Storage/Migrations/001_initial_schema.sql`) shows the actual
split already in place:
- **Plaintext**: `tbl_article.title`, `tbl_article.tree_path`; `tbl_folder.path`,
  `tbl_folder.name`, `tbl_folder.parent_path`; `tbl_concept_tag.name`; `created_at`/`updated_at`/
  `deleted_at` timestamps across articles, folders, and media; `tbl_media.file_name` and
  `content_type`; and the entire `tbl_folder_acl_entry` table (which `user_id` is
  allowed/denied/read-only on which `folder_id`).
- **Encrypted**: article/version/conflict body ciphertext, held in
  `tbl_article_body.ciphertext` / `tbl_article_version.ciphertext` /
  `tbl_conflict_version.ciphertext`, each with its own `encrypted_dek`/`dek_iv` wrapped by the
  master DEK (the same per-row envelope pattern used elsewhere in the system). Media file bytes
  are envelope-encrypted the same way but, per `docs/architecture.md`'s "Per-media DEK" note,
  stored as `.enc` files on disk rather than in SQLite, to avoid bloating the database.

Folder-level access control depends directly on the plaintext path. `CallerScope`
(`libs/BeeMemoryBank.Core/Services/CallerScope.cs`) computes per-request allow/deny/read-only
path sets from `tbl_folder_acl_entry` and exposes `IsAccessDenied`/`IsWriteDenied`/`IsReadOnly`/
`IsNavigable`/`FilterArticles`/`FilterFolders`, all of which take a plain `treePath`/`path`
string. `ArticleRepository` calls `_holder.Scope.IsAccessDenied(article.TreePath)` immediately
after loading a row (e.g. in `GetByIdAsync`) and `_holder.Scope.FilterArticles(articles)` after
every list/search query — both operating directly on the `tree_path` value already present in
the row or query result, with no decryption step involved in making that decision.

## Evaluated Options

1. **Encrypt metadata alongside content** (title, path, tags, timestamps all behind the same
   envelope encryption as the body).
   - **Pros**: a raw database compromise reveals nothing at all — no titles, no folder
     structure, no tag taxonomy, just opaque encrypted rows.
   - **Cons**: every folder-tree render, title search, and ACL check would need to decrypt the
     relevant column before it could even decide whether to show, hide, or allow a write on
     that row. That turns what are currently plain SQL projections and in-memory
     `HashSet<string>` lookups into per-row decrypt operations on every list/search/tree
     request — and worse, inverts the natural check order into "decrypt first to find out if
     you're even allowed to see this," which sits awkwardly against the existing ACL model.
2. **Encrypt some metadata but not others** (e.g. titles but not paths).
   - **Pros**: partial mitigation of the exposure in option 1.
   - **Cons**: folder ACL fundamentally needs the plaintext path as its enforcement key, and
     once `tree_path` is plaintext, an article's location in the hierarchy is already exposed —
     encrypting the title alone would hide *what* an article is called while *where it lives and
     how it's organized* remains visible, a partial improvement that doesn't remove the need for
     the more invasive parts of option 1's cost, for comparatively little gain.
3. **Plaintext metadata, encrypted content only** (chosen, and what the schema already
   implements).

## Decision
Titles, folder paths/names, concept tag names, and timestamps stay in plaintext columns.
Article/version/conflict-version bodies and media file bytes remain behind per-row envelope
encryption (a DEK per article/media item, wrapped by the master DEK).

### Justification
- **ACL enforcement has to run as a fast, plain check on every request.** The whole per-folder
  ACL model — `tbl_folder_acl_entry` rows resolved into `HttpCallerScope`'s in-memory
  allow/deny/read-only path sets, consulted via `IsAccessDenied`/`IsWriteDenied`/`IsReadOnly`/
  `FilterArticles`/`FilterFolders` on essentially every repository call — is keyed on the
  `tree_path`/`path` string. If that string were encrypted, resolving "can this caller see this
  row" would require decrypting the path first, for every candidate row, before an ACL decision
  could even be made.
- **Search and tree navigation need to be plain SQL.** Title search and tree rendering work as
  ordinary SQL projections and orderings over `title`/`tree_path`/`path`. Making that work over
  ciphertext would mean either an application-level decrypt-then-filter pass over every article
  (which stops scaling well past a small collection) or a separate searchable-encryption/index
  scheme — a materially different and more complex architecture for a personal knowledge-base
  product.
- **The actual sensitive payload — content — is protected.** Article, version, and
  conflict-version bodies are AES-GCM ciphertext with a per-row DEK wrapped by the master DEK
  (the same "Per-article DEK" isolation decision recorded elsewhere in
  `docs/architecture.md`: compromising one row's DEK doesn't expose the rest). Media file bytes
  get the same per-file envelope treatment and are kept out of SQLite entirely as `.enc` files.

## Consequences and Trade-offs
Be explicit about what this means for anyone who obtains the raw SQLite file (a stolen backup,
a leaked snapshot, a compromised disk) without ever unlocking the vault:

- **What leaks**: the entire folder tree — every folder's path, name, and nesting
  (`tbl_folder`); every article's title and its location in that tree
  (`tbl_article.title`/`tree_path`); every concept tag name and which articles carry it
  (`tbl_concept_tag.name`, `tbl_article_concept_tag`); every media file's name, content type,
  and size (`tbl_media.file_name`/`content_type`/`file_size` — not its bytes); creation/update/
  deletion timestamps and which node authored each change; and the complete folder ACL
  structure — which user is allowed, denied, or read-only on which folder
  (`tbl_folder_acl_entry`). In short: an attacker with the raw file can reconstruct the shape of
  the knowledge base and who has access to what, without reading a single word of actual note
  content.
- **What stays protected**: article/version/conflict body text and media file bytes. None of it
  is recoverable from the database file or the on-disk `.enc` media files without a valid
  unwrapped master DEK, which itself requires the correct master password to unwrap a key slot
  in `tbl_key_slot`.
- **A related, narrower plaintext-by-design case**: `tbl_article.protection_hint` stores an
  optional reminder phrase for passphrase-locked articles, deliberately left in plaintext so it
  can be shown on the lock screen *before* unlock. It's the same underlying trade-off — plaintext
  where the UX genuinely needs it before decryption is possible — applied narrowly to a single
  opt-in field, rather than to metadata generally.
- **This is a deliberate, bounded trade-off**, not an oversight: BeeMemoryBank's threat model
  protects content confidentiality against a raw-file compromise, but does not treat folder
  structure, article titles, or tag taxonomy as confidential in that scenario. Anyone deploying
  BeeMemoryBank in a context where the *names and organization* of articles are themselves
  sensitive — not just their content — should know this going in, rather than discover it by
  surprise.
