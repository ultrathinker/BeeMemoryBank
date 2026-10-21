# Security Policy

## Encryption Architecture

BeeMemoryBank uses a **3-layer envelope encryption** model designed to protect article, version, and media content at rest and in transit. Titles, folder paths and names, concept tag names, timestamps, the FTS5 search index, sync event-log payloads, and audit-log details are stored in plaintext by design — they're metadata that folder ACLs, search, and sync all need as fast, plain lookups rather than per-row decrypt operations. This is a deliberate, documented trade-off, not an oversight: see [ADR-0005](docs/adr/0005-plaintext-metadata.md) for the exact table-by-table split and what it means for someone who obtains the raw database file. Since 2.6.0 the metadata that travels between nodes, and what blind copies store, can be encrypted as well (a one-way switch for the whole network, off until the owner turns it on): see [Metadata encryption](#metadata-encryption-sync-protocol-4) below.

### Encryption Layers

```
Password ──Argon2id──▶ KEK (Key Encryption Key)
                            │
                     AES-256-GCM wrap
                            │
                            ▼
                      Master DEK (Data Encryption Key)
                            │
                     AES-256-GCM wrap
                            │
                            ▼
                      Article DEK (per-article)
                            │
                     AES-256-GCM encrypt
                            │
                            ▼
                      Article Content (ciphertext)
```

### Cryptographic Primitives

| Component | Algorithm | Parameters |
|---|---|---|
| Symmetric encryption | AES-256-GCM | 256-bit key, 96-bit nonce, 128-bit tag |
| Key derivation (Password → KEK) | Argon2id | 64 MB memory, 3 iterations, 4 parallelism (OWASP) |
| Key derivation salt | CSPRNG | 256-bit (32 bytes) |
| Digital signatures (sync) | Ed25519 | 32-byte seed private key, 32-byte public key |
| All random values | `RandomNumberGenerator` | .NET CSPRNG |

### Per-Article DEK

Each article is encrypted with a unique Data Encryption Key (DEK). This means:

- Compromising one article's DEK does not affect other articles
- Articles can be re-encrypted individually without touching the master key
- Article DEKs are wrapped (encrypted) with the Master DEK and stored in the database

### Multi-Slot Key System

BeeMemoryBank uses a LUKS-style multi-slot key system stored in `tbl_key_slot`:

- Multiple passwords can wrap the same Master DEK
- Each key slot contains: Argon2id salt, wrapped Master DEK ciphertext, IV, and iteration parameters
- Passwords can be added or changed without re-encrypting any article content
- A sentinel value (`AES-256-GCM("BeeMemoryBank", masterDEK)`) is used to verify correct password entry
- A `recovery` slot opens with a recovery key a superadmin issued and keeps offline. Besides unlocking from the command line, it lets the owner of a forgotten superadmin password set a new one **without signing in** (Sign In page → *Forgot your password? Use a recovery key*, or `bmb user reset-password`). That path is built so it can only help the owner: it opens `recovery` slots only and checks the sentinel; it is limited to superadmin accounts; every refusal (unknown user, ordinary user, wrong or foreign key) is one identical answer after identical Argon2id work; it is throttled per client address and per username and audited (`user_password_recovery_reset` / `_refused`, never the key); it replaces only that user's key slot and ends their web sessions and remote tokens; and it never unlocks the shared session or signs anyone in. Whoever holds a recovery key is as powerful as an administrator who knows the password — keep the key as carefully as the password. See [docs/account-recovery.md](docs/account-recovery.md).

### Agent Key Encryption

API agent keys use a separate wrapping mechanism:

- Agent keys are SHA-256 hashed for database lookup
- A derived encryption key (HKDF-SHA256 with a per-agent random salt, `kdf_version = 1`; legacy
  `v0` agents still authenticate against the older `SHA256(apiKey + "bmb-encrypt")` derivation)
  wraps the Master DEK for agent access
- This allows programmatic access without storing the user's password
- **Only an agent owned by a superadmin can carry a wrapped Master DEK and auto-unlock a locked
  node.** Before this, every agent's key wrapped the same Master DEK regardless of owner, so a
  self-service agent minted by an ordinary, folder-restricted user was cryptographically a key to
  the *entire* vault — the folder ACL and read-only flag are enforced only in software, over
  already-decrypted content, not by the key material. An ordinary user's agent still authenticates
  and works normally whenever the vault is already unlocked by someone else; it just can't unlock
  it by itself, and a stolen database file yields nothing usable from its row alone. Demoting a
  superadmin strips the wrapped DEK from every agent they own; a revoked agent's key material is
  wiped regardless of the owner's role.

### Data at Rest

