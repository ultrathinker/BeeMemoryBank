# ADR 0004: Lamport Clocks Instead of Hybrid Logical Clocks for Sync Ordering

## Status
Accepted

## Context
BeeMemoryBank synchronizes articles and folders across multiple nodes (desktop installs,
servers) via an event log, using Last-Writer-Wins (LWW) conflict resolution when two nodes edit
the same article or folder concurrently. Each node is an independent process with its own
system clock, and those clocks are not assumed to be synchronized with each other. The sync
layer needs *some* consistent way to order events across nodes so that, when two edits to the
same entity are compared, the two nodes independently comparing them arrive at the same answer
about which one wins.

The obvious naive approach — stamp every edit with the local wall-clock time
(`DateTime.UtcNow`) and let LWW compare raw timestamps — has a concrete failure mode: clock
skew. If node A's clock is correct and node B's clock is running fifteen minutes fast, an edit
made on B *before* A's edit (in real time) can still be stamped *later* than A's edit. A naive
wall-clock comparison would then let B's older edit incorrectly win, silently discarding A's
genuinely more recent change. This is the concrete problem that motivated moving to a logical
clock instead of trusting wall-clock time directly.

Both the schema and the sync layer keep this concern strictly separate from human-facing
timestamps: `tbl_article`, `tbl_folder`, and `tbl_media` (see
`libs/BeeMemoryBank.Storage/Migrations/001_initial_schema.sql`) each carry ordinary
`created_at`/`updated_at` wall-clock columns *and* a separate `lamport_ts` column. The ordering
timestamp used for conflict resolution is never the thing shown to a user as "when this
happened."

## Evaluated Options

1. **Naive wall-clock LWW** (raw `DateTime.UtcNow` comparison). Simple, but directly vulnerable
   to the clock-skew failure described above: a device with a fast clock can make an older edit
   look newer than it is, discarding real changes.
2. **Hybrid Logical Clock (HLC).** Combines a physical-time component (a wall clock kept within
   a bounded skew) with a logical counter, so the resulting timestamp is both causally ordered
   *and* stays close to real wall-clock time — useful when something downstream needs a
   timestamp that is both "happened-before correct" and "approximately when, in real time."
   HLC would have solved the same clock-skew problem naive wall-clock LWW has, but at the cost
   of a physical-clock component, a skew-bound assumption, and a two-part comparison rule.
   Nothing in BeeMemoryBank's actual conflict-resolution semantics needs the timestamp to be
   physically meaningful — the human-readable "when" is already carried separately by
   `created_at`/`updated_at`, and article-body TTL/expiry (the other classic use for HLC's
   physical component) is not a feature this sync layer has.
3. **Lamport clock** (chosen) — a single monotonically increasing counter per node, with no
   physical-time component at all.

## Decision
Sync ordering uses a plain Lamport clock (`libs/BeeMemoryBank.Sync/LamportClock.cs`): `Tick()`
increments the counter for a local event; `Update(remoteTs)` folds in an incoming event as
`max(local, remote) + 1`. Conflict resolution (`libs/BeeMemoryBank.Sync/ConflictResolver.cs`)
compares Lamport timestamps directly — the higher timestamp wins — and, on an exact tie, breaks
the tie deterministically by comparing `node_id` strings ordinally.

### Justification
- **What a Lamport clock actually guarantees.** If event A causally precedes event B (B's node
  had already observed A, directly or transitively, before producing B), then A's Lamport
  timestamp is strictly less than B's. This is a "happened-before" guarantee only — it says
  nothing about two events that never causally interacted; those can end up compared in either
  order, and that's fine, because they're genuinely unrelated in time. A Lamport clock does not
  produce a true global ordering of unrelated events, and it does not approximate real wall-clock
  time in any way.
- **Why that's sufficient here.** LWW only needs a rule for "which edit sticks" when two nodes
  touch the same article or folder, and that rule needs to be *deterministic* — every node must
  reach the same conclusion regardless of the order in which it happens to observe the two
  events. `ConflictResolver` achieves exactly that: Lamport timestamp first, then a fixed
  string-comparison tiebreak on `node_id` for genuine concurrency. Nothing about resolving a
  conflict between two article edits requires knowing which one happened closer to a real
  clock — only which one the other node already knew about, and a way to break a true tie the
  same way everywhere.
- **What HLC would have added, and why it isn't needed.** HLC's value over a plain Lamport
  counter is a timestamp that stays within a bounded distance of physical time, so it can double
  as a causal-order token *and* a real-world "approximately when" value. BeeMemoryBank has no
  consumer for that second property: the sync layer's only use of the ordering value is
  conflict resolution, and human-facing timestamps are handled entirely by the separate
  `created_at`/`updated_at` columns. Adopting HLC would add a physical-clock component, a
  bounded-skew assumption, and a two-part comparison — complexity with no corresponding
  behavioral change in this system's actual conflict outcomes.
- **The clock-skew problem this solves, concretely.** Because Lamport timestamps are derived
  from observed causality (a node's counter only advances on its own local events or on
  processing an event it received from another node), the local wall clock never enters the
  comparison at all. If node B never received node A's edit before making its own, the two
  edits are Lamport-concurrent and resolution falls to the deterministic `node_id` tiebreak —
  regardless of how far B's system clock happens to be skewed from A's. Skew simply has no
  channel through which it can affect the outcome.
- **The implementation still needed its own hardening**, which is worth noting precisely
  because "simpler than HLC" does not mean "trivial." `Update(remoteTs)` clamps the incoming
  remote timestamp to `current + 10,000,000` before folding it in, so a malicious or buggy peer
  sending an extreme value (e.g. `long.MaxValue`) cannot overflow the local counter and corrupt
  it permanently. `Initialize(maxKnownTs)` restores the counter as `max(current, maxKnownTs)`
  rather than overwriting it outright, so that any `Tick()`/`Update()` calls racing with startup
  can't be clobbered by a stale value read from disk.

## Consequences and Trade-offs
- Lamport timestamps by themselves carry no information about real elapsed time between events;
  they must never be treated as a proxy for "how long ago" something happened. That job belongs
  entirely to `created_at`/`updated_at`, which are ordinary wall-clock values and are kept
  columnarly separate from `lamport_ts` for exactly this reason.
- Concurrent, causally-unrelated edits on different nodes can be ordered either way by the
  Lamport comparison; the system does not attempt to guess which one a user would have
  considered "more recent" in wall-clock terms. The deterministic `node_id` tiebreak guarantees
  every node agrees on the outcome, but the outcome itself is an arbitrary (if consistent)
  choice between two edits that have no causal relationship.
- If BeeMemoryBank ever needs sync-layer timestamps that are also physically meaningful (e.g.
  cross-node TTL/expiry logic keyed to real elapsed time), that would be a reason to revisit
  this decision in favor of HLC; no such requirement exists today.
