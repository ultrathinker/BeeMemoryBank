# Forgot the password? Use a hint or a recovery key

Bee Memory Bank has **no e-mail recovery**. So a person must always have two ways back in: a **password hint**
they wrote themselves, and a **recovery key** kept offline.

## The password hint

Every place that creates or replaces a password asks for a hint (1 to 120 characters; it may not contain the
password, the username, or a line break). It is stored in the node's local user table in the clear, because the
Sign In page has to show it **before the vault is unlocked**; it is never synchronized, never written to an audit
line or a log, and the policy is the same in the web forms, the HTTP endpoints, the CLI and the phone apps.

On the Sign In page, **Forgot your password?** opens two choices: **Show my password hint** (asks for the
username) and **Use a recovery key** (below). The hint lookup is anonymous and node-local:
`POST /api/session/password-hint`, 10 attempts per 15 minutes per client address, answered through the Web page
only. An unknown username and a user without a hint get the same status, shape and sentence:
*No hint is available for this account. Use a recovery key, or ask an administrator to reset the password.*
There is deliberately no per-username counter: one lookup per name is all an enumerator needs, so it would stop
nobody, while it would let anyone spend a real person's budget and keep that person from seeing their own hint.

An encrypted node is **closed** after every restart, which is exactly when a password is forgotten, so the hint must
be readable before anything is unlocked. A derived copy of the hints is kept in `beememorybank.hints.json` beside the
boot store: the database stays authoritative and the file is rewritten from the user table at every unlock and lock,
and after every change (a user without a hint is absent; a deleted user disappears at the next rewrite). It does not
list user names - each entry is an HMAC-SHA-256 of the lower-cased name under a random salt stored in the file - but
the **hint text is in clear**. Anyone who can read the node's data folder can read every hint, which is why the forms
say never to put a secret in one. The file is not carried by a backup, a snapshot or a join archive; a restored
database rebuilds it at its first unlock.

The lookup still tells an observer that a given username HAS a hint (rate limited). The product accepts that:
mandatory hints are an explicit decision. Accounts created before hints existed have none; the Profile page offers
a soft **Add a password hint**, and nothing is forced on them until their next password change.

**A new node.** After the first administrator is created by `/Setup` (or by `bmb init` / `bmb join` on the command
line with its confirmation), the person is shown a recovery key **before the app** and must tick *I saved my
recovery key*. `bmb init` and `bmb join` finish the step with `--i-saved-the-key` or a typed `yes`; an unattended
run with redirected stdin cannot answer, so the node stays gated, the first web sign-in shows a new key (a second
recovery slot) and asks again. The step cannot be skipped by typing another address: while it is pending, every
Web page of a superadmin session is sent back to it. The API, MCP and agents keep working, so an unattended node
is never stuck.
A node that existed before this release is not gated; its Admin pages and the tree show a dismissible notice until
a recovery key is issued.

## The recovery key

