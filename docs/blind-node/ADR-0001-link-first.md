# ADR 0001: link the Api sources a blind node needs instead of copying them or keeping the role switch

Status: accepted (BMB-91, 2026-10-03). Revisit when the `Blind` assembly (stage 2) lands.

## Context

The blind node was the full Api in a role. Three alternatives were weighed.

1. **Copy** the needed code into new projects (`Blind.Core`, `Blind.Store`) and pin the copies with a drift test.
2. **Extract** a shared library out of Core/Storage/Sync and make the originals depend on it.
3. **Link** the original source files into new projects: same code, same types, one definition.

Three independent reviews of the first plan (copying) found what copying hides: the blind runtime closure is wide
(`EventApplier` applies replicated rows and takes ~24 repositories), the migration runner **deletes the ledger rows
of migrations it has no SQL for** (so a trimmed schema is dangerous, not just different), and the startup, CLI and
console contracts carry security behaviour that is easy to lose in a rewrite.

## Decision

Link. A blind project compiles the original `.cs` files (`<Compile Include=... Link=...>`); nothing is copied, nothing
in Core/Storage/Sync/Crypto/Search/Api changes, there is nothing to drift. The set of files is computed by the
compiler (`tools/blind-link`): start from the blind surface, build, link the file that declares each missing name,
repeat. Files a blind node must never contain are on an exclusion list; if the closure needs one, the build fails
and the host gets a replacement instead.

The host's own composition root, pipeline and startup tasks are written out (not switched by a role at runtime), so
the full node's registrations are absent rather than skipped. The full, byte-identical set of SQL migrations stays
embedded.

## Consequences

* Behaviour is the original's by construction; tests written for the Api in the blind role run unchanged against the
  new host (221 do) and a golden route list is shared by both.
* A change to a linked Api file is a change to the blind node: `blind_link.py --check` and the composition guards run
  in CI.
* Milestone A (this host, still referencing the existing libraries) already removes ~860 MB of programs from the
  image (Api 290 MB + Cli 279 MB + their native assets for every platform, replaced by 12.8 MB). Milestone B links the
  library closure into a `Blind` assembly (265 of the 435 files of Core/Sync/Storage/Crypto/Search, about 24 % fewer
  lines) so the Android blind app and the Linux node share one small core.
* Partial classes are linked whole; DI wiring files and global statics (`DapperConfig`, `EventWriteGate`) are not
  visible to a compile closure and are covered by tests instead.
