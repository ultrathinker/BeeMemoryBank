# Night plan, 2026-09-05 — closing the security-review findings

Working document for the overnight run. Written down because decisions were made here that the
code alone does not explain, and because the owner asked for the plan to live somewhere findable
rather than in a chat log.

Source list: the review artifact (37 findings). 26 closed before this run; this document covers
what happens to the rest.

---

## Standing constraints for this run

- **Production is not touched.** No deploy to Hetzner, no backup-restore drill. The owner decided
  this explicitly: about 20 people use that instance, and there is nobody awake to roll back.
  Items 2 and 3 stay open for a session with a human present.
- Every change lands as its own commit, so any single one can be reverted on its own.
- Agents work in isolated git worktrees; nothing merges to `master` without review here.
- `AGENTS.md` and `CLAUDE.md` are never edited on an agent's own initiative.

---

## The owner's principle, in their words

> Нода должна иметь права не больше, чем позволено синхронизацией. Общение между нодами должно
> идти только с помощью синхронизации. Одна нода у другой не должна вызывать какие-то открытые
> методы API.

Restated: a peer should be able to do no more than *synchronise* — write an article and propagate
it. It should not hold authority over the cluster, and node-to-node traffic should be the sync
protocol rather than ad-hoc API calls.

### Where that principle already holds

The public HTTP surface is already close to sync-only. After the wave-3 work, `PublicSurface` plus
`PublicSurfaceMiddleware` answer 404 without an internal key to everything except `/api/sync/*`,
`POST /api/join`, `/health`, `GET /api/version`, and the restore file download.

### Where it does not, and why that is the real problem

**Sync is not a safe channel by construction.** The destructive operations travel *inside* it as
ordinary events: `whitelist_add`, `whitelist_revoke`, `whitelist_update`, `hard_delete`,
`restore_network`, `master_password_changed`. A node that "only synchronises" can therefore still
wipe the mesh — through sync.

So the boundary that matters is not sync vs. not-sync. It is **content events** (article, folder,
comment, tag, media) vs. **control events** (the six above).

That split already exists and is already enforced: `EventApplier.ApplyAsync` has a
`requiresSuperadmin` gate listing exactly those six event types, and it checks `IsSuperadmin` on the
**receiver's own** whitelist row — so authority cannot be asserted by the sender. The mechanism is
right. What is wrong is the default: `JoinEndpoints` sets `IsSuperadmin = true` for every node that
joins with the master password. A phone that joined to read notes has the same power as the server.

**Review checkpoint for item 20 — two directions, only one of them changes.** `IsSuperadmin = true`
appears on both sides of a join and they mean opposite things:

- **Host side** (`JoinEndpoints`, the `else` branch that creates the row for the node that just
  joined): "the node that joined me may change cluster state." This is the one that must become
  `false`.
- **Joining side** (`InitEndpoints` ~line 309, `NodeSetupService` ~line 228, and the CLI join): "the
  node I just joined is a superadmin, from my point of view." This must stay `true` — the joining
  node has to accept whitelist and hard-delete events from the server that admitted it, or it is
  cut off from the mesh it just entered.

Flipping both would leave a fresh node refusing control events from its own bootstrap server.
Verify this specifically before merging item 20.

**Decision (item 20):** a joining node becomes an ordinary content peer. Promotion to superadmin
becomes an explicit act by an existing superadmin, through the whitelist endpoint that already
exists and already announces the change to the mesh. Nodes that have already joined keep whatever
authority they have — silently demoting someone's admin overnight would be worse than the bug.

---

## Remote subscriptions: a correction

An earlier objection raised here was wrong and is retracted.

The claim was that remote subscriptions (mounting a folder from another vault, read-only) violate
the sync-only principle because node A calls node B's HTTP API directly: `/api/auth/remote-token`,
`/api/folders/accessible`, `/api/folders/by-path/snapshot`.

Reading the code shows the feature is already built the way the owner proposed as a fix:

- `/api/auth/remote-token` requires the **username and password of a real user account on the
  target node**, verifies it with Argon2id against `tbl_user`, and issues a 90-day bearer token
  bound to that `UserId`.
