# bmb-searchbench

A console tool that drives a **real running `BeeMemoryBank.Api` instance** (not an in-process
TestServer — you want to measure the same code path a real deployment serves) with search queries
and reports latency/throughput statistics. Built for the `search-100k` initiative so every later
work package can answer "did this actually get faster, and by how much, at realistic scale" with
real numbers instead of vibes.

The harness:

1. Builds/seeds a **scratch** vault (shelling out to `bmb-seedgen`) or reuses one you point it at.
2. Launches `BeeMemoryBank.Api` as a real child process against that scratch data directory on a
   free loopback port.
3. Waits for `/health`, unlocks the session with the seed password, runs the benchmark scenarios.
4. Tears the Api down cleanly (stdin-lifeline graceful stop, hard-kill fallback) on exit, including
   on error or Ctrl-C.
5. Writes one JSON file per scenario under `_docs/search-100k/baseline/` and prints a summary table.

## Usage

```bash
# Smoke run: tiny corpus, only title+content scenarios, short mixed duration
bmb-searchbench --seed-articles 500 --seed-folders 20 --scenarios title,content --mixed-duration 10

# 10k baseline (all scenarios)
bmb-searchbench --seed-articles 10000 --seed-folders 200

# 100k baseline, the initiative's target scale
bmb-searchbench --seed-articles 100000 --seed-folders 2000

# Reuse an existing seeded scratch dir (no seeding step)
bmb-searchbench --data-path C:\Temp\bmb-bench\corpus-100k --corpus-size 100000
```

Run `bmb-searchbench --help` for the full flag list.

### What gets seeded

When you pass `--seed-articles N --seed-folders M`, the harness invokes `bmb-seedgen` against the
resolved data directory with `--seed 42 --locale ru,en --password test1234` (all overridable). At
the SeedGen's measured throughput of ~161 articles/sec, a 100k corpus takes ~10 minutes; budget
for that. The harness prints seedgen progress lines prefixed with `[seedgen]`.

