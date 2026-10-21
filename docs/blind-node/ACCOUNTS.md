# Blind app: several accounts and limits

A device that runs the blind app (`libs/BeeMemoryBank.Blind.AppCore` and its heads) can hold the encrypted copies of **several
meshes**, one **account** per source mesh. An account is the single-mesh blind app of 2.6, built once more: its own folder,
database, node identity, keys, state, log and pinned HTTP client. Nothing of one account is read or written by another; the only
things a device shares are the list of accounts and the lock on the data folder, the time, the network address, the sync loop, the device-wide limits and the one-at-a-time gate of heavy work.

## The words used here

- **Account**: one copy of one memory bank on this device, with everything of its own (folder, database, node identity, keys, state, log). Account 1 is a copy that was on the device before 2.7.
- **Source**: the memory bank an account is a copy of, and the full node it calls. **Hub** says the same when the full node's side is meant (it makes the invitation and answers the device). **Owner**: the person who runs the source. **Friend**: the person who holds the device.
- **Invitation**: the one-time line the owner makes on the hub (`docs/sync.md`, "Invitations to a blind copy"). **Match code**: the six-character code both screens show; the owner confirms it.
- **Paused**: the guard says "not now" for a reason: a limit that is reached, a limit of 0, the person's own pause, a device that cannot be measured or read. **Waiting** is the same for the network (not unmetered) and the battery (saving power); the screens word a pause as "Paused: ..." and a wait as "Waiting ...". Neither is a failure.

## Layout and the registry

```
<app data>/
  accounts.json            the registry (version 1): what accounts exist, their names, limits and switches. No secret.
  accounts.lock            empty; held open by the running app (see "One owner of the folder")
  device-usage.json        the device-wide day of the usage count (a high-water date; see Limits)
  accounts/<32 hex id>/    one folder per added account: beememorybank.db, usage.json, baselines/, log ...
  beememorybank.db ...     the 2.6 files, if the install is older: the account with the id "legacy", adopted where it is
```

* The account **id** is `legacy` or 32 lower-case hex characters, and nothing else is accepted: it becomes a folder name, so a
  registry that names anything else (`..`, a path, a drive) is refused as damaged, never followed.
* The **legacy account** is the 2.6 install in place: the first start writes `accounts.json` with one atomic write and moves no
  file. Its keys keep the names they always had (Android aliases, DPAPI entropy, Keychain accounts, the `bmb_blind_state`
  preferences), so an upgrade and a rollback to 2.6 both find them.
* **Fail closed.** A registry that cannot be read, or a missing one beside an `accounts/` folder, is *damaged*: the app opens on a
  sentence that says so, starts no account, writes nothing and refuses to change the registry. It never "starts empty" over
  folders that hold somebody's copy. A new account needs an intact registry.
* At most **8** accounts per device.

### Links

An id that is valid as a folder name says nothing about where the folder really is. Before anything is created, opened or composed,
the app looks (without following) at the data root, `accounts/`, the account's folder and `accounts.json`: none may be a symbolic
link, a junction or any other reparse point, a dangling one included, and none may be a file where a folder belongs. A root, an
`accounts/` or a registry file that is a link is a *damaged registry* (the app opens on a sentence, nothing is touched); an account
folder that is a link is a *repair case for that account only* ("Needs repair": no runtime, nothing opened through the link, the
link is left for a person). Only the root itself and what is below it are judged; the folders above the root are the host's choice.
The check and the use are not one atomic step: it stops mistakes (a restore, a copy, a tool), not a process that can already write
the app's folder.

**A shallow look inside the account's folder.** Before an account is composed, the immediate children the app opens by their name are looked
at, again without following: the database and the `-wal`, `-shm` and `-journal` files beside it, the state file, the log, the usage file, and the
roots of the media, the replica work, the backups and the baselines. One of them that is a link or a reparse point puts that account (only) in
repair (`unsafe-path`, a sentence that names the kind of thing, never a path) and no runtime is composed. This is as deep as it goes: what is
below those roots, and a swap between the look and the use, are not checked (see "Known limits").

### One owner of the folder

`accounts.lock` is held open (no sharing) for as long as a host lives. A second host on the same root, in another process or in the
same one, is refused with a sentence ("Another copy of this app is already using this data folder...") and composes nothing; it may
try again when the first is gone. The operating system lets go of the lock when a process dies, so there is no stale lock. A registry
used without a host takes the same lock for one update, and every update starts from the file on disk, so two registries never lose
each other's change. The registry, the state file and the log are written through a temporary file with a name of its own each time.

**The root is given back only when nothing can still use it.** Every start, add, repair, adoption, forget and removal, and the rebuild of a
runtime after a wipe, is an *operation* the host counts. Disposing the host waits (bounded) for them and for the runtimes' own work
(`BlindActivity`, the first load, the opening of the database). If all of it has ended, the runtimes are disposed and the lock and the registry
lease are released. If something has NOT ended (a thread cannot be killed: a job that ignores its token, a hook that never answers), the host
does **not** dispose a provider under it and does **not** release the lease: `Dispose` returns after its patience, `ShutdownIncomplete` is true,
`ShutdownFault` says what is still running ("the app is shutting down; ... still running"), `Changed` is raised, a second host on the same
root is refused as before, and the shutdown finishes by itself (the runtimes are disposed, the lease released, the fault cleared) when the
last operation ends. A closed host starts nothing: `StartAsync`, `AddAsync` and the other operations throw `ObjectDisposedException`, and a
wipe that finishes after the close composes no new runtime.

### The registry against the disk: repair state

At the start the app compares the list with what is on disk. These differences are a **repair state**: nothing is started or changed,
`snapshot.RepairIssues` lists each difference with a sentence for the screen, and only an explicit call on
`IBlindAccountsController` settles it, each one a single atomic write:

| Difference | Kind | Settled by |
|------------|------|-----------|
| a 2.6 copy in the root (identity in its state, or a database) that the list omits | `legacy-root-not-listed` | `AdoptLegacyRootAsync()`: lists it first, starts it, moves nothing |
| a folder under `accounts/` named like an account that the list does not name | `folder-not-listed` | `AdoptFolderAsync(id, name)`: lists it and starts it (refused for a link) |
| a link named like an account under `accounts/`, or (for a listed account) a link where the app opens a file or folder by name inside it | `unsafe-path` | by hand: remove the link |
| a regular file (or anything else that is not a folder or a link) that holds an account's name under `accounts/`, which the list does not name | `file-in-place-of-folder` | by hand: move the file away (it cannot be adopted) |
| a listed account that was set up and whose folder is gone | `folder-missing` | `ForgetMissingAccountAsync(id)`: takes the entry out, then clears the keys and state it kept (refused if the folder is back) |

The first three global rows stop every account from starting (the list cannot be trusted to be the whole truth); so does a file in the place of
a folder. The `folder-missing` row, and a LISTED account whose own folder or inner file is a link (`unsafe-path` for that account), concern one
account and leave the others running. Names under `accounts/` that this app never gives an account (`notes`, `desktop.ini`) are
nobody's business. **`established`** is written in an account's entry as soon as its folder exists, BEFORE its identity and keys are made: from then on a lost folder is a
`folder-missing` case, never a fresh account made over the keys it holds; an entry that never got that far has nothing to lose and
its folder is simply made. If that write fails, the runtime is discarded and the account shows "It could not be started"; the folder is
never made again on the strength of an entry that could not say it was set up. Independently of the marker, an account that holds an
identity trace (a node id in its state, a backup key or a pairing secret, or a state store that cannot be read) and whose folder is gone is
`folder-missing` too. The price: an account whose first start fails after the folder exists is on record as set up, so a folder lost
then needs "forget" instead of a silent new start. A legacy database that cannot be opened (locked, access denied) stops the adoption instead of reading as
absent: nothing is written until it can be looked at.

## Keys and state are bound to the account

Every platform store is built per account by a factory (`BlindStoreFactories`) from `BlindAccountNames`. A non-legacy account's
names carry its id: in the Android Keystore alias, the AES-GCM associated data and the preferences file name, in the DPAPI entropy,
in the Keychain account name; so a stored secret copied into another account's slot does not open. `tests/shared/BlindStoreContracts.cs`
(`BlindSecretStoreContract`, `BlindStateStoreContract`) is the contract every head runs against its own stores: an account's
secrets and state are invisible to every other account, survive a new instance, and removal deletes exactly its own.

## Removing an account

The sequence is idempotent at every step and resumes at the next start if the process died in it:

1. mark the entry `deleting` (the app now shows "Removing this account..." and starts nothing for it);
2. wait for the account's running work (`BlindActivity`) to end;
3. a courtesy "leave" call to the source (bounded; the source also learns it from the missing sync). `IBlindAccountLeaveHook.LeaveAsync`
   must be **idempotent at the source**: a crash after this step and before the entry is removed makes the next start call it again;
4. close the account's runtime and its own SQLite pool;
5. clear its keys, erase its state;
6. delete its files: the folder under `accounts/` (after link and parent checks), or for the legacy account only the files in the
   `OwnedPaths` allow-list of the 2.6 root (database, WAL/SHM, identity, TLS pin, baselines, usage ledger, log ...);
7. remove the registry entry, last.

The same lifecycle ends a runtime that is not being removed: disposing the host, a failed start and a cancelled start stop the work,
wait for it and for the opening of the database, release the device gate (the jobs do, as they end) and only then dispose the
services; a runtime that did not finish its start is taken out, and a live runtime is never replaced by a second one over the same
database. A removal that a crash cut off after the mark is finished at the next start, and the source is still told: a bounded
runtime is built over what the account still has (and makes no folder, identity or database), the hook is called once before the
keys are cleared, and a folder that is a link is refused and left (a dangling link too) with the entry kept.

The wipe of the 2.6 app ("reset this copy") removes the same files; since this release it also removes `baselines/` and `usage.json`.

## Limits

Each account can carry limits and two switches (the form is `BlindLimitsForm`, pure and tested):

| Setting | Meaning |
|---------|---------|
| stored bytes | the most this account may hold on disk (live: see "Storage is live") |
| traffic a day | bytes in + out, counted at the HTTP layer (`BlindMeteringHandler`): bodies exactly, plus 1 KB of headers/TLS per direction per request |
| requests a day | HTTP requests sent |
| unmetered networks only | wait while the network is metered *or the device cannot say* |
| pause on battery saver | wait while the device is saving power *or cannot say* |
| paused by you | stops the account until you resume it |

An empty box is no limit. **Zero is a pause**, never "unlimited". The form refuses fewer than 200 requests a day or less than 10 MB
a day (a sync round every 15 minutes would not keep up) and says why. A device has its own stored-bytes and traffic-a-day limits
over every account (`BlindDeviceLimitsForm`). An invited account starts with 2 GB stored and 200 MB a day (on a phone also
unmetered-only and battery-saver); account 1 after the upgrade has no limit.

**The day** is the device's local calendar day. The usage ledger (`usage.json` per account, `device-usage.json` for the device)
keeps 31 days and flushes every 4 MB or 30 s and after each round; it keeps a **monotonic high-water date** so a clock set back
never reopens a day that was already used up, and `BlindDayMath` finds the start of a day across DST gaps and ambiguous midnights.

**Usage that cannot be trusted is not a fresh budget.** A *missing* `usage.json` (or `device-usage.json`) is a fresh install. A file that
*exists* and cannot be read (access denied, locked), is empty or malformed, or was written by another version is kept exactly as it was
and never overwritten; the limits of traffic and requests cannot be checked against it, so an account that has such a limit is paused
for a reason of its own, `usage-unavailable` (the device, while it has a traffic limit: `device-usage-unavailable`), and the card says
so and offers **Start new counts** (`IBlindAccountsController.StartNewUsageLedger` / `StartNewDeviceUsageLedger`). That repair keeps the
damaged file aside as `usage.json.damaged-<utc>[-n]` (a verified copy made with create-new, never over a name that is taken; the number goes
after the highest one already there), keeps what this run has counted, and writes a new file over the damaged name, read back to be sure.
Only when that whole sequence has succeeded do the counts become available again: a repair that fails (disk full, access denied, a file that
cannot be opened) leaves the state **unavailable**, says why in a sentence, and may be asked again; the damaged file stays under its name
until the new one is written, so a restart never reads "missing = fresh". After a successful repair the newest **5** kept copies per ledger
stay and older ones are removed (best effort; a copy is never removed before the new file is in place, so the only damaged copy is never
deleted). The repair action says this.

A **save** that fails (the disk is full, access is denied) fails closed too: until a save works again (tried every 5 seconds, not at every
count) the counts are unavailable and the account (with a traffic or request limit) is paused like for a damaged file; the counts made
meanwhile are kept in memory and written when the save works. A read that failed for a passing reason (a lock) is tried again every 5 seconds and the counts
made meanwhile are added to the file's when it opens; damage found in the content stays until someone repairs it. An account with no
traffic or request limit is not stopped by damaged counts or by a failing save: nothing is checked against them. The same holds for the device's high-water
date: without it, a clock set back on top of a damaged file would refill the device's budget.

### The guard

`BlindLimitGuard` decides in this order: paused by you, metered network, battery saver, usage unavailable (only when a traffic or request
limit is set), traffic, requests, disk (`storage-unknown` when the size cannot be measured), device usage unavailable, device traffic, device
disk. The first that holds is the pause. A pause is not a failure: `BlindPausedException` is not an I/O or HTTP error, so no retry,
no back-off and no "install failed" is recorded for it, and a partly downloaded package keeps its place for the next day (range
resume). The one pipeline that never blocks is the paused report itself.

**Deciding and charging are one step.** `Check` only looks (a screen, the scheduler). What lets a request through is `AdmitRequest`: under
ONE lock shared by every account of the device (`BlindDeviceUsage.Gate`) it checks the account's limits and the device's, counts the request
and sets aside what the request is known to cost. Two callers racing for the last request, or two accounts for the last 9 KB of a device's
traffic, are decided one after the other and the second sees what the first took. What was set aside is spent as bytes really move and
given back when the request is cancelled, fails, or ends shorter than it said (a finalizer returns the reservation of a request that was
dropped without being disposed). Every sum saturates and every "does it fit" is `planned <= limit - held` (`BlindMath`), so a length near
`long.MaxValue` is a refusal and `BlindPause.Needed` never wraps.

**Metering is exact at the boundary.** The answer is read in steps the admission sizes (`BeginRead`): never past the next 256 KB check, the
traffic the day has left (account and device), or the storage that is covered or left; the 256 KB check is the full decision again (a switch,
a new limit, midnight). A request body is counted by the writes that succeed, with or without an announced length (the header only sets
traffic aside), so a cancelled send, a body shorter or longer than its header, or one the node never reads is counted for what went out.

### Storage is live

`BlindStoredBytes` is a baseline (a walk over the files the account owns: database, media, baselines, the replica work folder with its partial
download, backups, log) plus the bytes charged since (everything the network brought in is charged as it is read) plus what is reserved for
work that has begun. The guard decides on that live number, so a round that stores several blobs within one minute sees each one the moment
it arrives; the walk (every 60 s, and after every round and job) only re-syncs the counters and keeps what was charged while it ran.

**Growth the network did not bring.** What the app itself writes to disk in batches (a pulled blob, a page of events, an extracted archive, an
installed package, a backup step) is not charged as it is read. After **every** such persisted batch the account's meter is marked stale, and
the next admission (a request, a package, a device decision) walks the files again and decides on the fresh number, not on a number up to a
minute old. The 60-second cache stays only for the screen. So the overshoot of a storage limit by local growth is bounded by **one batch**
(a blob, 100 applied events, one step of a job), plus what the reservations of a job already set aside, and a second admission never decides
on a number from before the last batch.

**The device total counts every listed account.** The device's stored-bytes limit is decided over every account the registry lists, whether or
not it has a runtime at the moment (one still starting, one in repair, one being removed): an account that is listed but not running is
measured through a meter of its own over its folder, and one that cannot be measured makes the device's size unknown (`storage-unknown`
blocks a device decision, never reads as zero). A removal keeps counting its account until its files are gone **and** its entry is removed.

* An announced `Content-Length` is reserved **before the first byte**; a length that cannot fit is refused (an absurd one, near
  `long.MaxValue`, cannot fit).
* A response with no length (chunked), a zero one, or one that turns out longer than it said, may only go as far as the room that is left:
  the stream stops there with a pause. A package is installed only after it is complete and verified, so nothing partial is applied (the
  part file is kept for range resume and counted).