- `/api/folders/accessible` and the snapshot endpoint resolve that token to the user and apply
  **that user's ordinary folder ACL**.
- It is read-only by design.
- Tokens are revoked when the user is deactivated or deleted (`UserService.RevokeRemoteTokensAsync`).

So a subscriber holds *fewer* rights than a synchronising peer, not more: one user's read-only view,
with no master password, no mesh membership, no `IsSuperadmin`, and no ability to originate sync
events at all. There is nothing to fix for the reason the objection raised, and the feature should
stay.

**What is genuinely worth attention** is narrower and is not a security hole: folder ACL is now
evaluated on two independent code paths — the sync/local path and the remote-subscription path. The
risk is *drift*, where a filter is tightened in one and forgotten in the other. That is a guardrail
test, not a redesign. Logged below as a follow-up.

---

## Item 1 — DEK rotation can brick a node permanently

The worst of the remaining findings, and the one being fixed here directly.

### What happens

`DekRewrapper.ReWrapTableAsync` walks every DEK-bearing row and unwraps it with the **old** master
key, unconditionally. A peer that applied the rotation before this node did will ship articles whose
body DEK is already wrapped under the **new** key. That row throws `AuthenticationTagMismatch`, the
exception leaves the loop, the whole rotation transaction rolls back — and every retry hits the same
row. `SwapMasterDek` is never reached. The node can never complete the rotation, and the only way
out is to wipe it and re-join.

With the default `auto_accept = false`, that is the *expected* outcome of any rotation where
somebody wrote in the window between propose and accept.

### The fix

The failure of one row must not be able to destroy the node. Per row: try the old key (the normal
path and a rewrap), then the new key (the row raced ahead and is already where it needs to be —
leave it alone), and only if neither opens it treat the row as unreadable, record it, and continue.

Rolling back forever protects nothing: the rows that *could* have been rotated stay unrotated too,
and the operator gets an unrecoverable node instead of one unreadable article.

Counting matters as much as continuing. "The rotation finished" and "every row came with it" are
different statements, and the second is the one an operator needs — a row left behind is an article
that no longer opens, and it must not be discoverable only by a user hitting it months later. Hence
`RewrapTally`: rewrapped / already-on-new-key / unreadable, surfaced in the completion message the
operator already reads, with the unreadable rows named.

The projection matrix has its own pass, sealed directly under the master DEK rather than under a
per-row DEK, and it had the same flaw one table over — `tbl_projection_matrix` is replicated too.
Fixed the same way, except that a matrix readable under neither key is a hard error with a message
saying what to do: it is derived from the articles, so it must be dropped and re-embedded, which
costs time rather than data.

`EventLogger` also stopped writing a hardcoded `DekEpoch: 1` into every article event regardless of
the node's actual generation. Nothing read it, so nothing was visibly broken — but a field that is
always wrong is worse than an absent one, and the first reader to trust it inherits a silent bug.

### Deliberately not done tonight

- **A `dek_epoch` column on the DEK-bearing rows.** The tidier long-term design: the rewrapper would
  look the epoch up instead of trial-decrypting. It touches every writer, and two agents are in
  those files tonight. Trial-decrypt is self-correcting and needs no migration, so it is the correct
  thing to land first regardless.
- **Barrier semantics for heavy events.** `PeerDekRotationApplier`, `EventApplier.Restore` and
  `DekRotationService.AutoAccept` apply through fire-and-forget `Task.Run` while the pull loop keeps
  applying the next events. That is the mechanism that produces the race in the first place. Fixing
  it properly means a rotation event must be a barrier — nothing after it applies until it is done —
  and that interacts with the quarantine work in flight tonight.

---

## Running tonight

| Item | What | Where |
|---|---|---|
| 7 | Quarantine eats temporarily-invalid events: split "permanently broken" from "precondition missing" | agent, worktree `night-7` |
| 20 | Joining node becomes a content peer, not a superadmin | agent, worktree `night-20` |
| 10 | List and tree load every row and filter in memory; push pagination and prefix ACL into SQL | agent, own worktree |
| 11 | Vector cache rebuilt on every article write; the invalidation is redundant | agent, own worktree |
| 1 | DEK rotation bricking (above) | here |