If `--data-path` is omitted with `--seed-articles`, the harness creates a scratch directory under
`%TEMP%\bmb-searchbench\<label>-<timestamp>\` and deletes it on exit (unless `--keep-data`).

## Safety: never point this at a real vault

This tool launches an Api process against the data directory, **unlocks it with a known password**,
and fires search queries at it. Pointing it at a real user vault would let the benchmark read real
private content (and, via the optional seed step, potentially write into it). The harness therefore
**refuses to run** against paths that look like a real install. Two layers:

### Hard refusal (never overridden, not even by `--allow-data-path`)

The harness aborts with exit code 3 if the data path:

- contains a path segment named `BeeMemoryBankData` (the real install root on every platform);
- is at or under the well-known data root:
  - Windows: `%LOCALAPPDATA%\BeeMemoryBankData` or `%APPDATA%\BeeMemoryBankData`
  - macOS: `~/Library/Application Support/BeeMemoryBankData`
  - Linux: `~/.local/share/BeeMemoryBankData`
- is the bare user-profile directory, or a direct child of it named `Documents`, `Desktop`,
  `Downloads`, `Pictures`, `Videos`, `Music`, or `OneDrive`.

### Soft refusal (overridable with `--allow-data-path`)

The harness also refuses paths that don't look scratch-like:

- not under the system temp directory (`%TEMP%` / `/tmp` / …), **and**
- containing no benchmark marker segment (`searchbench`, `bmb-searchbench`, `search-bench`,
  `bmb-bench`, `bench-scratch`, `bmb-scratch`).

Pass `--allow-data-path` to override the soft check (e.g. when you keep scratch dirs under a custom
location). It does **not** override the hard refusal above — those always abort.

## Scenarios

All four scenarios report the same shape of stats (p50/p95/p99 latency, throughput, error count)
so baselines are directly comparable.

| Scenario   | Endpoint                              | What it exercises                                                                                  |
| ---------- | ------------------------------------- | -------------------------------------------------------------------------------------------------- |
| `title`    | `GET /api/search?q=...`               | Metadata (title/folder/tag) search — SQL `LIKE`-style via `unicode_contains`. Fast path.           |
| `content`  | `GET /api/search?q=...&content=true`  | Body/content search — the **linear scan** in `SearchService` that decrypts every active body. Slow. |
| `semantic` | `POST /api/search/semantic`           | Embedding nearest-neighbour search. Only meaningful once the embedding backfill has populated projections. |
| `mixed`    | all three, weighted 50/30/20          | N concurrent clients (default 20, matching the initiative's target user count) for a fixed duration. |

The closed-loop scenarios (`title`, `content`, `semantic`) run a fixed query mix — a mix of
"frequent" terms (Zipf-popular topic words / common English words) and "rare" ones — with a
configurable number of warmup + measured requests per query, single client. The `mixed` scenario
spawns N workers each issuing a random weighted query with a 25–100 ms think time until the duration
elapses.

### Query mixes

Picked from `tools/BeeMemoryBank.SeedGen`'s sources so selectivity is predictable:

- **Title/metadata:** topic words used as folder segments and title prefixes (`Engineering`,
  `Security`, `Review`, `Architecture` frequent; `PenTests`, `Accessibility`, `Rollbacks` rare).
- **Body/content:** BERT-vocab English words that appear in bodies (`the`, `system`, `data`
  frequent; `performance`, `infrastructure` rarer). Note: a body search **always** decrypts the
  whole corpus; the query only changes how many bodies match (response size), not the scan cost.
- **Semantic:** short natural-language phrases (`incident response runbook`, etc.).

### Embeddings readiness (semantic scenario)

`bmb-seedgen` does **not** produce embeddings — the Api's background embedding processor does that
after the articles exist. The harness polls a fixed frequent semantic query until its result count
stabilises (≥2 consecutive polls with no growth) or `--semantic-wait` (default 180s) elapses, then
runs the benchmark and records the wait time and final probe count in the JSON. If no embeddings
appear within the wait, the scenario still runs but the report flags the results as unreliable.

## Output

One JSON file per scenario, per run, written to
`_docs/search-100k/baseline/<scenario>-<corpus-size>-<label>.json` (this directory is gitignored —
the files are local artifacts for the review process, not committed). The filename corpus-size is
the `--seed-articles` value, or `--corpus-size`, or `existing`. The label defaults to a UTC
timestamp; override with `--label`.

Each file has the shape:

```jsonc
{
  "scenario": "content",
  "corpusSizeLabel": "100000",
  "startedAtUtc": "2026-08-12T...",
  "endedAtUtc":   "2026-08-12T...",
  "durationSeconds": 184.3,
  "totalRequests": 100,
  "successCount": 100,
  "errorCount": 0,
  "latencyP50Ms": 1840.2,
  "latencyP95Ms": 2120.5,
  "latencyP99Ms": 2310.1,
  "latencyMinMs": 1700.0,
  "latencyMaxMs": 2310.1,
  "latencyMeanMs": 1880.4,
  "throughputReqPerSec": 0.54,
  "concurrency": 1,
  "perQuery": [
    { "query": "the", "expectation": "frequent", "samples": 20,
      "p50Ms": ..., "p95Ms": ..., "p99Ms": ..., "meanMs": ...,
      "resultCount": 48213 }
  ],
  "note": null
}
```

A human-readable summary table (plus per-query detail) is also printed to stdout.

### Reading the numbers

- **Latency percentiles** are in milliseconds, computed by linear interpolation (R-7) over the
  sorted measured latencies.
- **Throughput** for closed-loop scenarios is `totalRequests / wallClockSeconds` for the measured
  phase (excluding warmup). For the mixed scenario it is the same ratio over the fixed duration.
- **resultCount** in the per-query breakdown is the article+folder count of the last successful
  response — an approximate indicator of selectivity, not a precise total.
- **errorCount** in the mixed scenario counts non-2xx responses (timeouts, 5xx under load).

### Allocations

The brief allows skipping live GC-allocation measurement if wiring it out of a separate process is
too heavy for this WP. This harness skips it (a per-request allocation probe across a process
boundary is out of scope for a no-new-dependency tool). Latency/throughput are the load-bearing
numbers; allocation measurement is left as a gap for a later WP.

## Process hygiene

The Api is launched with `BMB_STDIN_LIFELINE=1` and a redirected stdin; on exit the harness closes
that stdin to trigger a graceful `IHostApplicationLifetime.StopApplication()` (session lock + DEK
wipe + Kestrel drain), waits up to 15 s, then hard-kills the whole process tree with a bounded
`WaitForExit` follow-up — the kill+bounded-wait pattern from `AGENTS.md`. This runs in `finally` /
`DisposeAsync`, so it applies on success, error, and Ctrl-C. If the harness created the scratch data
directory itself, it deletes it on exit unless `--keep-data` is set.

The Api's own stdout/stderr are captured to
`<data-path>/../searchbench-logs/<label>/api-stdout.log` and `api-stderr.log` for post-mortem
inspection.