* A long job reserves its **peak footprint** beyond the archive for the whole job and gives it back when it ends
  (`BlindLimitGuard.AdmitPackage`, refused before the first byte when it does not fit): a first load or a baseline update needs 3 times the
  archive (the archive, its extraction, the installed copy), a backup 4 times (the check's extraction and the two copies the backup writes).
* **Unknown is not zero.** A root that cannot be read, a root or any folder above it that is a link (`BlindPathSafety.ComponentProblem`, looked at
  before anything is listed), a link found inside owned data, or a walk that fails for any reason other than "that file is gone" leaves the size
  unknown. An account with a storage limit is then paused (`storage-unknown`, retried after 5 seconds); the card says "unknown" and the device
  line "stored size unknown". Nothing relies on "ignore what cannot be read".

### The paused report

When a pause that the source should know of starts (`traffic`, `requests`, `disk`, `device-traffic`, `device-disk`, `paused-by-you`;
not the network and the battery, and not the three faults of this device's own files: `usage-unavailable`, `device-usage-unavailable`,
`storage-unknown`), the next report to the source carries `&paused=<reason>&pausedUntil=<UTC time>`, so the owner's
page can say that the copy is paused, and until when, instead of "silent". It is sent at once, again after 24 h while the pause
lasts, at once if the reason changes, and at once for a new episode; a failed delivery is retried after an hour; a successful round
clears it. No number of the person's settings is sent, only the reason and the end.

**What the owner sees.** The hub words it itself, from a closed list of reasons, never from text the device sent: *"Paused by the device's owner: daily traffic limit (until 21:00 UTC)"*,
on its Blind nodes page and on the network map, as a banner and never a notification; it replaces "silent" for that copy while the report is fresh (7 days), then the copy is judged on
its contact again. The apps send the end as a UTC time, so the hub tells it in UTC (the hub also reads a time with the device's own offset and would then say "device time"; no app of
this release sends one). A copy that only waits for an unmetered network or for the battery is not reported, so a long wait looks like silence on the owner's page. A paused copy
goes stale; one paused for a long time may need a new first load.

### The scheduler

`BlindDeviceScheduler` is the one loop of a device: one account at a time, the one that synced longest ago first, one round and the
due long job. It skips a paused account until the pause ends (24 h for a pause with no end), except for the network and the
battery and the three faults (`usage-unavailable`, `device-usage-unavailable`, `storage-unknown`), which are asked again each tick (a lock
is released, a repair is made, a switch turns). Changing a setting or asking for a sync ends a skip. A pause starts no first-load retry back-off.

**One account cannot hold up the others.** Each sync round and each long job runs under a token of its own, linked to the loop's and cancelled
after `SyncTimeout` (10 minutes) or `HeavyTimeout` (45 minutes). A job that honours it ends as a **pause**
("Paused; it continues where it stopped."), never a failure. A job that does not stop within `StopGrace` (15 seconds) is **abandoned**: the loop
goes on to the next account, the account is left alone until the old job has really ended, and the scheduler logs it.

**An abandoned heavy job holds the device gate and says so.** Heavy jobs (a first load, a baseline update, a backup) share one gate per device. The
gate records who holds it (the account and since when). When the scheduler abandons a heavy job, the gate marks that holder abandoned (the host's
`NoteJobAbandoned`). Another account's heavy call then does not read "Already running": it is a typed pause, "Paused: the device is busy: *A*'s
job did not stop", and it is not a failure. The scheduler asks again when the gate is free, and otherwise on a back-off (1 minute, doubling to 15
minutes, reset when the gate frees); the busy result does not grow the account's own retry counter. The card of the account that holds the
abandoned gate says: "Its long job did not stop when it was asked to (...)". The gate lets go when the job really ends.

**One loop.** `StopAsync` waits `StopTimeout` for the loop to end. A loop that has not ended by then stays the scheduler's loop (it is
draining): `EnsureScheduled` starts no second loop beside it, a later stop waits for the same loop, and a replacement is made only after it has
exited.

## Adding an account from an invitation

The owner of a full node (the *hub* the copy will call) makes a one-time invitation on its Blind nodes page (`docs/sync.md`, "Invitations to a blind copy"); the
friend pastes it into the accounts screen. `IBlindAccountsController` has four calls for it, and `BlindAccountHost` implements them in
`Services/Blind/Invitations/`:

* `PreviewInvitation(text)` looks at the text and the accounts only (nothing is sent, nothing is written) and returns a `BlindInvitationPreview`: *"A copy of
  your friend's Bee Memory Bank, kept encrypted: this device cannot read it. It comes from mem.example.org. This invitation is a one-time code, not an encrypted one."*
  (two halves, on purpose: the COPY is encrypted; the INVITATION is not - it works once, for a short time, and is useless without the owner's confirmation), the limits the account will start with (2 GB stored, 200 MB a day; on a phone also
  "only on unmetered networks" and "paused on battery saver", see `BlindAccountHostOptions.IsPhone`), how long the invitation still works, or why it is not taken
  (`BlindInvitationRefusals`): not an invitation (or a pairing code of the old way), a **version or a trust mode this app does not know** (never guessed at: it decides
  where a secret is sent), expired, an account for the **same hub** (the same node id, however it was added) already on the device, the cap of 8, a damaged list or a
  repair that blocks adding, or an account whose state cannot be read (fail closed).
* `AddFromInvitationAsync(text, settings?)`, `PollInvitationsAsync(force, ct)` and `Reconnect(id)`, described below.

### The steps, and what a crash leaves

| Step | What happens | A crash here |
|---|---|---|
| 1 | the account is listed, phase `pending` (limits and switches from the settings), and what the invitation says about the hub - **never its secret** - is kept in the account's state as one value (`bmb.blind.invite`, version 2: with the hub's signature and the SHA-256 of the secret, which the signature covers). **The state store is untrusted input**: every time the value is read, every rule of the invitation's text form holds again (the shape of the address, the trust and its pin, the lengths, the bounds of the name, the expiry and the hub's confirm deadline) and the signature verifies against the key the value names, so an address or a trust mode edited in the store is never called; the account then says "Needs repair: ..." and waits for the person to remove it (a value that is *absent* is the crash case below, and is cleaned up) | no entry, or a `pending` entry with nothing to redeem with: the next start cleans it up |
| 2 | the runtime starts and makes the account's identity with the pairing path's own `CreateIdentityAsync` (a blind node id, an Ed25519 key, a backup key); the one-time pairing secret that comes with it is spent: an invited account is not paired the old way | the same |
| 3 | **identity probe**: `GET /api/sync/identity` through the client the invitation's trust selects (`BlindHttpClientProvider.GetClient`: the pin, or the system's chain with the host name). The answer's node id **and** key must equal the invitation's, constant time on the key. Otherwise nothing is sent and the account is removed again. This is a sanity check that the computer at the address is the one meant; the invitation's signature, not this look, is what binds the address to the hub's key (`docs/sync.md`, "The string"). The probe is not made again on the same connection as the redemption: a backend behind the owner's own load balancer is the owner's infrastructure | the same |
| 4 | **redeem**: `POST /api/sync/invite/redeem` with the account's identity, platform and app version, its backup key and the proof (`bmb-invite-redeem-v1...`). The invitation's secret is in this body and nowhere else | see below |
| 5 | the 202 makes the account `awaiting-confirmation` with its **match code** (computed from the account's own identity, and compared with the hub's: another code is refused). The secret is zeroed | the next start has no secret but asks the hub (step 6): it finds the redemption (the hub knows this node id and key) and goes on |
| 6 | **status**: `POST /api/sync/invite/status` signed with the key the redemption registered, every 15 s while the accounts screen is open (`BlindInvitationWatch`, 10 minutes), then at each scheduler pass but at most every 15 minutes | - |
| 7 | `confirmed`: the account stores a `BlindCallCode` built from the invitation (authenticated under a fresh secret that is spent at once) and becomes `active`. Every path after it - `BlindPhoneState.CallCode`, the pinned or public-CA client, the pull client, the first load, backups - is the old pairing's, unchanged, and the first load runs through the limits guard like any account's. `refused` and `expired` become the phases of those names | the call code is written first and the phase second: a restart sees a call code under a phase that is not active yet and finishes it without the network |

**The secret.** `x` is held in memory (`PendingRedemption`) from the paste until the hub has answered, and is zeroed then, when the account is removed and when the host
ends. It is written to no file, no state value, no log line, no card and no exception (a test scans the data folder, the state, the secret store, the card, the log and the
sentences of every result of a finished flow, for the secret in every encoding). The invitation text is never stored. A crash before the answer therefore loses it:
the redemption cannot be repeated, but the status (which needs no secret) says whether it landed.

*Every parse has an owner.* `Judge` (the preview and the add) hands the parsed invitation to its caller only on the way out that adds an account, and wipes the secret of it on every
other way out; the preview wipes it before it returns, the add wipes both parses (the one it keeps and the one inside the gate) in a `finally`. `BlindInvitation.ToString()` is redacted;
`ToWireText()` writes the secret and is the only formatter that does. (A string cannot be wiped: the pasted text is the caller's, and it is the head's to keep short-lived.)

