# Two-node test mesh (`test1` / `test2`)

A permanent, throwaway pair of nodes that sync with each other, living on the owner's Hetzner box
next to (but strictly isolated from) the real node. It exists to catch the class of bug the
automated suite cannot see, because every test there runs against a fresh in-memory database inside
one process:

| Only reproducible here | Why the suite misses it |
|---|---|
| A migration meeting data that already exists | Test databases are always empty at migration time |
| Docker networking, reverse proxies, `X-Forwarded-For` | No containers, no DNAT |
| Sync across restarts, lag, an offline peer | Test processes live for seconds |
| Anything touching disk: media, snapshots, compaction | Temp directories, torn down immediately |
| The real web UI and a real MCP client | No browser, no live agent |

The concept-tag bug in `6392fc0a` — every article in a list response reporting zero tags, since the
May release — was found here within ten minutes of the mesh coming up, by noticing that the list
route and the single-article route disagreed about the same article.

## Where it lives

```
Hetzner:/home/evgeny/bmb-test/
├── docker-compose.yml     ← copy of this directory's file
├── seed.sh                ← copy of this directory's file
├── models/model.onnx      ← one shared copy, mounted read-only into both nodes
└── src/                   ← its own git checkout, never the one under projects/
```

Docker's data-root on that host is `/mnt/HC_Volume_105418619/docker` (a separate 20 GB volume), so
images and the `test1-data` / `test2-data` volumes do not consume the root filesystem.

## Isolation from the real node

The real node runs as compose project **`hetzner`** out of `projects/BeeMemoryBank/deploy/hetzner/`.
This one pins **`name: bmb-test`** in its compose file, so any command run from `bmb-test/` — up,
down, even `down -v` — can only ever address `test1` and `test2`. Different project, different
volumes, different ports, different checkout. Never run compose commands for one from the other's
directory.

## Access

Every port is bound to `127.0.0.1` on the server **on purpose**: these nodes share one well-known
master password, and publishing them would be handing the vault to the internet. Reach them from
any machine with a tunnel:

```bash
ssh -N -L 5011:127.0.0.1:5011 -L 5012:127.0.0.1:5012 \
       -L 5013:127.0.0.1:5013 -L 5014:127.0.0.1:5014 Hetzner
```

Then locally:

| | Web UI | API + `/mcp` |
|---|---|---|
| test1 | http://localhost:5011 | http://localhost:5013 |
| test2 | http://localhost:5012 | http://localhost:5014 |

Master password: `TestNode-1-Pass!2026`. Admin user `admin` on both.

The API needs an internal key on every request; read it off the node itself:

```bash
KEY=$(docker exec test1 cat /app/data/.internal-key)
curl -H "X-Internal-Key: $KEY" -H "X-User-Role: superadmin" -H "X-User-Id: 1" \
     http://127.0.0.1:5013/api/articles
```

## Rebuilding after a code change

The build is capped at 1.5 of the host's 2 cores so it cannot starve the owner's real node. A cold
build takes **about three minutes**; incremental ones are faster.

```bash
ssh Hetzner
cd ~/bmb-test/src && git fetch origin && git reset --hard origin/master
cd ~/bmb-test
docker buildx build --builder bmbtest --load -t bmb-test:latest src/
docker compose up -d
```

The `bmbtest` builder is a `docker-container` buildx instance whose CPU and memory ceilings were set
with `docker update`. If it ever disappears, recreate it — and re-apply the caps, because a plain
`buildx create` has none:

```bash
docker buildx create --name bmbtest --driver docker-container --bootstrap
docker update --cpus=1.5 --memory=3g --memory-swap=4g buildx_buildkit_bmbtest0
```

## Resetting to a known state

```bash
cd ~/bmb-test
docker compose down -v          # only ever touches bmb-test
docker compose up -d
# then re-initialise and re-seed — see "First-time setup" below
```

## First-time setup (what was done once, kept for reproducibility)

```bash
K1=$(docker exec test1 cat /app/data/.internal-key)
K2=$(docker exec test2 cat /app/data/.internal-key)
P='TestNode-1-Pass!2026'
J='Content-Type: application/json'

# test1 becomes a standalone node
curl -X POST http://127.0.0.1:5013/api/init/standalone -H "$J" -H "X-Internal-Key: $K1" \
     -d "{\"adminUsername\":\"admin\",\"displayName\":\"test1\",\"password\":\"$P\"}"

# and must be UNLOCKED before it can accept a join: the join writes a signed whitelist event,
# which needs the master DEK. A locked node answers the joiner with a bare 403 that says nothing
# about the real reason — worth remembering, it costs ten minutes otherwise.
curl -X POST http://127.0.0.1:5013/api/session/unlock -H "$J" -H "X-Internal-Key: $K1" \
     -d "{\"password\":\"$P\"}"

# test2 joins over the compose network
curl -X POST http://127.0.0.1:5014/api/init/join -H "$J" -H "X-Internal-Key: $K2" \
     -d "{\"adminUsername\":\"admin\",\"displayName\":\"test2\",\"remoteUrl\":\"http://test1:5300\",\"password\":\"$P\"}"

# test1 does not learn how to reach test2 from a join — give it the address so sync is two-way
NODE2=$(curl -s -H "X-Internal-Key: $K1" -H "X-User-Role: superadmin" \
        http://127.0.0.1:5013/api/whitelist | grep -o '"nodeId":"[^"]*"' | head -1 | cut -d'"' -f4)
curl -X PUT "http://127.0.0.1:5013/api/whitelist/$NODE2/address" -H "$J" -H "X-Internal-Key: $K1" \
     -H "X-User-Role: superadmin" -d "{\"newApiAddress\":\"http://test2:5300\",\"password\":\"$P\"}"

./seed.sh
```

After the join, the trust asymmetry from the September trust-model change is visible directly, and
is the fastest way to confirm a build has it: `test1` lists `test2` with `isSuperadmin: false`
(a content-only peer), while `test2` lists `test1` with `isSuperadmin: true` (the node it entered
through, trusted on first use).

## The seeded corpus

`seed.sh` creates 200 articles over ten folders, mixing Russian and English bodies — the embedding
model is multilingual, and an all-English corpus hides tokenizer problems. Every article carries a
`seed` tag plus one naming its folder, so `bee_search_by_tag` has something predictable to return.

```
/Public/Docs (25) · /Public/Docs/API (20) · /Public/Notes (25 ru)
/Private/Personal (20 ru) · /Private/Finance (15)
/Projects/Alpha (25) · /Projects/Alpha/Specs (15) · /Projects/Beta (20 ru)
/Archive/2025 (25) · /Archive/2024 (10 ru)
```

Seed `test1` only. `test2` must get its copy by sync — seeding it directly makes the two nodes
diverge for reasons that have nothing to do with whatever is being tested.

## Rules

- **Never point this at the real node**, and never run backup or restore drills there. That is what
  this environment is for.
- Sync takes tens of seconds, not milliseconds. Poll for a result; do not assert immediately.
- Treat both nodes as disposable. If one is in a strange state, reset rather than repair — a
  half-fixed node makes the next person's results meaningless.
