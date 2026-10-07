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