Each agent is required to prove its tests catch their bug — reinstate the original defect, watch the
test fail, restore the fix, watch it pass — and to say so plainly if a test passes either way.

## Landed so far

- **Item 1 (here).** Rewrap survives a row it cannot open, on both the per-row-DEK pass and the
  projection-matrix pass; `RewrapTally` reports what was left behind; article events carry the
  node's real DEK epoch. Commits `7464e26b`, `0c6b8d88`, `ea8341f9`.
- **Item 7 (agent, reviewed and merged).** `SyncFailureClassifier` is the single place a failure is
  sorted into permanent or deferred; deferred failures are budgeted by wall-clock time (6 hours)
  rather than attempt count, because push-on-save can retry the same event many times a minute and
  an attempt-count budget meant to span hours would burn through in minutes under load. Merge
  `3896ec35`.

  **Found in review and fixed here (`faea0981`):** the whitelist lookup filters on `status = 'A'`,
  so "never heard of this node" and "this node is revoked" both arrived as null and were treated
  alike — which made a revoked peer's events deferrable. Nothing applied while the check kept
  failing, so it was not an open door, but re-adding the peer inside the six-hour window would have
  applied its entire pre-revocation backlog, resurrecting exactly the writes the revocation was
  meant to discard. A revoke is an answer, not a missing precondition.

## Queued after those

13 (repositories reachable from the API layer), 16 (media into the blob store by hash),
14 (decompose `EventApplier`), 19 (confidential rotation, X25519 envelopes per peer).

## Follow-ups recorded, not scheduled

- ACL-drift guardrail across the sync path and the remote-subscription path (above).
- **`WhitelistRevokeBackfill` is dead code, and migration 021 made that matter.** Its docstring
  claimed it runs on every startup; nothing constructs it. Corrected in the file so the comment
  stops asserting a heal that does not happen.

  The reason it now needs a decision rather than a shrug: whitelist rows carry a version as of 021,
  and legacy revoked rows sit at version 0 — which loses to any incoming `whitelist_add`. So the
  ghost-node resurrection this class was written to prevent is exactly what a stale add produces
  against those rows today. Running the backfill fixes them, because it stamps the version along
  with the event. It also emits real revoke events to every peer, which is an outward-facing action
  on live data and belongs in a session with somebody watching — not in a startup path firing
  unattended, and not tonight under the no-production rule.

  Worth deciding early: the exposure is on any vault old enough to carry rows revoked by the
  original join bug.
- `tests/BeeMemoryBank.Integration.Tests` is intermittently flaky and the cause is **not yet
  established**. Across five runs, two produced failures and three were clean; the failing tests
  differed each time (`SnapshotRestoreSessionTests`, then `SnapshotRoundTripTests` plus
  `DekRotationFlowTests`), and each passed in isolation immediately afterwards.

  Two hypotheses were raised here and **both were checked and are wrong**, recorded so nobody spends
  the time again: (1) a shared process-wide session flag — `SessionService` holds no static state,
  every test class gets its own instance; (2) the static ACL cache in `FolderAccessService` leaking
  between test classes — its keys are already namespaced by `DatabaseId`, with a comment saying why.
  The classes that did fail are also already inside `HeavyOperationCollection`, so they are serialized
  against each other and against compaction/restore/rotation.

  A diagnostic full run captured for this purpose came back clean, so there is still no failure
  message to work from. The next person to see it should capture the assertion text before changing
  anything — the remaining suspects are other static state (`ArticleWriteLock.Locks`,
  `LegacyPasswordSlotMigrationService`'s static semaphore) and shared on-disk paths, but that is a
  list of suspects, not a diagnosis.
- Items 2 and 3 (deploy, backup-restore drill) need a human present.
- Whether the sync-only principle should eventually absorb remote subscriptions is an open product
  question, not a bug. It would mean removing the ability to mirror a vault you have not joined.
