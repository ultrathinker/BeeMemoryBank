# Forgot the password? Use a recovery key

A **recovery key** is a second way into the vault's master key, issued once by a superadmin and kept offline
(Admin → Security → **Issue new recovery key**). It is stored as a key slot of type `recovery`; only a key
derived from it is in the database, never the key itself.

If the only administrator forgets their password there used to be no route back in from the browser: the Sign
In page took a username and a password, changing a password needs the old one (or another superadmin), and the
recovery key was accepted only by the `bmb` command line and by confirmation boxes. Now there is one.

## What you do

**In the browser.** On the Sign In page choose **Forgot your password? Use a recovery key** (`/RecoverAccess`),
then enter

1. the **username** of the administrator account,
2. a **recovery key** of this node,
3. a **new password**, twice (the usual rules: at least 8 characters, an upper-case letter, a lower-case letter
   and a digit).

On success the page says *Password changed*; sign in with the new password as usual. Signing in is what
unlocks the vault — the page itself signs nobody in and does not unlock anything.

**On the command line** (on the machine that holds the data directory):

```bash
# the key on the first line of stdin, the new password on the second — never as arguments
printf '%s\n%s\n' "$RECOVERY_KEY" "$NEW_PASSWORD" | bmb user reset-password --user admin --recovery-key-stdin --data /var/lib/beememorybank
# or, in a terminal, with no echo:
bmb user reset-password --user admin
```

Afterwards you will be asked (a banner after the first sign-in) to consider issuing a new recovery key. Older
recovery keys keep working; make a new one if a card or paper copy was lost or seen by someone else.

## What it will and will not do

- **Only superadmin accounts.** A recovery key is the owner's tool. Naming an ordinary user, an unknown user or
  a deactivated one is refused exactly like a wrong key.
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

## Limits and records

| | |
|---|---|
| Per client address | 5 attempts per 15 minutes at the Web layer (its own budget, not the sign-in one); a successful reset clears it |
| Per username | 5 attempts per 15 minutes at the node, counted for names that do not exist exactly as for those that do |
| Typos | A new password that breaks the rules, or two that do not match, is said at once and costs no attempt |
| Audit log | `user_password_recovery_reset` (user, client address) and `user_password_recovery_refused` (reason class `key` or `account`, address) — never the key, a password or a name that is not an account |
| Other nodes | Key slots are node-local, so other nodes still accept the old password. A `master_password_changed` event tells them, as the Admin → Security card does |

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
  master key *without* touching the session. `UserService.ResetPasswordWithRecoveryKeyAsync` then reuses the
  admin-reset path (`ReplacePasswordAsync`: login hash, key slot, security stamp, remote tokens) with that key.
- The recovery key is verified first and always at full cost; a valid key with an ineligible name pays the cost
  of a real reset too.
- Throttling: `PublicRateLimitMiddleware` (per address, route class `RecoverAccess`) and `RecoveryAccessState`
  (per address and per username, plus the pending announcement).