- All article, version, and media content is stored encrypted in SQLite (or, for media bytes, as `.enc` files on disk)
- Titles, folder paths/names, tag names, and timestamps are stored in plaintext — see [ADR-0005](docs/adr/0005-plaintext-metadata.md); on a full node this stays so after metadata encryption is switched on, while the event log and blind copies then hold them only sealed
- Keys are never persisted in plaintext
- The Master DEK exists only in memory during an active session
- Since 2.6.0 the owner can also encrypt the database files themselves, per node, on purpose: see [Encrypted database](#encrypted-database-260-opt-in-per-node) below
- Salt and wrapped key material are stored per-slot for offline brute-force resistance

### iPhone Regular App

The source-only iPhone regular app keeps the vault in its app container, excluded from iCloud and computer backups. Its master password
is never stored; the Master DEK exists only while the app is unlocked. Optional Face ID / Touch ID unlock uses a random key in the
device-only Keychain, gated by the current biometric set, to wrap the DEK rather than storing the password. The app has no listener or
server surface: it calls pinned peers while open and for the short time iOS grants as it leaves the foreground. The phones' own database
encryption (2.6.0) is built and tested but stays hidden until its native release gate has been run on a device
([docs/sealed-database.md](docs/sealed-database.md#phones)); neither phone ever hands a database key or a page key to the Keystore or the Keychain.

### Online DEK Rotation

Replacing the Master DEK across an entire network without exporting/re-importing the vault. Reasons to rotate: suspected compromise of the current DEK, periodic key hygiene, or rotating off material that may have transited insecure paths.

**Confidential per-peer envelopes (ADR 0006, since 2026-09-04).** The new DEK is no longer wrapped
under the old one for transport — that old scheme meant every current *and revoked* peer already
held a copy of the wrapped new key inside the plaintext, replicated `dek_rotation_commit` event, so
revoking a peer didn't stop it from reading every future rotation (see "What rotation does not
protect against" below for what this still doesn't fix). The initiator instead seals the new DEK
once per currently-trusted peer with a per-peer X25519 envelope derived from the Ed25519 identity
keys every node and whitelist row already carries — no new key material, and a revoked node simply
receives no envelope it can open. See [docs/adr/0006-confidential-dek-rotation-x25519-envelopes.md](docs/adr/0006-confidential-dek-rotation-x25519-envelopes.md)
for the full design. The legacy old-DEK-wrapped field survives only as a fallback for a peer that
hasn't upgraded past this change yet.

**Initiator flow** (one superadmin, one node):

1. **Propose** — verify the master password against the current DEK; generate a fresh 32-byte DEK; seal it into a per-peer X25519 envelope for every currently-trusted peer (a peer with an unusable/malformed identity key is excluded from this rotation rather than aborting it for everyone); emit a signed `dek_rotation_proposed` sync event.
2. **Accept** — emit a signed `dek_rotation_commit` event referencing the proposed event; create a **pre-rotation snapshot** automatically (so the rotation is rollback-able to disk); verify the master password a second time before any destructive work; in a single SQLite transaction, walk every per-item DEK in `tbl_article_body` / `tbl_article_version` / `tbl_conflict_version` / `tbl_media` plus the embedding projection matrix, the node's own Ed25519 identity seed, the remote-account bearer tokens, and the node data keys (`tbl_node_data_key` — the key chat.db is sealed under, so AI-chat history, attachments and stored LLM provider keys survive the rotation; chat rows still sealed directly under the Master DEK are moved onto it first, and the rotation refuses to start if any remain), unwrap with the old DEK and re-wrap with the new one (a row already re-wrapped by a peer that raced ahead is recognized and left alone, rather than aborting the whole rotation); delete the `tbl_agent` rows that carry a wrapped Master DEK — superadmin-owned auto-unlock agents, whose wrap is keyed by an API key the server never stores and which a rotation treats as part of the retired key material (every other agent holds no key material and keeps working); re-wrap the initiator's key slot; update the sentinel and the monotonic `dek_epoch`. Atomicity guarantees the database is never left with a mix of old- and new-keyed rows that genuinely needed rewrapping. Then `SwapMasterDek` rolls the in-memory DEK over with a 2-second drain window for in-flight readers.

The HTTP `/accept` returns 202 immediately and the work runs in the background; the UI polls `/progress` for status.

**Peer-acceptance protocol.** After the initiator commits, the rotation event propagates through the existing signed sync stream. Each peer's `tbl_whitelist.auto_accept_dek_rotation` toggle controls behaviour:

- **Auto-accept = true** — the peer applies the rotation autonomously the moment the COMMIT event arrives. Strictly necessary checks (PROPOSED was delivered first, signature verifies against the originator's whitelisted public key, peer is not revoked) gate the auto-apply.
- **Auto-accept = false** — the rotation lands in `tbl_dek_rotation_state` as `Committing`; the Admin UI surfaces a banner with **Apply** / **Reject — leave network** buttons. Reject permanently disconnects this node from the rotated network (its DEK now diverges from peers').

**Lazy slot rewrap.** When a peer auto-applies a rotation, it deliberately preserves user key slots (only `recovery`-type slots are dropped); the initiator-side flow drops every other user slot. On the next login on the peer, `SessionService.UnlockAsync` detects a sentinel mismatch and walks the chain of `Applied` rotations in `tbl_dek_rotation_state` — at each step decrypting the next DEK with the previous one — until the candidate matches the current sentinel. The user's slot is then re-wrapped against the latest DEK using their existing KEK. Transparent: no password re-prompt, no admin intervention.

**Sentinel mismatch does not block sync.** When two nodes' Master DEKs differ (a peer rotated, this node hasn't applied yet), the sync layer logs a warning and continues pulling events anyway — otherwise the COMMIT event that would bring the node back into sync could never be delivered (catch-22).

**Crash recovery.** A startup sweep marks any rotation row stuck in `Committing` from THIS node as `Failed`. Peer-originated `Committing` rows are left in place to be retried by a hook in the next successful unlock (`RetryPendingAutoAcceptsAsync`). `Proposed` rows older than 24h are auto-cancelled.

### What rotation does not protect against

Read this before relying on rotation for the first reason listed above (suspected compromise of the current DEK).

**Fixed for confidential rotations (2026-09-04 onward), still true for older ones.** Before ADR-0006,
an attacker who held the old DEK derived every later one, given any copy of the database taken
after the rotation: the new DEK was wrapped under the *old* DEK and shipped inside the plaintext,
replicated `dek_rotation_commit` payload — so whoever had DEK₁ plus a later `beememorybank.db`, a
local backup, or a pulled page of events could unwrap `encrypted_new_dek` with DEK₁, get DEK₂, then
DEK₃ from the next commit, and so on. That is now closed for a mesh whose nodes are all on a build
with confidential rotation: the new DEK is sealed once per currently-trusted peer with a per-peer
X25519 envelope (see above), so revoking a peer means it holds no envelope it can open for any
*future* rotation. Two things this still does not do: it does not retroactively protect a rotation
that already shipped the old-style commit before this change existed on that mesh, and if the mesh
still contains a peer old enough to need the legacy fallback field, that field is exactly as exposed
for *that* rotation as it always was. Compaction eventually removes old commit rows from the log,
but that is a side effect of an unrelated mechanism, not a security boundary.

If the DEK is genuinely believed to have leaked, the containing action is still revoking the peers
you no longer trust *and* rotating — on a fully-upgraded mesh, revoking removes a peer's ability to
open the new envelope even if it already held the old DEK.

**A rotation is also not safe on a mesh where anyone writes during it** — see the `dek_epoch` note in [docs/encryption.md](docs/encryption.md); a peer that receives a post-rotation article before it applies the rotation cannot afterwards apply that rotation at all.

## Trust Model

Read this before you invite a second node onto your network. See also [docs/sync.md](docs/sync.md#trust-model)
for the same picture from the sync layer's side.

### Joining is authorised by the master password, and a joined node is a superadmin

`POST /api/join` takes the master password, tries it against every password-bearing key slot, and — if it opens a slot belonging to an active superadmin (or a legacy pre-user-table `password` slot) — returns the wrapped Master DEK and adds the caller to `tbl_whitelist`. There is no invite token and no per-node approval step: proving you know the password is the whole gate for mesh *membership*.

A joiner whose join fails after the node answered it can take its row back with `POST /api/join/abort`. It is authorised by the same master password (the same check, so the same slots and the same superadmin rule), is rate-limited like the join, and can remove only the never-synced row of that attempt: the node id with the key the join used, active, recorded within the last hour, with no sync position, never the node's own or a blind id. The removal is a signed `whitelist_revoke` that the mesh applies like an admin's. Anyone holding the master password could already revoke that row as a superadmin, so the route grants nothing new; it only gives the joiner, which has no session, a way to do it for its own failed attempt. It logs an audit entry `join_aborted` without any secret.

The new peer is stored with `is_superadmin = 1`: whoever knows the master password is a superadmin (a deliberate decision). The password already hands over the DEK, so withholding the flag protected nothing and only split the mesh — such a node applied its own hard delete or password-change notice locally while every peer refused the event. A blind node (NodeId with the blind mark) is never a superadmin and is refused by `/api/join`. `EventApplier` consults the flag before applying the cluster-state-modifying event types — `whitelist_add`, `whitelist_revoke`, `whitelist_update`, `hard_delete`, `restore_network` and `master_password_changed` — and rejects one signed by a peer whose flag is unset. Concretely, only a node whose `is_superadmin` bit is set on the *receiving* node's own whitelist row can:

- **Revoke another peer.** A `whitelist_revoke` event it signs is applied by every node that receives it and accepts it. A node never revokes *itself* on a remote event, so the revoked peer keeps its own copy of the vault — but every other node stops accepting its events, which is the same thing as being cut out.
- **Hard-delete content network-wide.** `hard_delete` is not a tombstone: on every peer it physically purges the article (or the whole folder subtree) from the database and deletes the matching `.enc` media files from disk. Nothing is left to restore from except a snapshot taken beforehand.
- **Initiate a network-wide restore.** A `restore_network` event replaces the content of every peer (articles, folders, tags, media; not users, key slots or the node's identity) with the initiator's snapshot. Peers with `tbl_whitelist.auto_accept_restore` set apply it unattended; on every other peer the event remains Pending. There is no UI prompt, notification, or manual approval path: enabling auto-restore for the originator makes the peer apply it on its next sync or unlock. Online DEK rotation propagates through the same peer-acceptance mechanism.

So a device that joined only to read notes can also revoke any other peer, hard-delete content network-wide, or trigger a destructive restore. If that is not wanted for a particular device, demote it (below); the web, CLI and mobile join flows all apply the same rule (`JoinAuthority`).

A node's Ed25519 identity key is pinned to its NodeId at join and there is no way to rotate it: every peer verifies signatures against the public key stored in its own whitelist row, and nothing accepts a replacement for an existing NodeId. So if a node's *identity* key is believed compromised — as distinct from the master password or the DEK — the only remedy is a new NodeId, which means wiping that node and rejoining it. Revoking the old NodeId is what stops the compromised key being useful; it does not give the machine a way back under the same identity.

Authority is granted the same deliberate way it is taken away: `PUT /api/whitelist/{nodeId}/superadmin` (Admin → Trusted Nodes) sets or clears `is_superadmin`, and the flag travels in a `whitelist_update` event so the whole mesh agrees. Demoting removes the three powers above without cutting the peer off from content; promoting gives them back (and is how a peer recorded before that rule gets them). Two limits are worth knowing. Each node enforces the flag from *its own* whitelist row, so a change that reaches only some nodes only takes effect on those nodes — and a node running a build older than this feature ignores the flag entirely and keeps treating every peer as it always has. The affected node itself is not told: it has no whitelist row for itself, so its own UI still offers actions that every other node may now reject (or, for a promotion issued by a node that is not itself superadmin anywhere else, that no other node will ever accept in the first place — see [docs/sync.md](docs/sync.md#trust-model) for which workflows that touches). Revoking (`DELETE /api/whitelist/{nodeId}`) remains the answer for a peer you no longer want syncing at all.

**Bootstrapping a mesh still needs one trust anchor.** A node's own row is never present in its own whitelist, so there is no way for a joiner to *learn* whether the node it just dialed into is itself a superadmin — the join response has nothing to read that from. The CLI, mobile and web-based join flows all record the immediate bootstrap node as a superadmin: it handed over a key slot the master password opens. What the rest of the mesh thinks of the joiner is decided by each node from the bootstrap node's `whitelist_add`, which it accepts only if the bootstrap node is a superadmin in its own rows.

### A node blind copies can call (a hub)

A blind copy never listens; it calls a node whose whitelist row says it may be called and how to trust its TLS endpoint
([ADR 0007](docs/adr/0007-blind-copies-call-a-full-node.md)). Setting that row (Admin → Trusted Nodes → "Let blind copies call
this node") is superadmin-only, refused for agents, needs the master password again and is verified before it is saved: the address
answers as that very node (id and Ed25519 key) and, for a **normal certificate**, the TLS handshake validates through the system's
chain, name included. A **pinned certificate** records the key the node presents on that one connection (trust on first use; the
dialog says so and accepts the expected pin), and warns when a public certificate would break the pin at its next renewal.

What each choice protects against, and what it does not: a pin cannot be satisfied by a certificate a CA issues by mistake or under
duress; a normal certificate can, so anyone a public authority issues the name's certificate to can impersonate the hub's TLS
endpoint to a blind copy. What that gets them is limited: a blind copy authenticates by an Ed25519 challenge and verifies the Ed25519
signature of every package under the key its call code carries, so a man in the middle cannot hand it forged data (it can withhold
it), and what travels is ciphertext plus the plaintext metadata every node holds (ADR 0005; sealed once metadata encryption is
on), never the master key. A stolen
blind-copy token (one hour) can pull exactly that and nothing more; the replica route limits each device to 20 requests per ten
minutes, and revoking the copy in Admin stops its token at once. The blind apps accept a normal-certificate node only over https
to the call code's own origin, with no redirect and no fall-back to http.

### Blind-copy invitations and accounts (2.7)

A friend's blind copy is added by a **one-time invitation** that the node's owner makes on the full node the copy will call (Admin → Blind nodes, "Invite a blind copy"; the
mechanics are in [docs/sync.md](docs/sync.md#invitations-to-a-blind-copy-27), the app's side in [docs/blind-node/ACCOUNTS.md](docs/blind-node/ACCOUNTS.md)). One device
can then hold the copies of several memory banks, one **account** each.

**Who can do what.**

| Who | Can | Cannot |
|---|---|---|
| The node's owner (a superadmin, with the node unlocked) | make, list, withdraw, refuse and confirm invitations; confirming asks for the master password again (5 wrong tries per 5 minutes) | an agent cannot, even one owned by a superadmin (403); a regular user cannot |
| Anyone who holds the string while it is valid | ask to join, as a device of their own choosing; the first to ask binds the invitation to that device (the same device, with the same key, gets the same pending answer if it asks again, but only while the invitation is still `redeemed` and its 72-hour confirmation window is open; afterwards it uses the signed status request) and makes a *pending request* the owner sees | see anything of the memory bank, confirm their own request, or use the invitation for a second device |
| A device the owner confirmed | be a blind copy: sign in with its own key, pull the ciphertext package and take part in sync as a blind peer (never a superadmin); its token lasts an hour and the replica route allows 20 requests per ten minutes | read content, hold the master key, or do what a superadmin does |
| The friend (holds the device) | see what any blind copy sees ([ADR 0009](docs/adr/0009-metadata-confidentiality.md): sizes, counts, timing, node ids, peer names; on a network that has not switched metadata encryption on, titles, paths and tags too); stop syncing; remove the account | alter the copy undetected: events and baselines are signed and a restore checks them |

**What the string protects, and what it does not.** The string is a **one-time code, not an encrypted one**: the only thing the owner and the friend share is the string itself,
so a key inside it would protect nothing. It protects by being short-lived and single-use (15, 30 or 60 minutes, 30 by default; the first redemption burns it, in one conditional
statement), by carrying no vault password, master key or reusable sync token (it does carry the node's public key and a one-time secret), and above all by the **owner's confirmation**: no content and no
usable key of the memory bank reaches the device until the owner has compared a six-character *match code* (worked out from the invitation and the device's own identity, shown on both screens) and
confirmed with the master password. What does reach the full node *before* the confirmation is what the device sends when it asks: its node id and public key, a name, its platform and app version, a
proof of its key, and the key for its own backup, which the node keeps encrypted under its master key until the owner decides and clears in every final state. **Anyone who gets the string
within its minutes can ask to be the friend.** If that is an eavesdropper who redeems first, the real friend is told the invitation is not valid and the owner sees a request whose code the
friend cannot read out: the owner refuses it. If the owner confirms without comparing the code, the eavesdropper's device becomes a blind copy and gets exactly what a friend's copy gets,
until it is revoked; the comparison is the control. Guessing is hopeless (128-bit id, 256-bit secret compared in constant time, one answer for every kind of failure, per-address and
per-invitation budgets, a bounded audit log), and a stolen web session cannot enrol a device, because confirming needs the master password. The routes the device calls are new public
surface: `POST /api/sync/invite/redeem`, `POST /api/sync/invite/status` and `POST /api/sync/leave` are listed one by one in `PublicSurface` and limited for the whole node as well as per caller; the first two take at most 8 KB of strict JSON, answer 503 while the node is locked, and have a reserved lane so that a flood of made-up ids cannot starve a real invitation. A redemption checks the device's signed proof before it looks up the
invitation; a status request reads the one row it needs to find the device's key and then checks the proof before it answers. The invitation routes do not write the secret or the device's backup key into a log line, an
audit line or an answer, and the app does not store the string and empties its box once the computer has answered; this is not a promise about every other error text of the apps (see "Notification texts" below).

**What a hostile string, QR code or link can and cannot do.** The string carries the node's signature over every field (the address, the node's id and key, the trust mode and pin, the
invitation id, the hash of the secret, the expiry and the name), so an edited, cut or spliced string is refused before anything is sent. An `http` address, an address with credentials, a
link-local, unspecified or cloud-metadata address, an unknown version or trust mode, and a string over 2 048 characters are refused too. The app then probes the node at the address (its id and key
must be the invitation's) through the pin or the system's certificate chain *before* it sends the secret. An edited string cannot make the app talk to another host than the one the node signed, and no string can add an account
by itself, or reach any other account on the device (the backup key the app sends belongs to the new account only). What it *can* carry is a whole invitation written by someone else, with their
own node and key: the signature binds the address to the key, not the string to the owner, so a friend who pastes it joins **their** mesh, gives that node its new account's identity, backup key,
device name and address, and receives ciphertext, bounded by the account's limits. Paste only what a person you know sent you. A `bmb-invite:` link (a phone's Camera opens a QR code as one)
goes to the Add screen and nothing more (on Android it is kept in memory only, never logged or stored, for this scheme only and at most 2 048 characters); the screen shows a preview and adds nothing until the person presses Add. The
name in the preview is whatever the string's maker chose; the address beside it is the one that will be called. Android's in-app QR scanner is not in the default build: it pulls in Google
Play services and ML Kit libraries, which include a logging transport, and what that sends has not been checked against the "no telemetry" promise of [PRIVACY.md](PRIVACY.md).

**A removed account is erased on the device.** Removing an account stops its work, tells the source if the device is online, then clears its keys (Keystore alias and file, DPAPI file,
Keychain items), its state and its folder, and takes it off the list last. A removal cut off by a crash is finished at the next start, and a key or a setting that cannot be deleted leaves the
account "being removed" with a sentence, never reported as done. The files are deleted in the ordinary way: the space comes back, the blocks are not overwritten; what they held is ciphertext
whose keys are gone (on a network that has not switched metadata encryption on, also plain titles and paths). Removing an account does not revoke the device on the source: the source is told
and shows "removed from the device by its owner", and revoking stays the owner's act. In the other direction, a 401 or 403 from the source (including one sent by a proxy or firewall in front of
it) is remembered once and the account asks again at its normal rhythm; a second one, at least one retry interval (15 minutes) later and within 24 hours, is shown as "removed by the owner" (a
finished sync round, a finished first load, or a baseline check the source answered - a 304 or an installed baseline - forgets the first, so a proxy that says no for a moment does not end the
account's work; a baseline check of a copy that holds no baseline asks nothing, and a backup does not say whether it asked the source, so neither forgets anything); the copy stays until the person removes it. A refusal that reaches an account the
source has already removed is not remembered, and "Try again" and a new pairing start from nothing. The count is kept in the device's wall time: a clock corrected forward by 15 minutes or more between
two refusals of one burst counts them as two. Every app has a button for it (Reconnect, worded "Try again"), which puts a healthy copy back.

**Accounts are separate; what they share.** Each account has its own folder, database, node identity, keys, pinned HTTP client and log, and its secrets are bound to it (the account's id is in the
Keystore alias and its associated data, the DPAPI entropy and the Keychain account name), so a secret copied into another account's slot does not open. The app looks before it opens and does not follow a link or junction
in its data folder (a swap between the look and the opening is not detected), refuses to run twice on one folder, and in doubt (a damaged list, an unlisted folder) starts nothing and asks for an explicit repair. What a device does share is its own
limits, its clock and its network address: two sources that both see the device can link it by address and timing.

**The limits are a promise to the friend, and what they do not protect against.** The limits (storage, traffic a day, requests a day, unmetered networks only, pause on battery saver, pause by
hand, and the device's own) are enforced by the app on its own network client, before a request goes out and while an answer is read. A limit that cannot be checked (unreadable counts, a folder
that cannot be measured, a network or battery state the device cannot report) pauses the account instead of lifting the limit. They keep **a malicious or careless source** from filling the
friend's disk or data plan, and a paused copy tells the source only the reason and the end time. They are accounting inside the app, not an operating-system quota: **a hostile app, or any process
that can write the app's data folder, can reset the counters** (a missing counts file is read as a fresh install) or use the network on its own; headers and certificates are counted as a fixed
estimate; and a storage limit can be overrun by one batch of local writes. Do not rely on them against someone who controls the device.

**Notification texts and lock screens.** An Android long job's notification names the copy (the name is the source's: a person's or a computer's); its public version, shown instead on a locked screen
whose owner chose to hide sensitive content, says "Blind copies" with no name and no figure. The iPhone "no contact" reminder is a local notification that **names the account**: for an account made from an invitation the title does ("No contact with <name>'s server ..."), for account 1
and for an account paired with codes the title is generic but the text under it still contains the name. Either way the name can be read on the lock screen unless the person hides previews: rename an account whose name
should not be shown. The usual desktop tooltip gives counts without account names ("3 copies - 1 paused"); a running job or a problem can replace or precede that line. On the
owner's side the "paused" and "removed" lines are built by the node from a closed list of reasons, never from text a device sent, and they appear as a banner, not as a notification. The Android
pages and the iPhone app show a failure by its kind or a fixed sentence rather than the text of an exception. The shared code that builds the failure line of an account's card still uses the type and the first 160
characters of the exception's own message; it is written never to hold a secret or a path, but that is not tested for every path.

**A server blind node can join with an invitation (`bmb blind join`).** It sends the full node *its own pair code* in the redemption, and the owner's confirmation seeds it, as pasting that
code would. What that changes, in the table's style:

| Who | Can | Cannot |
|---|---|---|
| The friend who runs the command | read the preview of the invitation first and refuse; read the match code out to the owner; give the join up (`--cancel`) | join without seeing who made the invitation and where it points; learn anything of the memory bank before the owner confirms |
| Whoever holds the invitation text | have a node of theirs join the owner's full node (the same *pending request* as for an app, shown as "Invited (server)") | get the node seeded without the owner's master password; use a pair code of a node that did not sign the redemption (the full node refuses a code naming another node id or key) |
| The full node's owner after confirming | manage that node (reseed, anchors, recovery) - the power the pair code always gave | read its content (a blind node stores ciphertext); seed it twice from one confirmation |
| The full node (holds the pair code) | seed the node while the code lives (up to 73 hours, until a seed spends it or the join ends) | use it afterwards; it is cleared from the row in every final state by a trigger, and the node forgets the secret when it learns the join was refused or expired |

The pair code (which names the node's address, its TLS pin and a one-time secret) is kept on the full node **encrypted under the master DEK, bound to the invitation**, only between the redemption
and the owner's decision. The CLI never reads a key file (the running node holds the keys and does the work) and never prints the invitation, a secret or a key; the invitation given as an argument is visible to
other accounts of the host in the process list and the shell history while it lives (at most an hour), which is why `bmb blind join -` reads it from standard input. The node accepts a join only from its local
console API (internal key, superadmin, and not an agent: a superadmin's agent key is refused as it is on the invitation routes of the full node). The full node commits a redemption before it answers
and the answer can get lost, so the node writes its join record (`blind/join.json`: owner-only, flushed, no secret, never the invitation text) and keeps the pairing secret BEFORE it sends the request,
and forgets the secret only when the full node refuses its first request for good or says to the node's signed status question that it does not know the redemption, when the owner's time runs out, or on `--cancel` (a refused repeat of the command says nothing about the first request and ends nothing): an owner can never confirm a redemption that the node cannot complete, and a repeat of the
same invitation by the same node finds the same redemption. A wipe stops a join that is waiting and refuses a new one until it is over. The invitation remains a bearer capability (whoever holds the text can
have a node of theirs redeem it; the owner's confirmation with the master password and the match code are what stand between that and a seed). Trusting an invitation is trust on paste: one written by someone else points the node at *their* full node, which could then seed it - the preview shows the address and the owner's name,
the command asks before it sends anything, and the node's own owner is the one who has to compare the name with who gave them the line.

**Known limits.** An invitation is added to the device as a pending account before the node has answered, so for a moment a device holds an account with an identity and no data. A device whose clock is
more than ten minutes off cannot learn the owner's decision (its card says so). A full node that is the only computer of its network and has not switched metadata encryption on is not seen as a
superadmin by its copies, which then reject its own whitelist events (a device's confirmation among them) until they are quarantined. A local process that can already write the app's folder is outside what the link checks and the lock stop. The full list is "Known limits" in [docs/blind-node/ACCOUNTS.md](docs/blind-node/ACCOUNTS.md#known-limits).

**A server blind node's two limits.** The server blind node (Docker, service) has no app accounts, no device-wide limit and no invitation service of its own (it can only join one, above). Its owner can cap its stored bytes and its traffic per local day (`BMB_BLIND_MAX_STORED_BYTES`, `BMB_BLIND_MAX_TRAFFIC_PER_DAY`, or the node's local console; empty is no cap, `0` pauses, a traffic cap under 10 MB a day is refused, and a nonsense value stops the node from starting). The record of how they hold is ["Limits of a server blind node"](docs/blind-node/README.md#limits-of-a-server-blind-node-27); what matters for security:

- *Not an operating-system quota.* A process that can write the node's data volume can change data or counters, and an admitted request finishes, so a cap can be passed by the bytes that the requests in flight when it was reached move beyond what was reserved for them.
- *Nobody unauthenticated holds budget.* The reservation (the announced length, or 1 MiB when none was announced) is taken only after the handler has authenticated the caller; no handler reads a body before that, except the three routes with a small JSON body, bounded first (256 KiB for a list of blob hashes, 16 KiB for a restore claim; larger is refused `413`). A client that has not authenticated costs at most 16 KiB of counted traffic per claim attempt.
- *Every request that holds anything has a lifetime:* half an hour plus the time its size needs at 64 KiB/s, counted from authentication. After that it is aborted and keeps counting until it has really ended; a handler still stuck two minutes after the abort is settled without it. A trickling body, or requests that send nothing, hold the room that long at most, and nothing goes on uncounted. A node that is only busy with other transfers answers a one-minute "busy", which a full node shows as busy and not as the owner's pause.
- *Wrong restore codes are the address's, not the codes'.* Five in five minutes from one source address, then `429` with `Retry-After` before the next code is looked at; a right code costs nothing. A stranger cannot revoke or use up the code of a device that is about to restore; a code is 80 random bits. The counts are in memory (a restart forgets them), and behind a proxy that hides the client's address the callers share one budget. The name of a restored computer is at most 200 characters, so the claim stays under its 16 KiB.
- *A paused data route* (reads and writes alike) answers `503` with `X-BMB-Paused` and `Retry-After` and discloses only the closed reason (`disk`, `traffic` or `busy`) and a retry time, never the cap or the measured use. A current full node reads it as a pause (no alarm, no growing back-off); an older one sees an ordinary failing 503 and may report the node as unreachable until the pause ends.
- *The stored size includes what peers make the node stage.* A seed is judged as a whole at its first part against the room that is left, and one that would take the node past its cap is held back as a pause before a byte is staged. A full node does not build and upload a held-back seed again before the time the node named, but it keeps that in memory only: a restart of the full node ends the wait (the cost is one package build and a first part the node refuses before it stages a byte).
- *Fails open on a damaged settings file:* if the node's settings file cannot be read, it is read as the defaults and the limits count as none until the file is repaired.
- *Still true:* the node's older protocol limits (such as 20 package requests per peer per ten minutes) apply, it joins a full node with `bmb blind join` or with the old pair code (one of them at a time), and one container serves one memory bank.

### Metadata encryption (sync protocol 4)

Off until a superadmin switches it on for the whole network (Admin → Metadata encryption, or `bmb sync activate`; the
operator guide is in [docs/sync.md](docs/sync.md#switching-metadata-encryption-on), the cryptography in
[docs/encryption.md](docs/encryption.md#metadata-encryption-sync-protocol-4)).

What it protects against: anyone who sees sync traffic or relays it without the master DEK — a blind node and whoever
operates or seizes its volume (after its first encrypted baseline), a phone or desktop blind copy, a reverse proxy, a
network observer behind a broken TLS — learns no title, path, tag, folder or file name and not the shape of the tree:
every content event travels and is stored sealed (AES-256-GCM under a metadata key that is itself wrapped under the
master DEK), and a tampered or replayed envelope is refused or quarantined, never applied and never read as protocol
3. A node a DEK rotation left out cannot open the metadata key generation that rotation creates, so it reads nothing
written afterwards. The generation is created in the same transaction that swaps the DEK; until its `metadata_key_set`
event is logged the initiator writes no sealed metadata (503, retried), so there is no window in which something is
sealed with the previous generation.

What it does not protect against: anyone holding the master password or the master DEK — every full node opens
everything, and keeps titles in clear in its own database unless the owner has encrypted that node's database (below); the fields that stay readable by
design (outer event ids, node ids, Lamport times, sizes and timing; the payloads of control events, including device
display names; the exact list is `docs/design/protocol4-clear-fields.golden.txt`); copies made before the switch
(backups, snapshots, an old blind copy's database), which the operator guide asks you to retire.

Design points worth knowing: the switch is one way (no deactivation event, and no way back from the node: a backup taken
before it holds the titles in clear, and no restore of the product takes an unencrypted database); a 2.5 node is refused (426) once a node has switched, and a 2.6 node that has not
pulled the switch yet is still accepted only because it shows the capability; the switch re-asks the master password
and is refused for agents and while anything blocks it; a locked full node holds sealed events (it cannot open them)
and serves them onward unchanged, and applies them at unlock in log order with the originator's authority as it was
at receipt, so a revocation that arrived meanwhile cannot split the mesh; while events are held, compaction, a DEK
rotation, a network restore and the switch are refused. Unwrapped metadata keys live only in memory and are wiped at
lock; a node that cannot open any key refuses to write rather than write in clear. No log line carries a title, path,
tag or name from a sealed event (a test scans the logs of the whole sealed path for canaries).

### Encrypted database (2.6.0, opt-in per node)

Off until the owner presses "Encrypt this node" (Admin → Database encryption, or `bmb vault seal`); no update, start, join or restore
encrypts anything by itself. The operator guide is [docs/sealed-database.md](docs/sealed-database.md); the design is
[docs/design/step-b-spec.md](docs/design/step-b-spec.md).

What it protects against: a stolen disk, a copied data folder, a backup of it, or a cloud snapshot of the volume. The vault and chat
databases are encrypted page by page (SQLCipher 4 format, SQLite3 Multiple Ciphers) under keys derived from a per-node database key that
only the master key can unwrap; no table name, title, path, tag, user, agent or chat title is readable in any file the node writes. A locked
node holds no key in memory. The migration verifies the encrypted copy against the original before it swaps, keeps no copy of the
plain database (there is no way back from the node), overwrites and deletes the plain files it replaced, and refuses to start while another plain copy of the vault
lies anywhere in the data folder (found by content, not by name). A node whose folder holds a plain companion beside the encrypted pair
refuses to start and says why. While locked, a node keeps relaying its peers' sealed events from a small plain *boot store* (wrapped keys,
the peer list, opaque events); it applies them at the next unlock.

What it does not protect against: process memory, swap, hibernation files and crash dumps of an **unlocked** node; free-space remnants of
the plain files from before the switch (SSD wear levelling, copy-on-write file systems: use full-disk encryption as well); plaintext
copies the owner made or asked for (exports, downloads, a cold backup, a hard link outside the data folder); file sizes and counts; and
anything that puts a key next to the data. **Auto-unlock does exactly that, by the owner's choice:** on a desktop it uses the OS secret
store (the protection rests on the account password); on a server "Start unlocked after a restart" keeps a key on the server itself,
behind a strong warning and a typed confirmation, so a stolen disk then opens everything. Neither is ever turned on by an upgrade, a
migration, a restore, a default or an environment variable, and a restore starts a server locked.

Backups of an encrypted node are file copies of a **stopped** node's data folder (a running node's database, `-wal`, `-shm` and boot
store change at different moments), taken without `server-auto-unlock.*` and `os-auto-unlock.dat`, which would otherwise carry the key
that opens them. `bmb vault verify` is the check after a restore.

### The master password is the network, not the node

Every consequence above still follows from one credential, just not in a single step anymore. Anyone who has the master password can stand up their own node and join — none of which requires access to any machine you own — and from there read and write every article, exactly as any other synced peer can. Because a join is recorded as a superadmin, they can also revoke your peers, hard-delete your content everywhere, or restore the entire mesh to a snapshot of their choosing. Guard the master password accordingly; if it leaks, change it on every device and revoke any node you do not recognise. `/api/join` is also one of the few endpoints that has to be reachable through a reverse proxy (see [docs/deployment.md](docs/deployment.md)), so it is exposed on purpose.

Treat the master password as a root credential shared by every machine in the mesh, and note two things about changing it. Key slots are node-local — `tbl_key_slot` is neither synced nor included in a join snapshot — so a password change applies to the one node you made it on; every other node still accepts the old password at its own `/api/join`. And changing it does not evict a node that already holds the Master DEK, because the DEK itself is unchanged: that needs a revoke, plus a DEK rotation if the DEK may have leaked.

### "Lock" is advisory

`SessionService.IsUnlocked` is a single process-wide flag: one vault state shared by the web UI and every MCP agent on that node. There is no per-user or per-agent session. **Lock** wipes the Master DEK from memory, but two mechanisms put it back with no human involved:

- **A superadmin-owned agent key.** An agent created by a superadmin stores the Master DEK wrapped with its own API key (`Agent.CanAutoUnlock`); an ordinary user's agent stores no key material and cannot do this. `AgentAuthMiddleware` unwraps it and re-unlocks the process on the *next request that key makes* — with a live MCP client attached, that is usually seconds after the Lock. Two things stop it: revoking the agent (its key no longer resolves to a row at all) or deactivating its owner (the request is rejected with 401 before the unlock is even attempted).
- **OS auto-unlock (Windows, opt-in).** `OsAutoUnlockService` keeps a DPAPI-protected random secret in `<data>/os-auto-unlock.dat` that wraps the Master DEK in an `os_auto_unlock` key slot. It is attempted once, at API startup — it does *not* re-unlock after a Lock inside a running process — so its practical effect is that restarting a locked node unlocks it again. DPAPI's `CurrentUser` scope is an OS-user boundary, not a per-application one.
- **Desktop update handoff (Windows app, automatic).** When the user clicks *Update now*, the app asks its own node (`POST /node/update/unlock-handoff`, internal key, loopback-only front route) to write the Master DEK to `<data>/update-unlock.dat`, DPAPI-protected under the current Windows user with a 10-minute expiry, right before the node stops. The next API start deletes the file first, uses it only if that delete succeeded, the expiry holds and the key verifies against the sentinel, and never reads it again. The window is the minute or two of the restart; anyone able to read the file as that user could equally have read the key from the running, unlocked process a moment earlier. A handoff that is never consumed stays DPAPI-protected, expires, and is deleted on the next start.

Lock is therefore a real operation with a short half-life unless you also remove what can undo it: revoke or delete the superadmin-owned agent keys, and disable OS auto-unlock if a restart must stay locked.

**Lock on sleep (desktop app, optional, off by default).** The setting "Lock the vault when this computer sleeps" in the desktop app's Settings window (Power) is off until the person turns it on; it is read at each sleep, so it takes effect at once, and with it off a sleep sends nothing. With it on, the desktop app locks the node it started when the computer goes to sleep (Windows and macOS), and the person signs in again after waking up. The shell calls `POST /node/lock` on its node's front — loopback only (an off-machine caller gets 404), and only with the node's internal key in `X-Internal-Key` — and the front calls the Api's `POST /api/session/lock` as the node itself (internal key and superadmin role, bounded to a few seconds). It is the same Lock as the button, with the same limits: a superadmin-owned agent key re-unlocks the process on its next request, and a node the app merely attached to (one it did not start, so it holds no key for) is not locked by it.

**Blind-node alarms (desktop app).** Once a minute the desktop app asks the node it started `GET /node/alarms`, with the same gates as `POST /node/lock` (loopback only, 404 otherwise; the node's internal key, 403 without it); the front calls the Api's `GET /api/blind-nodes/alarms` as the node itself (internal key and superadmin role, bounded to a few seconds). The answer holds alarm kinds, blind-node ids, times and protocol numbers, and a blind node's display name only while the vault is unlocked; no free text, so nothing a remote node says reaches a notification. The texts of the notices are fixed in the app.

## Reporting a Vulnerability

We take security vulnerabilities seriously. If you discover a vulnerability in BeeMemoryBank, please report it responsibly.

### How to Report

Send an email to **universeissilent42@gmail.com** with:

1. A description of the vulnerability
2. Steps to reproduce (if applicable)
3. The potential impact
4. Any suggested mitigations

### Response Timeline

- **Acknowledgment:** Within 48 hours
- **Initial assessment:** Within 5 business days
- **Status updates:** Every 7 days until resolution

### Responsible Disclosure Policy

- Do not publicly disclose the vulnerability until a fix has been released
- We will credit researchers who report vulnerabilities (unless you prefer to remain anonymous)
- We ask that you:
  - Avoid accessing or modifying other users' data
  - Do not degrade service availability
  - Provide reasonable time for us to address the issue before any public disclosure

### Supported Versions

| Version | Supported |
|---|---|
| Latest release | Yes |
| Development branch | Best effort |

### Docker images (supply chain)

- `ghcr.io/ultrathinker/beememorybank` and `ghcr.io/ultrathinker/beememorybank-blind` are built by
  `.github/workflows/docker-publish.yml` on GitHub Actions, never on a developer machine, and only when a maintainer
  publishes a release (the same approval the Windows installer has). Each architecture's image is started and checked
  (`scripts/smoke-docker.sh`) before any tag is written; a failed check leaves no `X.Y.Z`, `X.Y` or `latest` behind.
- Each published image carries a GitHub-signed build provenance attestation:
  `gh attestation verify oci://ghcr.io/ultrathinker/beememorybank:X.Y.Z --owner ultrathinker`. Every action the workflow
  uses is pinned by commit, it runs only in this repository (a fork's run does nothing), and only the jobs that can push
  hold `packages: write` (every step that pushes runs for a release tag only, and there is no pull-request trigger).
- What the build downloads is checked: the embedding model against the SHA-256 the node itself verifies
  (`ADD --checksum`), restic against its release checksum. The base images are Microsoft's `dotnet/aspnet:10.0` and
  `dotnet/sdk:10.0`, kept current by Dependabot.
- Nothing secret is in a layer: the internal key, the console password and the restic password are created in the data
  volume at the first start. The containers run as root for now; the images declare only the Web port (5301) and the
  blind node's sync port (5610) as exposed.

## Uploaded Images

A picture is decoded by the server (to re-encode it as JPEG, scale it down, or shrink it for an AI agent), so a hostile file is input to an image decoder.
The image library is SkiaSharp ([ADR-0008](docs/adr/0008-image-library-skiasharp.md); notices in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)). What the transcoder
does before and around it:

- **Only five formats reach a decoder**: JPEG, PNG, GIF, WebP and BMP, recognised by their signature. Anything else (TIFF, RAW/DNG, ICO, ...) is refused before the library sees it.
- **Decompression bombs are refused from the header**: a file over 64 MB, or a picture of more than 100 million pixels, is a client error (HTTP 400) before any pixel is decoded or
  allocated. A truncated or damaged file is refused, never half-decoded.
- **SVG is never rasterised by the server**; it is stored as uploaded and served with the restrictive user-content response headers of `UserContentResponseHeaders`.
- The decoder is native code inside the Api process. A weakness in it would be a weakness of that process, so the library is kept on a maintained line and checked with
  `dotnet list package --vulnerable --include-transitive`; the component versions bundled in its native library are listed in the ADR.

## Folder-Level Access Control

BeeMemoryBank implements per-folder access control lists (ACLs) that restrict which users and AI agents can read, write, or manage articles within specific folders.

- **Per-folder ACL** — each folder has an independent access list for users and agents
- **Horizontal privilege escalation prevention** — users cannot access folders they are not explicitly granted permission to, even if they manipulate API requests
- **Agent isolation** — AI agents are scoped to their assigned folders and cannot traverse the full tree
- **Server-side enforcement** — ACL checks are performed at the API layer before any data is returned or modified, regardless of client-side UI

## Agent Privilege Escalation Prevention

AI agents authenticated via bearer tokens cannot elevate their privileges through request header manipulation or parameter injection.

- **Role scoping enforced server-side** — agent roles are resolved from the authenticated token, not from request headers
- **No header spoofing** — the API ignores any role or permission headers sent by clients, relying solely on the authenticated session
- **Folder-scoped operations** — agents can only operate on folders explicitly granted to them
- **Audit trail** — all agent operations are logged with the agent identity for post-hoc review

## XSS Prevention

All user-generated content rendered in the Web UI is sanitized to prevent cross-site scripting (XSS) attacks.

- **DOMPurify** — client-side sanitization applied to all Markdown-rendered HTML content
- **Server-side validation** — input validation rejects obviously malicious payloads before storage
- **Defense in depth** — even if an attacker stores malicious script tags, DOMPurify strips them before rendering

## Constant-Time Key Comparison

Internal key validation uses constant-time comparison to prevent timing side-channel attacks.

- **FixedTimeEquals** — used for all internal API key comparisons, preventing attackers from inferring key values through response timing analysis
- **Applied to** — agent bearer tokens, internal Web↔API shared secret (`BMB_INTERNAL_KEY`)

## Web↔API Internal Authentication

The Web server communicates with the API server over a trusted internal network. Admin endpoints (user management, lock/unlock) require a shared secret key (`BMB_INTERNAL_KEY`) to prevent spoofing.

- Set `BMB_INTERNAL_KEY` to the same random value on both the API and Web systemd services
- Generate with: `openssl rand -hex 32`
- If unset, admin endpoints only accept requests from `127.0.0.1` / `::1` (safe only if API is not reachable from outside)

The Web server also requires cookie authentication with the `superadmin` role for all admin pages and proxy routes — the internal key is an additional layer, not a replacement.

## Security Best Practices for Deployments

- Use HTTPS in production (reverse proxy with TLS termination)
- Set strong Argon2id parameters (defaults follow OWASP recommendations)
- Keep your Master DEK recovery mechanism secure
- Regularly update dependencies
- Use LUKS or equivalent full-disk encryption on the host machine
- Set `BMB_INTERNAL_KEY` in production (see above)