A **recovery key** is a second way into the vault's master key, issued by a superadmin **for themself** and kept
offline (Admin → Recovery keys → **Issue new recovery key**). It is stored as a key slot of type `recovery`; only a
key derived from it is in the database, never the key itself. Since 2.12 a recovery key is **personal**: it belongs to
the superadmin who issued it (the slot records the owner) and resets only that person's password. A key issued before
2.12 has no owner on record; see [keys with no owner](#keys-with-no-owner) below.

If the only administrator forgets their password there used to be no route back in from the browser: the Sign
In page took a username and a password, changing a password needs the old one (or another superadmin), and the
recovery key was accepted only by the `bmb` command line and by confirmation boxes. Now there is one.

## What you do

**In the browser.** On the Sign In page choose **Forgot your password? Use a recovery key** (`/RecoverAccess`),
then enter

1. the **username** of the administrator account — for a key issued since 2.12, the account the key belongs to,
2. a **recovery key** of this node,
3. a **new password**, twice (the usual rules: at least 8 characters, an upper-case letter, a lower-case letter
   and a digit),
4. a **password hint** for the new password (mandatory, same rules as everywhere).

On success the page says *Password changed*; sign in with the new password as usual. Signing in is what
unlocks the vault — the page itself signs nobody in and does not unlock anything.

**On the command line** (on the machine that holds the data directory):

```bash
# the key on the first line of stdin, the new password on the second — never as arguments
printf '%s\n%s\n' "$RECOVERY_KEY" "$NEW_PASSWORD" | bmb user reset-password --user admin --recovery-key-stdin --data /var/lib/beememorybank --hint "the blue folder by the window"
# or, in a terminal, with no echo:
bmb user reset-password --user admin
```

Afterwards you will be asked (a banner after the first sign-in) to consider issuing a new recovery key. Older
recovery keys keep working; make a new one if a card or paper copy was lost or seen by someone else.

## What it will and will not do

- **Only superadmin accounts.** A recovery key is the owner's tool. Naming an ordinary user, an unknown user or
  a deactivated one is refused exactly like a wrong key.
- **Only the key's owner (since 2.12).** A key issued since 2.12 resets only the password of the superadmin it
  belongs to. Naming another superadmin is refused exactly like a wrong key, after the same work — the answer is no
  oracle for who owns which key. A key issued **before 2.12** has no owner on record and keeps the older rule: it
  resets any active superadmin's password.
- **Only this node's keys.** A recovery key from another node's database does not work; neither does a password
  offered as a "recovery key".
- **Only the one account changes.** Other superadmins' passwords and key slots, and every recovery key, are left
  as they were. All web sessions of the reset user end (their security stamp is replaced), and their remote API
  tokens are revoked.
- **The vault is not opened.** The master key the recovery key unwraps goes into the user's new key slot and is
  wiped; the node stays locked if it was locked, and open if it was open.
- **No hint.** Every refusal is the same sentence — *The username or the recovery key is not correct.* — after the
  same amount of password-hashing work, so it cannot be used to find out which usernames exist or which accounts
  are administrators.
- **Replicated superadmins on shared nodes ([ADR 0014](adr/0014-people-and-shared-nodes.md)).** On a shared non-home
  node whose accounts live on an **accounts node**, verifying a *wrong* recovery key still gives the exact generic
  refusal above (preserving no-oracle parity). Once the recovery key itself proves valid, any reset targeting a
  replicated superadmin (`replicated = 1`) is refused — HTTP 409 from `POST /api/session/recover-access`, exit code 1
  from `bmb user reset-password` — naming the accounts node so the user resets their password there instead;
  the subsequent `superadmin_doors_set` event pushes the updated public door to all shared nodes.

## Listing and revoking keys

Admin → **Recovery keys** lists every recovery key of the node: its owner (or "owner unknown" for a key issued before
2.12), when it was issued, and the label its owner gave it. The key itself is never shown again — it is not stored in
recoverable form. A superadmin sees all keys.

- **Issue new recovery key** makes a key for the signed-in superadmin, with an optional label ("the card in the safe").
  It is shown once; copy it before closing the window.
- **Revoke** (the bin on a row) stops that key working at once — it can no longer reset a password or open the vault —
  and cannot be undone. Revoking another person's key asks for a confirmation that names them.
- **Revoke all keys with unknown owner** revokes every pre-2.12 key whose owner is unknown (below).

A revoke is refused if it would leave the node with **no door a person can open**: there must remain another recovery
key, the legacy password, or an active superadmin's password. Auto-unlock doors (OS, server, quick unlock) are machine
secrets and do not count. The same rule holds for deleting a slot of any other kind (`KeyManagementService.RemoveSlotAsync`, the
phones' slot cards): a superadmin's own password door cannot go while only machine doors would be left. The API is `GET /api/keys/recovery-list`, `POST /api/keys/recovery-revoke` and
`POST /api/keys/recovery-revoke-unknown` (superadmin only). Issuing and revoking are audited (`recovery_key_issued` /
`recovery_key_revoked`) with the slot, the owner and the actor — never the key.

### Keys with no owner

A key issued before 2.12 has no owner on record, so nobody can say whose it is; anyone holding it can reset any
superadmin's password. These keys, together with the un-migrated legacy `password` slot and agents whose owner is
missing, form the **unknown-owner set**. A removal revokes them by default (`UnknownOwnerDoors`); the Recovery keys page
offers the same "revoke all keys with unknown owner" action. The synthetic "admin" account a migrated vault gets is an
ordinary person (its slot and agents have an owner), so it is never in this set.

Demoting or deleting a superadmin revokes that person's recovery keys along with their other doors — a recovery key is
personal and goes with the person.

## When a superadmin is removed

The removal of a superadmin (2.12, ADR 0014) takes their recovery keys away in its first step, together with their password door:
from that moment their key opens nothing and resets no password, on the Sign In page and with `bmb user reset-password` alike, and
an account that is "being removed" is refused a reset even with a key it never owned. The recovery keys of everybody who stays keep
working through the removal's master-key change (a sealed recovery door is re-sealed by the rotation). Ownerless keys issued before
2.12 are in the unknown-owner set the removal's review lists, pre-selected "remove".

On shared nodes the person's account is a mirror of the accounts node's list. From the first step the mirror is "being removed" too
(the person cannot sign in there and cannot join a device); once the shared node applies the list without them (step S2) the mirror
and its door are gone. A recovery key never resets a mirror's password (that is done on the accounts node). If the accounts node is
lost and a superadmin takes the removal over on a shared node, their mirror there becomes an account of their own, with the password
they typed to take it over; their recovery keys are unchanged.

## Limits and records

| | |
|---|---|
| Per client address | 5 attempts per 15 minutes at the Web layer (its own budget, not the sign-in one); a successful reset clears it |
| Per username | 5 attempts per 15 minutes at the node, counted for names that do not exist exactly as for those that do |
| Typos | A new password that breaks the rules, a missing or invalid hint, or two passwords that do not match, is said at once and costs no attempt |
| Audit log | `user_password_recovery_reset` (user, client address) and `user_password_recovery_refused` (reason class `key` or `account`, address) — never the key, a password, a hint or a name that is not an account |
| Other nodes | Key slots are node-local, so other **personal** nodes still accept the old password; a `master_password_changed` event tells them, as the Admin → Master Password page does. A **shared** node is different: the reset happens on the accounts node and the updated door reaches every shared node through `superadmin_doors_set` (see the replicated-superadmin rule above) |

The per-username budget means that someone who knows an administrator's username can keep that account's
recovery locked for a while by guessing; they gain nothing else by it, and the key itself — 256 random bits — is
not guessable at this rate. The owner can always use the command line on the host.

If the vault was **locked** when the password was reset, the `master_password_changed` event cannot be signed
(the node's identity key is sealed under the master key). It is announced at the next successful sign-in. If the
node restarts before anyone signs in, that one announcement is skipped; change the password on the other nodes
by hand if you want them to follow.

## After sealed metadata is turned on

Turning on sealed metadata ([encryption.md](encryption.md)) adds a **metadata key** that seals titles, folder paths, tags and comments
in the events and in the baselines nodes hand to blind copies. It changes nothing about forgotten passwords:

- **Changing or resetting a password needs nothing more.** The metadata keys are wrapped under the master key, not under any password, so a
  reset that re-wraps the master key under the new password leaves them as they were.
- **A restore recovers them from the same material.** Recovery boxes and the links to retired master keys open the master key and the
  chain of older ones; each metadata key row then opens with the master key of its fingerprint. Every package, backup and join carries all
  the metadata key rows in the clear control tables (they open only with the master key), so no extra step and no extra secret is needed
  after a master key rotation: the new rotation makes a new metadata key and the old ones stay readable.
- **If every slot, recovery box, link and authorised peer is lost,** the metadata is unrecoverable, exactly as the bodies are. A blind node,
  a phone backup or a backup folder cannot open it for you: they hold the baseline sealed under keys only the master key opens.

How a restore from a blind node, a phone backup or a backup folder uses them is described in
[snapshot-restore.md](snapshot-restore.md#restore-from-a-baseline-and-the-deltas-after-it).

## On an encrypted node

When the owner has used **Encrypt this node** ([encryption.md](encryption.md#an-encrypted-node-the-database-key-and-the-boot-store)), the database
file is encrypted and the ways into it live twice: in the database, and as copies in the small `beememorybank.boot.db` file (the
*boot store*) that a locked node reads before anyone has unlocked it.

- **Forgotten password: nothing changes for you.** The recovery key is one of the ways in (a `recovery` slot, copied to the boot
  store like the others). *Forgot your password? Use a recovery key* works on a locked node: the page opens the database for the
  moment it needs, with the key the recovery key yields, writes the new password's slot, rewrites the boot store, and closes it
  again. The node stays locked if it was locked, and open if it was open. Signing in with the new password is what unlocks it.
- **Lost boot store, database still there.** The encrypted database cannot be opened without the boot store, and the node says so
  and refuses to start rather than guess. Ways back, in this order of preference: restore the newest **snapshot** (a snapshot opens
  with your master password or recovery key and does not depend on the boot store), join again from a peer that has the data, or
  restore from a blind copy. Prevent it: every product backup path (snapshots, the update's pre-update copy) already includes the
  boot store, and a copy of the data folder must contain it too ([deployment.md](deployment.md#backing-up-and-restoring-the-data-folder)).
- **Lost everything but a recovery box on a blind node.** The restore described in [snapshot-restore.md](snapshot-restore.md) builds a
  new node, which can be encrypted again afterwards.
- **No back door.** Without a password, a recovery key, an auto-unlock the owner switched on, a peer or a recovery box, the data is
  gone, exactly as the bodies already were. A server that restarts stays locked until one of those is used, unless the owner
  switched on *Start unlocked after a restart* ([deployment.md](deployment.md#start-unlocked-after-a-restart-servers)); a restore
  never switches that on.

## How it is built

- `POST /api/session/recover-access` on the API, called only by the Web page (it requires the internal key and is
  **not** in `PublicSurface`: a keyless caller gets 404, like `/api/session/unlock`). A blind node does not map it.
- `SessionService.TryOpenWithRecoveryKeyAsync` opens only `recovery` slots, checks the sentinel, and returns the
  master key together with the **owner of the slot that opened** (`RecoveryKeyOpen`), *without* touching the session.
  The owner is read from the database row, which is authoritative even when the door came from the boot store of a
  closed node. `UserService.ResetPasswordWithRecoveryKeyAsync` then reuses the admin-reset path
  (`ReplacePasswordAsync`: login hash, key slot, security stamp, remote tokens) with that key — but only when the named
  account is the key's owner (or the key has no owner, a pre-2.12 key).
- The recovery key is verified first and always at full cost; a valid key with an ineligible name — including another
  superadmin who is not the key's owner — pays the cost of a real reset too.
- Issuing (`KeyManagementService.IssueRecoveryKeyAsync`) records the owner and creator (the issuing superadmin) and an
  optional label; only an active superadmin qualifies. Listing and revoking are `ListRecoveryKeysAsync`,
  `RemoveSlotAsync` and `RevokeUnknownOwnerRecoveryKeysAsync`, guarded by `SlotDoors.EnsureAPersonDoorRemainsAsync`.
- Throttling: `PublicRateLimitMiddleware` (per address, route class `RecoverAccess`) and `RecoveryAccessState`
  (per address and per username, plus the pending announcement).