*Removing an account ends its invitation work first.* `RemoveAsync` marks the account `deleting`, then `StopInvitationWorkAsync` cancels everything the account asks of the hub (a
redemption in flight, a status request, a back-off that is pending), waits for an add that is still setting the account up (unless the caller is that add's own cleanup), waits for
an ask of the scheduler or the screen that was under way, and only then drops the secret and goes on to clear the keys, the state and the folder. Nothing of the account is written after
that point. A scheduler pass leaves an account alone while its add is still giving it a runtime (`AccountWork.IsSetUp`): it would find no identity yet, take that for a failure, and
remove an account that is being made.

*A removal whose first registry write fails.* The call of `RemoveAsync` cancels the add's work at once (before the mark, so that no redemption is sent for an account whose removal has been
asked for), and that cannot be taken back. The mark itself is therefore written up to 3 times (`BlindAccountHost.MarkAttempts`, 50 ms apart; for a file that is held for a moment, a lock
that is taken for a moment): a registry write that works on a later try ends with the account removed as asked. If every try fails the removal throws and the account is what it was, and
an add that had already seen its cancellation has ended without finishing: if its runtime was up, an account made from an invitation gets the add's last steps from the removal (the one-time
pairing secret is spent, the account is set up, and the scheduler and the screen ask the hub about it as they would have), and an account paired the old way is complete as it is; if the
runtime is not up, the card says so ("Its start was cancelled..." or "Setting up this account was cut short..."),
and removing the account again, which now waits for nothing, works.

*The add and every ask are operations of the host.* `AddFromInvitationAsync` and `PollInvitationsAsync` run inside `BeginOperation`: `QuiesceAsync` and a disposal wait for them (the
disposal also cancels them, through the host's lifetime), and close no runtime and release no root under them; the secrets still held are zeroed last, with the root. A host that is
closing takes no new account and asks nothing. `StartRuntimeAsync` looks at the CURRENT list entry before it makes a folder, a database or a key, and again where it publishes the
runtime (a removal marks the list before it looks for the runtime, under the same lock): an account that was removed meanwhile gets no runtime, and what was composed after it went is
ended, not kept.

**Answers of the hub to the redemption.**

| Answer | Meaning for the account |
|---|---|
| 202 | `awaiting-confirmation` |
| the same 202 again | a retry after a lost answer is safe while the invitation is still `redeemed` and its 72-hour window is open: it carries the same identity and the hub recognises the key. After the owner has decided, or the window has closed, a repeat gets the generic 410 and the device uses the signed status request |
| 410 `invite_not_valid` | one sentence for unknown, expired, used by another device, withdrawn. The device first asks the status (a 410 may be the answer to a retry of a redemption that did land and was decided since); if that is also 410 the account is removed |
| 409 | the hub knows this node id under another key: the account is removed |
| 429 | stays `pending`; the next try is not before `Retry-After` (5 s to 15 min), even when a screen asks - and never later than the invitation's end |
| 503 `locked` (or busy) | stays `pending`; a back-off of 15 s, 30 s, 1 min, 2 min, then 5 min, until the invitation runs out - a locked hub is never asked in a storm |
| 404 | the full node is older than 2.7: *"The owner's Bee Memory Bank is older than 2.7 and cannot take invitations; ask the owner to update, or pair with a code the old way."* The old way is never started on its own |
| no answer, a timeout, a 5xx | the same as a lost answer: `pending`, tried again |
| the certificate or the pin is refused | nothing was sent; the account is removed (while waiting for the owner's decision it is asked again at the usual rhythm) |

A held back-off never postpones what the invitation running out means: it is capped at the invitation's end, and the end is judged first. When the invitation ran out and every try of
this run was refused before the hub could have taken anything (a 429, a 503, a refused certificate, nothing sent), the account ends then and **no status request is sent**. When a try
may have landed (an answer lost on the way, one that was not understood), or this run knows nothing of the tries before it (the app restarted), the hub is asked once what became
of it: the owner can still decide about a redemption that landed before the end. A `pending` account that never got an answer, long after the invitation and any decision the owner could
still make are over (the invitation's expiry plus 72 hours), is given up. A card says what it is doing (`BlindAccountSnapshot.Note`): *"Connecting to mem.example.org..."*, *"Could not reach mem.example.org. It will try again."*, *"The
owner's Bee Memory Bank is locked right now..."*, or the pause sentence when the account's own limits say "not now".

**Metering.** All of these calls go through the account's own metered client: counted, and refused with nothing sent when the account's limits, switches or "paused by you"
say so (the account then says so instead of redeeming and looks again at its back-off, which costs no traffic).

### A clock that is off

The proof of a status request carries the device's time, and the hub takes it within 10 minutes of its own. A device whose clock is further off gets a 410 for an invitation that is
alive, and must not conclude "expired" or "refused" from it. The 410 carries the hub's HTTP `Date` header; when it differs from the device's clock by more than **5 minutes** the
account stays as it is (`awaiting-confirmation` or `pending`) and its card says *"This device's clock is 15 minutes off (behind mem.example.org's). Correct the date and time on this
device; it checks again in a few minutes."* The hub is asked again at a low rate (every 5 minutes, even for a screen that asks every few seconds); when the clocks agree the answer
is read as it says. A 410 from a hub whose clock agrees, or that sent no `Date`, is "not valid" as before.

### Cards

`awaiting-confirmation`: *"Waiting for your friend to confirm. Read this code out to them: K7F-2QX"*; `refused`: *"Your friend refused this device. You can remove this account."*;
`expired`: *"The invitation expired. You can remove this account."*; `removed-by-owner`: *"Your friend removed this device from the network. The copy stays until you remove
it."* (an account paired the old way says "The owner" instead of the name). The name is the account's name, which starts as the invitation's.

### The source removed the device

**Two refusals in a row** mean the source removed the device. A 401 or 403 to a sync round, **or to the first load, a baseline update or a backup** (the challenge, the authentication, a
download: the hub's way of saying it does not know the device any more) is told to `IBlindSourceRemovalSink` (called by `BlindAppController` for the rounds and by `BlindHeavyWork` for the
long jobs; any account, paired either way), and `BlindSourceRemoval.Judge` reads it against the account's own state:

- the **first** refusal is only remembered - `bmb.blind.source_refused_at`, the time, in the account's own state store, an additive key that 2.6.4 never reads and that a state written
  before it simply does not have. The account stays `active` and goes on at its normal rhythm (a round every 15 minutes; the first load's back-off). Its card, the home's sync line and the
  round's answer say, neutrally: *"The source refused this device once; asking again."* (log kind `refusal`, not a failure, not a problem line; a burst of refused retries of the first
  load writes the sentence once, an identical last line is not repeated). The card says it only while nothing newer is known: a first load that failed after the refusal (the network,
  a 5xx) is shown as *"First load failed: ..."*, because only a success forgets the refusal. A reverse proxy or a firewall in front of a healthy source can answer 401 or 403 for a moment,
  and that must not end the account's work;
- a **second** refusal that comes after **at least one normal retry interval** (`BlindSourceRemoval.MinimumGap`, 15 minutes) and **within 24 hours** (`Window`) of the first makes the
  account `removed-by-owner`. Refusals that come sooner (the same burst: a long job asks the source several times in a row, a round and a job start together) are the first one and
  change nothing - the remembered time stays, so the interval is counted from it;
- a refusal **older than 24 hours** counts as a first one again (the new time is remembered);
- any **contact that goes through** forgets the remembered refusal: a finished sync round, a finished first load, and a baseline check that the source answered - a 304 ("the baseline you
  hold is the current one") or a newer baseline that was installed (`IBlindReplicaSource.CheckBaselineAsync` says `Current` or `Installed`). A baseline check of a copy that holds no
  baseline yet asks nothing (`NotAsked`), a source that only implements the old `UpdateBaselineAsync` cannot tell a 304 from "nothing asked" and so never reports a contact, and a backup
  does not say whether it asked the source (it downloads the node's current package, but a run that resumes reuses the package it already holds): none of these says anything, and none
  forgets a refusal. (A backup can be refused like any other call, and then counts as a refusal.) Disconnect and wipe clears it with the rest of the state;
- an account the source has **already removed** counts nothing: a refusal that reaches it after the round that removed it (the long job of the same pass, or on the iPhone the sync round
  that follows a long job that removed it) is answered with the removed sentence and is not remembered. The scheduler does not run the long job from the card it took before a sync round
  that removed the account, and the iPhone's work round does not sync after a long job that removed it.

When it removes, the scheduler leaves the account alone, so there is no retry storm - an owner who revokes the device between the confirmation and the first load no longer leaves a
"First load failed" that comes back at every pass - the copy and its keys stay until the person removes the account, and nothing counts as a failure; the remembered time is dropped, so a
person who tries again starts from nothing. `Reconnect(id)` forgets any remembered time and puts the account back to `active` (a mistake on the source's side, a proxy that said no
twice); the next round decides, and it takes two refusals again. So does an answer of a computer that a removed account accepts (`ReactivateAfterPairing`), and so does any call code set in
the account's state (`BlindPhoneState.CallCode` forgets the remembered time: a refusal belongs to the computer that gave it, so after the old way's `StartRePair` the new computer's first
refusal is a first one). A refused certificate, a 5xx and a network error are not a refusal at all. A state that cannot be read or written never removes an account: the refusal counts as
a first one.

The price of the rule: the 15-minute gap is a constant (`MinimumGap`), not read from the scheduler, and the second refusal must fall inside 24 hours of the first. A copy that wakes less than once a day (an iPhone's background
rounds, a phone that was off) can meet a first refusal at every wake and never a second inside the window, so a revoked, rarely-woken copy may say "asking again" for a long time; an app in front for an hour or more produces the
second refusal. A revoked copy therefore reaches "removed by the owner" up to one retry interval later than with the old one-refusal rule.

The rule reads the device's **wall clock** (a monotonic clock does not survive a restart, and the time is remembered across restarts). A clock corrected forward by 15 minutes or more
between two refusals of one burst makes them count as two refusals, and the account is marked removed; a clock set back restarts the count (a remembered time in the future is not
trusted, so the next refusal is a first one).

### Removing an account tells the source

`BlindSourceLeaveHook` is the default `BlindAccountHostOptions.LeaveHook`: when the device is online it authenticates like any round and sends `POST /api/sync/leave` through the
account's own client **before** the keys are cleared (best effort, bounded by `LeaveTimeout`, 10 s). The pipeline counts the call and never blocks it, so a paused copy still says
goodbye. The hub does not revoke anything: its Blind nodes page then says the device's owner removed the copy and offers **Revoke**. An account that never connected tells nobody.
A removal that a crash cut off tells the source at the next start, from the account's own state.

### The old way stays

"Add with a pairing code" (`AddAsync`) is unchanged and per account: the account shows its phone code and the computer's call code is pasted into `AcceptCallCode`, which is
tried against every account that waits for an answer and lands in the one whose pairing secret made it. An invited account has spent its secret, so it never waits for a
code and a code is never given to it.

## What a head must do

The heads (Android, Windows, macOS, iPhone) implement the platform seams (secret and state store factories, `IBlindDeviceConditions`
for metered network and battery saver) and a screen over `BlindAccountsView` / `BlindAccountCard`; every sentence on the screen is
produced in AppCore. For invitations a head also: sets `BlindAccountHostOptions.IsPhone` on a phone; shows `PreviewInvitation` (the pasted text or a `bmb-invite:` link)
before `AddFromInvitationAsync`; starts a `BlindInvitationWatch` when its accounts screen opens and stops it when it closes; and runs the device scheduler (`BlindDeviceScheduler`),
which asks about waiting invitations at each pass. A head that has no way to know whether the network is metered answers *unknown*, and an account with that
switch on then waits: the guard fails closed. A background worker must treat `BlindPausedException` as "nothing to do", not as a failure
to retry.

The four heads at a glance (each is described below; all of them use the AppCore host, the device scheduler and the sentences of the screens):

| | Android | Windows | macOS | iPhone |
|---|---|---|---|---|
| secrets of an account | Keystore alias + blob file, bound to the id | DPAPI file, entropy with the id | Keychain items, name with the id | Keychain items, name with the id |
| state of an account | preferences file `bmb_blind_state.<id>` | `state.json` in the account's folder | `blind-state.json` in the account's folder | `blind-state.json` in the account's folder |
| "unmetered networks only" asks | `ConnectivityManager` (no `NOT_METERED`) | connection cost of the internet profile | `nw_path_monitor` (expensive or constrained) | `NWPath` (expensive or constrained) |
| "pause on battery saver" asks | `PowerManager.IsPowerSaveMode` | Energy Saver | Low Power Mode | Low Power Mode |
| a round every | WorkManager, 15 minutes (long jobs hourly) | one device loop, 15 minutes (long jobs hourly) | one device loop, 15 minutes (long jobs hourly) | the open app: 15 minutes; in the background only what iOS grants |
| an invited account starts with the two switches | on (a phone) | off | off | on (a phone) |
| adding an invitation | paste, or a `bmb-invite:` link (the Camera app opens a QR code) | paste | paste | paste, or a `bmb-invite:` link (the Camera app opens a QR code) |

## The Android head

`mobile/BeeMemoryBank.BlindMobile` (.NET MAUI, `net10.0-android`) is the Android head of the above. It names no account's runtime itself: one `BlindDevice` per process holds the `BlindAccountHost`
and the device's two schedulers, and the pages, the WorkManager workers and the backup service reach the accounts through it and nothing else, so there is never a second host on the app's folder.

### Keys and state, per account

| | Account 1 (the 2.6 install, id `legacy`) | A new account `<id>` |
|---|---|---|
| Keystore alias | `bmb_blind_seed_v1`, `bmb_blind_pairing_v1`, `bmb_blind_backup_v1` | the same with `.<id>` appended |
| Blob file | `bmb_blind_seed.bin`, `bmb_blind_pairing.bin`, `bmb_blind_backup.bin` in the app's root | the same names in `accounts/<id>/keys/` |
| AES-GCM associated data | `bmb-blind-identity-seed-v1`, `bmb-blind-pairing-v1`, `bmb-blind-backup-key-v1` | the same with `/<id>` appended |
| State | SharedPreferences file `bmb_blind_state` | `bmb_blind_state.<id>`; every write is a `commit()` |

The names are `BlindAccountNames`'s (the naming contract); the code that applies them is `KeystoreBlindSecretStoreFactory` (`Services/Blind/BlindKeystoreSecrets.cs`: no Android type in it) over one small seam,
`IBlindKeyWrap`, whose Android implementation is `AndroidKeystoreKeyWrap` (a non-exportable AES-256 key per alias, `AES/GCM/NoPadding`, no screen unlock needed so background work can use it). A blob copied
into another account's slot does not open: the alias differs, and the associated data differs even under the same key (a test proves each separately). Removing an account deletes its blobs first and then its
Keystore entries (a leftover key opens nothing; a leftover blob would be a secret at rest), tries every role even when one fails and says what failed. A Keystore entry the platform did not delete (or that is
still there afterwards: `AndroidKeystoreKeyWrap.DeleteKey` asks again) is a **failure**, not a success: `Clear()` throws, the account stays `deleting`, and the host runs the removal again (at the next Remove or the
next start) until every alias is confirmed absent; the same for the state, where a `commit()` or `deleteSharedPreferences` that answers `false` is an `IOException` (`IBlindPreferencesFile` is the seam that lets a test
make the platform say so). The messages name the role and the kind ("the Keystore entry of the backup key could not be deleted"), never an alias or the platform's own words. Saving a key never makes an account's
folder: a secret saved after the account was removed is refused. An install of 2.6.4 upgrades with nothing renamed: its blobs, aliases and preferences are account 1's, byte for byte
(`BlindHeadCompositionTests.AnInstallOf264_BecomesExactlyOneAccount_...` builds a 2.6.4 install with the 2.6 composition itself and starts the new host over it).

### Background work: one job for the device

The four unique WorkManager names are the 2.6 ones - `bmb_blind_sync`, `bmb_blind_heavy`, `bmb_blind_heavy_now`, `bmb_blind_sync_now` - and no work is named per account (an installed copy keeps its queued work).
The workers hand the pass to the device's schedulers (`BlindDeviceScheduler`, two instances over the one host, built with `RunSync` / `RunHeavy` off respectively):

* `BlindSyncWorker` (every 15 minutes, and "Sync now"): `device.StartAsync`, then `SyncScheduler.TickAsync`: a sync round for every account, one at a time, the one that synced longest ago first, each under its own
  time limit, a paused account skipped until its pause ends. It runs no long job and shows no notification.
* `BlindHeavyWorker` (every hour, and right after pairing or after an invitation was confirmed): a foreground job whose notification names the account (`Name: First load: 12 %`); `HeavyScheduler.TickAsync`: the first
  load and the due backup of each account, one long job on the device at a time (the device gate). The foreground notification is published when an account's job really starts (`BlindHeavyWork.Started`: the gate
  is held and no limit says "not now"), naming THAT account, and replaced for the next account's job; a pass in which nothing is due shows none.
* The rhythm is the WorkManager period (`SyncEvery` / `HeavyEvery` of the two schedulers are zero), so a job that fires a few seconds early is not skipped for 15 minutes.
* "Back up now" starts `BlindBackupService` for ONE account (the intent names it). Who may run it is decided by ONE small state machine for the whole process, `BlindManualBackup`
  (`Services/Blind/BlindManualBackup.cs`, portable and tested by interleaving with fakes), and one service instance's side of it, `BlindManualBackupService`; the Android file only reports what happened to it and does
  what the answer says. The states are **Idle**, **Pending**(account, generation, deadline) and **Running**(account, generation, owner = the service instance):
  * **Reserve** (`BlindBackupService.Start`, the page's tap, synchronously, before the system is asked to start the service): Idle -> Pending. The same account joins; ANOTHER account is refused on the spot with
    "The backup of <name> is running. This phone makes one backup at a time: ask again when it has finished." A Pending reservation is overtaken only when it is past its 30 s deadline AND another account asks (one lost
    start cannot lock the others out); the lapse applies to Pending only, a Running one never lapses.
  * **Deliver** (the system delivered the start to a service instance): starts the work ONLY for the current Pending reservation of that account (-> Running, owned by that instance). Anything else does NOTHING and
    says so (`Stale`: nobody reserved it, or another account overtook it; `Busy`: another account's or an older instance's run holds the phone; `Joined`: a duplicate for the instance's own run) and never touches the
    state: a stale start cannot occupy the run slot, so the newer account's start is never lost. It only acknowledges the start (`startForeground`, the system's five-second rule) and, when the instance has no run of its
    own, stops itself; it never stops the service under its own run.
  * **End** releases the run only for the generation AND the owner it was started with: a late end of an old run cannot release a newer owner's generation, and two runs of one account cannot coexist.
  * **Rollback**: if anything between accepting the run and starting its task throws (the foreground step, the thread pool) the run is given back at once with exactly the generation that was accepted, so a failed
    start cannot hold the phone for ever.
  * **Destruction**: when Android destroys the service the run it owns is **stopping**: every request is refused as "The backup of <name> is stopping. Ask again in a moment." until that run really ends, and a new
    service instance that is delivered a start finds `Busy` and starts nothing (no second run beside the cancelled one).
  * **Withdraw**: a start the system refused takes back only its own request. The state is memory only: a killed process starts Idle.

### The notification

One ongoing notification (id 1101, channel `bmb_blind`) for the long jobs and "Back up now". It is **private on purpose**: the full version names the account (`Alpha: First load: 45 %`; the name of a copy is the
name of its source, a person's or a computer's, which is sensitive), and it carries a **public version** (`SetVisibility(Private)` + `SetPublicVersion`) that the system shows instead on a locked screen whose owner
chose to hide sensitive content: "BeeMemoryBank blind copy" / "Blind copies", no name, no job, no figure. When no account has been selected (the backup service starts before the host can say whose job it is) the
full notification says the neutral "Blind copies" too, and is updated with the account's name as soon as it is known. The words are `BlindNotificationText`'s, built once.

The wait of the system is a saving, not a rule: when EVERY account that uses the network is "only on unmetered networks" the two periodic requests are re-enqueued with `NetworkType.Unmetered`; when every one pauses
on battery saver, with `SetRequiresBatteryNotLow(true)` (`ExistingPeriodicWorkPolicy.Update`, once per change; `BlindWorkPlan.For`). An account that is paused by the person, being removed, refused, expired or removed by
its owner does not count. The requests of the person ("Sync now", the first load) wait for a connected network and nothing else, so that a switch is answered at once by the guard, where it can be read. The check at
run time stays the authority: `IBlindDeviceConditions` is asked before every round and every request, with `ConnectivityManager.GetNetworkCapabilities(active)` lacking `NOT_METERED` for a metered network and
`PowerManager.IsPowerSaveMode` for battery saver (needs `ACCESS_NETWORK_STATE`, a normal permission). Anything the phone cannot say (no active network, no capabilities, a service that throws) is *unknown*, and an account
with the switch on then waits.

The boot receiver (no host yet) only makes sure something is queued and never loosens what the accounts asked for. Removing an account cancels none of these jobs (they are the device's); the host refuses new
rounds of an account being removed and stops only that account's own backup.

### Pages

* **Accounts** (the start page): the cards of `BlindAccountsView` (name, source host, one state line, stored, today, last sync), the device line, "Add account", the repair actions when the list and the disk disagree
  (`Use it as an account`, `Forget this account`, each an explicit tap), and the limits of this phone (`BlindDeviceLimitsForm`). While it is open it asks about invitations that wait for their owner every 15 seconds
  (`BlindInvitationWatch`). A card of an account whose removal did not finish (see above) says why and has "Try removing it again": the same removal, run again now; without the tap the host runs it again at the next start.
* **Add**: a paste box and a Paste button, the preview from `PreviewInvitation` (whose copy, from which computer, the limits, how long it works - or why it is not taken), Add. Nothing is sent and nothing is written before Add;
  the box is emptied as soon as the computer has answered. "Add with a pairing code" is the old way for a new account.
* **Account**: the 2.6 page bound to one account's controller (state, backups, log, the two codes of the old way - not shown for an account from an invitation), the account's card, its limits (`BlindLimitsForm`: sizes as
  "2 GB", "200 MB", the two switches, Pause / Resume), Rename, and **Remove this account** with `BlindRemoveConfirmation`. "Disconnect and wipe" is gone: with several accounts nothing restarts the process, and an
  account made by an invitation cannot be made again by wiping it.

Every sentence on these pages is a field of an AppCore view record; the pages build none, except the **fixed sentences** (`BlindPageSentences`) of failures that are not the person's doing (the clipboard, the scanner,
the QR picture, the state that cannot be read, the background scheduler's last error): a page never shows an exception's own text (an alias, a path or a class of the implementation), and never the scheduler's
`LastProblem`, which is built from one. Failures of what the person asked for (rename, remove, add) are told by `BusyAction.Describe`, the apps' one rule, **and the app sets that rule to the closed failure text of the blind core at start**
(`BlindFailureSetup.Apply()`, the first thing `MauiProgram` does: `BusyAction.Describer = BlindFailureText.Say`, by exception TYPE, never by message; an app that sets none gets the helper's default, which says a local
failure's own words). A refusal of the shared core that a page turns into an exception is thrown as `BlindPageSentences.Refusal(sentence)` (a `BlindSentenceException`, the mark that the closed rule passes on as it is); a plain
`InvalidOperationException` would be told as "an unexpected problem". `BlindBusyRouteCanaryTests` drives the REAL `BusyAction.cs` (compiled into the test project) through its catch / display route with hostile exceptions
(a path, a key alias, an address with a token) and asserts only the closed sentence reaches the screen; `BlindHeadFix1SourceTests` pins that the app sets the Describer first, that the helper's route is `Describe(ex)`, and
reads every `catch` of the three pages: no use of the exception variable other than that call, no formatter called by a page, no sentence thrown as a plain exception. The end of a job the page waited for (first load, sync, backup) is said by PHASE only (`BlindPageSentences.JobEnd`): a failed job gets a fixed
sentence that points to the log, never `BlindJobOutcome.Message`; the same test fails on any `.Message`, `LastFailure`, inner exception or stack trace read in a page. What the page binds from the shared view
(`BlindHomeView`, the cards) is the shared core's to keep clean.

### Invitation entry points

* **Paste** (the Add page). Every text from outside - the clipboard, the scanner, a link - passes one gate (`BlindInviteInput`) before it reaches the box: longer than an invitation can be
  (`BlindInvitation.MaxTextLength`, 2048, which is also the box's `MaxLength`) is refused BEFORE it is trimmed, copied or assigned, with a sentence that repeats none of it; what passes is trimmed (a clipboard ends in a
  line break). Whether it is an invitation is the preview's question, from the text alone.
* **A `bmb-invite:` link**: the launcher activity has one intent filter (`VIEW`, `DEFAULT`, `BROWSABLE`, scheme `bmb-invite`). The link goes to the Add page *in memory only* (`BlindInviteLinks`: only this app's scheme,
  at most `BlindInvitation.MaxTextLength` characters, kept until the page takes it, never logged or written, taken out of the activity's intent) and shows the preview. The link alone adds nothing: the person presses Add.
* **QR**: the owner's Blind nodes page draws the QR of the same string. The phone's Camera app opens it as a `bmb-invite:` link, which needs nothing more from this app. Scanning *inside* the app (a
  "Scan a QR code" button on the Add page, behind `IBlindQrScanner`; the text it reads goes into the paste box and gets the usual preview) is written (`AndroidQrScanner`, Google Play services' code
  scanner: Google's own screen, so no CAMERA permission) but is **off by default**: it is built only with `-p:BmbQrScanner=true`. Why, and what was found, below.

The app asks for no CAMERA permission (`BlindManifestTests` holds that).

### The in-app QR scanner: what was found, and why it is off

* **Package**: `Xamarin.GooglePlayServices.Code.Scanner` 116.1.0.17 (the binding of `com.google.android.gms:play-services-code-scanner` 16.1.0), compatible with `net10.0-android`; the app builds, trims and publishes
  with it (Release APK 44.7 MB without, 45.7 MB with). The scanning screen is shown by Google Play services; the app declares no CAMERA permission and no camera feature either way (`BlindManifestTests`).
* **It is not "ML Kit-free"**: the binding depends on `Xamarin.Google.MLKit.BarcodeScanning.Common`, `.Common` and `.Vision.Common`, Firebase's component registry (`Xamarin.Firebase.Components`, `Encoders`,
  `Encoders.JSON`), Google's DataTransport (`TransportRuntime`, `TransportBackendCct`, `TransportApi`), `Xamarin.Google.Android.ODML.Image` and Play services Base/Basement/Tasks - 16 packages in all. The merged
  manifest then gains `MlKitInitProvider`, `MlKitComponentDiscoveryService`, the DataTransport job service and alarm receiver, the CCT backend registration and two Play services activities.
* **Licences**: the Microsoft bindings are MIT (`LICENSE.md` of each package; DataTransport, Firebase and the two Ktx pins `MIT AND Apache-2.0`). The Google libraries underneath are Google's own: Play services and
  ML Kit under Google's terms for those SDKs, Firebase components and DataTransport under Apache-2.0. `THIRD-PARTY-NOTICES.txt` would list them.
* **The reason it is off**: DataTransport is the library ML Kit logs its usage through, and `PRIVACY.md` says Bee Memory Bank has "no telemetry, no analytics". Whether anything leaves the phone has to be seen on a phone
  first (traffic of the app while scanning), and `PRIVACY.md` would then name Google Play services under "Components by other vendors". That is a decision for the project, not a build detail.
* **Dependency pins it needs** (inside the switch only): Play services pulls AndroidX Fragment 1.9 and Tracing 2.0, which carry the Kotlin extensions themselves, so the older `fragment-ktx` / `tracing-ktx` bindings
  MAUI brings define the same classes twice and the dexer stops ("Type androidx.fragment.app.FragmentKt is defined multiple times"). `Xamarin.AndroidX.Fragment.Ktx` 1.9.0 and `Tracing.Tracing.Ktx` 2.0.0
  (`VersionOverride`, this app only) are the empty successors that fix it.

### Tests

`tests/BeeMemoryBank.BlindMobile.Tests` runs the shared contracts (`BlindSecretStoreContract`, `BlindStateStoreContract`) over this head's stores, the naming, two accounts in one process, the upgrade of a 2.6.4 install
(account 1 only, nothing moved, renamed or re-keyed, keeps syncing through the device's scheduler), the work plan, the link inbox and the conditions probe - all on any OS, over software keys and in-memory preferences. The
Keystore calls, SharedPreferences and WorkManager are one small class each and are checked on a phone.

## The desktop heads (Windows and macOS)

`desktop/BeeMemoryBank.BlindDesktop` is one Avalonia host for both systems; the operating system's part is `desktop/BeeMemoryBank.BlindDesktop.Windows` or `.MacOS` (the host picks the one it
was built for). `BlindDesktopRuntime` builds ONE `BlindAccountHost` over the platform's per-account stores and the one device loop (`BlindDeviceScheduler`) that syncs the accounts in turn; the
single-instance guard per data folder is unchanged (a second start asks the first to show its window). `BlindTimerScheduler.cs` stays in the tree only because the iPhone head still links it; the desktop
no longer starts it.

### Where the keys and the state are

| | account 1 (the 2.6 install, id `legacy`, nothing moved) | a new account `<id>` |
|---|---|---|
| Windows secrets | `<root>\secrets\{identity-seed,pairing-secret,backup-key}.dpapi`, DPAPI entropy `BeeMemoryBankBlind/v1/<role>` | `<root>\accounts\<id>\secrets\*.dpapi`, entropy `BeeMemoryBankBlind/v1/<id>/<role>` |
| Windows state | `<root>\state.json` | `<root>\accounts\<id>\state.json` |
| macOS secrets | Keychain service `com.beememorybank.blind`, accounts `identity-seed-v1`, `pairing-secret-v1`, `backup-key-v1` | the same service, accounts `<id>/identity-seed-v1` ... |
| macOS state | `<Application Support>/BeeMemoryBankBlind/blind-state.json` | `.../accounts/<id>/blind-state.json` |

`WindowsSecretStoreFactory` / `WindowsStateStoreFactory` and `MacOsSecretStoreFactory` / `MacOsBlindStateStoreFactory` build them from `BlindAccountNames`; both pass the AppCore contract
(`BlindSecretStoreContract`, `BlindStateStoreContract`): an account's secrets are invisible to the others, a blob copied into another account's slot does not open (a DPAPI blob fails with another entropy, a Keychain item
fails its digest, which binds the Keychain account name), and clearing an account removes exactly its blobs or items. Removing an account clears those, then the host deletes its folder. The macOS factory refuses
an id the app did not make before it builds a Keychain account name from it. The state-store factories and the DPAPI store's table of folder locks hold what they made **weakly**: the account's runtime owns its
store, so one object per file stays true while anyone uses it, and a removed account leaves nothing behind in them for the life of the process (`WindowsStoreCacheTests`, the same checks for the macOS factory in
`AccountStoresTests`).

### What the two switches ask of the system

| | "Only on Wi-Fi or other unmetered networks" (metered?) | "Pause on battery saver" (saving power?) |
|---|---|---|
| Windows | `NetworkInformation.GetInternetConnectionProfile().GetConnectionCost()`: fixed or variable cost, roaming, over or near the data limit = metered; `Unrestricted` = not metered. An unknown cost, or no profile = cannot tell | `PowerManager.EnergySaverStatus == On`; `Off` and `Disabled` (no battery) = no; `Uninitialized` = cannot tell |
| macOS | one `nw_path_monitor` for the process: a satisfied path that is *expensive* or *constrained* (Low Data Mode) = metered; no usable path, or no report yet = cannot tell | `NSProcessInfo.isLowPowerModeEnabled` (macOS 12 and later; older = cannot tell) |

"Cannot tell" is `null`, and an account with the switch on then waits (the guard fails closed toward the data plan). The system is asked at most every 2 seconds. A computer starts an invited account **without** the two
switches (`IsPhone` is false): Ethernet on a PC is allowed, and a person who wants them turns them on in Limits. The desktop heads are never a server host: the "not applicable" of a server blind node does not arise here;
a PC with no battery answers "not saving power" and a machine with no internet profile answers "cannot tell".

### The window

An account list on the left (one card per account, every sentence from `BlindAccountCard`, the device line from `BlindAccountsView`), the selected account on the right: its card lines, today's panel (identity, the
pairing code with its QR for the old way, the computer's answer, first load, sync, backups, the log) while the account is active and its copy is up, then **Limits** (`BlindLimitsForm`: sizes in MB or GB, requests a
day, the two switches; a refusal is the form's own sentence), **Pause / Resume**, **Rename** and **Remove this account...** (`BlindRemoveConfirmation`; the source is told when the device is online). **Add account** is a
paste box with the preview of the invitation (`BlindInvitationPreview`: whose copy, from which computer, the limits it starts with, how long it works, or why it is refused) and an "Add" button; "Add with a pairing code"
is the old way, per account. Paste is the only entry on a desktop (no camera, no link handler). The device's own limits and the repair states (`RepairIssues`, each with its one explicit action) are on the left; a damaged
list shows its sentence and nothing else. While the window is open `BlindInvitationWatch` asks about an invitation that waits for its owner every 15 seconds (the window starts out hidden and the watch runs only while it is shown: an app launched into the tray never starts it, and nothing that is still on its way - a read of the accounts,
an add that ends after the window was hidden - starts it again; one helper in `MainViewModel` starts it). Removing an account while the watch is inside the hub ends that ask first: the host cancels the account's ask and waits for it before anything of the account is
cleared, so nothing is written for an account that is going (`WindowsInvitationRemovalTests`, with a hub that holds its answer and one that ignores the cancel). The window numbers its reads of the accounts and does not
show an older read over a newer one. The paste box holds at most `AddAccountViewModel.MaxPasteLength` (4096) characters, and a text longer than an invitation can be (`BlindInvitation.MaxTextLength`, 2048, blank space
not counted) is refused before it is trimmed or previewed, with its own sentence, and not kept. The tray / menu-bar tooltip sums the accounts up ("3 copies - 1
paused - 1 needs attention"), the long job that is running leads it (an account that was removed takes its line with it: `AccountNotifications.Reconcile`), and the menu has Open, Sync now and Back up now (every
account), Add account, Settings and Quit. The text boxes are filled from the account when it is shown and never overwritten while the person types. A failure on any screen is told by its KIND (`FailureNotice.Say` over the shared `BlindFailureText`: no path,
address or token), the exception itself goes to the error log through the view model's log callback, and the log records its TYPE and its stack frames, never what it said, nor what an inner exception said
(`DiagnosticText.Describe`: one line, at most 8 frames of `Type.Method` without file names, at most 6 exceptions of the chain, 800 characters; `ErrorLog.Write` and the console output of `--self-check` use it, and the self-check's
own findings are fixed sentences that name no folder). `ScreenTextGuardTests` fails on a source line that reads an exception's message or `ToString()` - in a log call as well - outside the two platform files that classify or carry it
(`DiagnosticTextTests`, the self-check and start-up tests push an exception with a path, an address with a token and a line break through the log file and the console output and find none of it). None of this is a guarantee for every error or log path of the apps (`FAILURE-TEXT.md`).

### The upgrade of an installed copy

A 2.6.4 install is account 1: the first start writes `accounts.json` and moves nothing - the DPAPI files, the Keychain items, `state.json` / `blind-state.json` and the database keep their names and their bytes
(`WindowsCompositionTests.A264Install_BecomesAccountOne...`, `MacOsUpgradeRehearsalTests`). It has no limits and any network until the person sets them.

### What is not covered by a run here

The macOS head was compiled, not run, on the Windows PC where it was written (the host and its test project compile with `-p:BlindWindowsHead=false -p:BlindMacHead=true`). The hand-built Objective-C block that
hands `nw_path_monitor` its update handler, the `objc_msgSend` calls for Low Power Mode and the Keychain calls are checked on a Mac only (`TheRealSource_OnAMac_AnswersForReal` and the other `MacOnlyFact` tests). The window
itself was not started by any test: its view models are tested without Avalonia and its bindings are compiled (a wrong binding fails the build), but its look is not.

## The iPhone head

`mobile/BeeMemoryBank.BlindIos` ([IOS.md](IOS.md), "Several accounts"; source-only for now). It is the Android head's twin on iOS:

- **Keys and state.** Keychain items `<id>/identity-seed-v1` ... under the one service (account 1 keeps the 2.6 names), `blind-state.json` in each account's folder, and the 2.6 root's file protection and no-backup mark on every account folder.
- **The two switches.** `NWPathMonitor` (expensive or constrained = metered) and Low Power Mode behind `IBlindDeviceConditions`; the managed HTTP handler is kept (no `NSURLSession` cellular promise).
- **While the app is in front**, the one `BlindDeviceScheduler` loop syncs the accounts.
- **In the background** the two fixed BGTask identifiers serve the accounts in `BlindDeviceScheduler.PassOrder` (least recently synced first), one at a time through each account's guard, with an even slice of the wake when there are several accounts: **each account syncs less often in the background; the open app syncs all of them.** Only one background round runs at a time and never beside the open app's loop.
- **Invitations.** A `bmb-invite:` QR code needs no scanner: the system Camera opens the link in the app (the URL scheme is in `Info.plist`, next to `bmb-blind-call`), on the Add page.
- **Notifications and logs.** One "no contact" notification per account (for an account made from an invitation the title names the account; for account 1 and an account paired with codes the title is generic, but the text under it still holds the name; either way the name can show on the lock screen), repeated daily while the silence lasts. A round's result goes to the log of the account it concerns; what is about the wake as a whole goes to a device log that names no account.

## Known limits

* The status request is signed with the device's clock and the hub accepts it within 10 minutes of its own: a device whose clock is further off cannot learn the owner's
  decision until the clock is corrected. It says so on the card (see "A clock that is off") and asks again every 5 minutes; it does not conclude "expired". A hub that sends no `Date`
  cannot be compared, and its 410 is read as it says.
* An invitation written whole by someone else, with their own hub and key, is a valid invitation to their hub: the signature binds the address to the key, not the string to the
  owner. A friend who pastes it joins that mesh; nothing of the owner's invitation is in it. The identity probe is not repeated on the redemption's own connection: with the signature the
  origin is bound to the hub key, TLS authenticates the origin, and a backend behind the owner's own load balancer is the owner's infrastructure.
* A crash between the hub taking a redemption and the app writing the 202 loses the invitation's secret, so the redemption is not repeated; the status finds it. A crash before the
  hub took it leaves the invitation unused (the person pastes it again).
* An invitation is added as a pending account before the hub has been asked: for a moment (and for as long as the hub cannot be reached) the device holds an account with an
  identity and no data. It counts towards the cap of 8.
* A hub that is the only computer of its network and has not activated metadata cannot say that it is a superadmin (`BlindPreflightReplicaAuthority`), so the package it serves lists
  it as a plain peer and a copy rejects the hub's own `whitelist_add` events - the confirmation of a device among them - until they are quarantined (five rounds). Such a hub
  should activate metadata (protocol 4) before it invites copies.
* The lock keeps the app's own processes apart; a malicious process that can already write the app's folder is outside what it stops.
* An account whose first start failed after its folder was made is on record as set up (the marker is written before the identity): if the
  folder is lost then, the account shows `folder-missing` and needs "forget", it is not silently started again.
* A removal that a crash cut off after the courtesy call tells the source a second time at the next start. The hook is idempotent at the source
  by contract: a source that has already seen the leave must treat the second call as a success. The app keeps no memory of the first call.
* A pause during the blobs of a complete archive resets that archive on the next attempt (blobs are content-addressed and kept).
* Sizes are 1024-based (KB, MB, GB; MiB and GiB are read the same). A size that is too big to be one (any length of digits, a huge decimal) is
  refused with the usual "more than a device can hold" sentence, never an exception.
* Storage that is written locally from a download (an extraction, a sealed baseline) is covered during a job by its reserved peak footprint and
  counted exactly by the walk at the end of the job, not byte by byte; the bytes the network brings in are the live charge.
* Headers and TLS are an estimate (1 KB per direction per request); bodies are exact.
* A job that ignored its token and was abandoned goes on running in the background; the loop does not wait for it and starts nothing new for
  its account until it ends, and no other account starts a heavy job while it holds the device gate (a typed "device busy" pause with a
  back-off). A host that is disposed while such a job (or any operation) is still running keeps the data folder's lock until it ends
  (`ShutdownIncomplete`); the process, not the host, is what ends it by exiting.
* The damaged usage file is kept aside (a verified copy) when the repair is asked for, not when the damage is found; until then it stays where it
  is. At most the 5 newest copies per ledger are kept; the retention is best effort.
* Local growth between two walks of a single batch (a blob, 100 events, a job step) is not seen before the batch ends: a storage limit can be
  exceeded by at most one batch plus what a running job had set aside.
* The bytes of a response read that races the disposal of its admission (a stream abandoned while a read is in flight) are not counted: the
  admission is closed and its operations are no-ops, so no reservation can leak; the price is that a read that loses this race goes
  uncounted.
* The usage ledgers are saved synchronously, under the device's budget gate: a volume that stalls (not fails) stalls admissions with it.
  Known, not changed.
* Shared static state of the libraries that the blind composition does not use (`ArticleWriteLock`, `HeavyOperationLock`, the
  `FolderAccessService` cache) is not per account.

### Declined on purpose (residual risk)

The data folders live in the app's sandbox. The design defends against accidents and honest bugs (a restore, a copy, a tool, a crash), not
against a local process that can already write the folder and race the app.

* **No-follow handles, mount-point detection and check/use windows.** Links are looked at (the root, `accounts/`, the account's folder, the
  registry file, and the immediate children the app opens by name) with a `lstat`-style look, and then the path is used by name. A swap of one of
  these between the look and the use, a mount or a bind mount whose root is a real directory, and anything deeper than the immediate children
  are not detected. Closing that needs handle-based, no-follow APIs on every platform and every head, and would not stop a process that can
  already write the folder.
* **The standalone state and log writers have no cross-process lock.** `FileBlindStateStoreFactory` (state.json) and `BlindPhoneLog` take no lock of
  their own: they require the host's root lease (`accounts.lock`). Used without a host, a second process can write the same state or log file.
  The heads always go through the host.
