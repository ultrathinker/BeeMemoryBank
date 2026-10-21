# Deployment Guide

## Environment Variables

| Variable | Component | Description |
|---|---|---|
| `BMB_DATA_PATH` | API, CLI | Path to the data directory (database, snapshots) |
| `ASPNETCORE_URLS` | API, Web | Bind URL (e.g., `http://localhost:5300`) |
| `ASPNETCORE_ENVIRONMENT` | API, Web | Production / Development |
| `BMB_API_URL` | Web | Internal API URL (e.g., `http://localhost:5300`) |
| `BMB_MCP_URL` | Web | Optional. The MCP address the Profile page prints in the connection snippet for an AI assistant (for example `https://bee.example.com/mcp`). A bare origin gets `/mcp` appended; it must be `http://` or `https://`, with no user name, query or fragment. Normally unset: the page works the address out itself (see *AI assistants and the MCP address* below). Set it when your reverse proxy forwards `/mcp` under a name the page cannot see, or to print one fixed address whatever the page is opened from. |
| `BMB_API_PUBLISHED_PORT` | Web | Optional. The host port the API (5300) is published on, on the host's loopback. `docker-compose.reverse-proxy.yml` sets it; in `docker-compose.yml` it goes with the commented API port line. The Profile page then prints `http://127.0.0.1:<port>/mcp`, which is right for an AI assistant on the same computer. |
| `BMB_WEB_BIND` | Docker host (`.env`) | Optional, default `127.0.0.1`. The host address `docker-compose.yml` publishes the Web port on. The default keeps the login page on this computer only; `0.0.0.0` exposes it on every network interface of the host, on purpose (see below). |
| `BMB_INTERNAL_KEY` | API, Web | Shared secret for Web→API authentication. Every request from Web to API must carry this key in the `X-Internal-Key` header. **In Docker:** `docker-entrypoint.sh` auto-generates and exports the key before starting both processes — you do not need to set it manually. **From source (separate processes):** you must set it explicitly and pass the same value to both API and Web (see below). The API refuses to start in Production if the key is missing. |
| `BMB_AUDIT_RETENTION_DAYS` | API | Optional. How long to keep `tbl_audit_log` rows. Default `90`. Set to `0` to disable pruning entirely. The pruning service runs ~24 h after process start and once a day after; it skips when the session is locked (no operator present to react to anomalies) and writes a meta-audit row recording the deletion so the prune itself shows up in the audit trail. |
| `BMB_COMPACTION_KEEP_COUNT` | API | Optional. How many of the most recent events survive an event-log compaction. Default `1500`, minimum `100` (a lower value is ignored and the default used instead). This number is also how far a peer may fall behind before compacting would strand it, so it trades two things off against each other: raise it and `tbl_event` stays large but a phone that was off for a week can still resume; lower it and the log shrinks but a peer that misses a busy day has to wipe and rejoin. With ~20 active writers 1500 events is roughly a day or two. Compaction refuses outright while any peer would be stranded and names them; `acceptCuttingOffPeers` on `POST /api/admin/compact` is the deliberate override, and it logs which peers it stranded. |
| `BMB_AUTO_UNLOCK_KEY_FILE` | API | Optional, and **only a pointer**: the path of the file that holds the key of *Start unlocked after a restart* once the owner has switched that on in the app (see the section of that name below). Setting it switches nothing on: with the checkbox off the node logs *key file configured, auto-unlock is off* and never opens the file, and a key file that merely appears is ignored. Keep the file **outside** the data folder and out of every backup, and mount it read-only (a Docker secret); it must not name `<data>/server-auto-unlock.key`. The node reads it and never writes, moves or deletes it. 64 hexadecimal characters, readable by the node's user only. |
| `BMB_SEALED_INBOX_MAX_MB` | API | Optional. On an encrypted node: how many megabytes of changes and attachments a **locked** node keeps for its peers before it answers *507 Insufficient Storage* (default `2048`; it also refuses when less than 1 GiB is free on the data drive). The peers retry later, nothing is lost. See *A Locked Node Still Receives Changes* below. |
| `BMB_PROTECTED_UNLOCK_SECONDS` | API | Optional. How long the node remembers the password of a password-protected article after you unlock it, so that its page and its editor open without asking again and a save does not ask either. Default `900` (15 minutes); allowed `30` to `3600`, anything else is ignored. A short time (for example `120`) is for trying what the editor does when the time runs out: it shows "Temporarily unlocked" with the time left over the text and, when the time is out, asks for the password in a dialog on Save. This is the standard time: a person can choose another for one article (15 minutes to 8 hours, the **change** link of the strip), and that choice lasts until the article locks. |
| `BMB_TRUSTED_PROXIES` | API, Web | **Set this whenever a reverse proxy (including Docker port publishing) sits in front of the node.** Comma- or whitespace-separated IP addresses and/or CIDR networks whose `X-Forwarded-For` header is believed — e.g. `172.16.0.0/12` for the Docker bridge range. Without it every per-IP rate limit keys on the proxy's own address, so all clients share one bucket: a single anonymous caller can exhaust the sync-challenge budget and stall synchronization for the whole mesh, or the login budget for every user at once. Only one hop is trusted (`ForwardLimit = 1`), and an unparsable entry is logged and ignored rather than failing startup. Trust here is transitive — anything that can reach the port from a listed address can claim any client IP — so name the proxy or its network and nothing wider. Both processes announce what they ended up trusting on startup — `[forwarded-headers] Trusting X-Forwarded-For from: …`, or a line saying nothing is configured — so check the log after changing this rather than assuming it took. |
| `BMB_TRUST_LOOPBACK_FORWARDED_HEADERS` | API, Web | Optional, `true`/unset. Believe `X-Forwarded-For` from loopback. For a proxy sharing the host with the node (the desktop app sets this itself). Independent of `BMB_TRUSTED_PROXIES`; either or both may be set. Not sufficient under Docker — published-port traffic arrives on the bridge interface, never on loopback. |

## Example Node Setup (Docker)

```
Container: bmb  (docker compose)
Web:  127.0.0.1:5301  → container :5301   (published, this computer only)
API:  container :5300                     (NOT published — see below)
Data: /var/lib/beememorybank  (bind mount to /app/data), or the Docker volume bmb-data
Image: ghcr.io/ultrathinker/beememorybank:<version> (docker-compose.image.yml),
       or a multi-stage build from Dockerfile (docker-compose.yml)
```

### The ready-made image

Released versions are on GitHub's container registry: `ghcr.io/ultrathinker/beememorybank` (full node) and
`ghcr.io/ultrathinker/beememorybank-blind` (blind node, see [docker/blind/README.md](../docker/blind/README.md); several meshes on one machine are several containers, see
[blind-node/README.md](blind-node/README.md#several-memory-banks-on-one-machine-docker)), each for
`linux/amd64` and `linux/arm64`. Tags: `X.Y.Z`, `X.Y` (the newest patch of that line) and `latest`; a pre-release
(`2.4.0-rc1`) gets only its own tag. `docker-compose.image.yml` runs it with the same safety defaults as
`docker-compose.yml`; `BMB_VERSION` in `.env` picks the tag. The image carries everything: the Api, the Web front, the CLI
(`/app/cli/bmb.dll`) and the search model (`/app/api/model.onnx`, checked against its SHA-256 when the image is built). A
`model.onnx` in the data folder is still used first, as a repair copy; `BMB_ONNX_MODEL_PATH`, when set, wins over both.

By default the data is in the Docker volume `bmb-data` (scoped to the compose project: `docker volume ls` shows
`<folder>_bmb-data`). `BMB_DATA_PATH=./data` (or an absolute path) in `.env` keeps it in a host folder instead; set it
before the first start. Either way the container runs as root, as the image built from source always did, so a host
folder ends up owned by root.

**One node per data folder.** The Api and the blind node lock the data folder (`.instance.lock` inside it) for as long as they run, and a
second one on the same folder refuses to start (after waiting up to 15 seconds for a predecessor that is still shutting down):
two nodes on one folder would write different events under one node id, and the mesh could not tell them apart. A container
that is left running, a second `docker compose` project on the same volume, or a restore script that starts a node on the
live folder all get that refusal. `bmb` commands do not take the lock and may run next to a node. The lock is a file lock: it
works for a Docker volume, a bind mount and a local disk; a folder on a network share that two different computers mount
(NFS, SMB) is not protected, so do not point two nodes at one.

Switching an existing source deployment (`docker-compose.yml`, data in `./data`) to the image: put `BMB_DATA_PATH=./data` into
`.env` first. Without it the image's compose file uses the volume, and the node opens empty on Setup while your bank sits
untouched in `./data`. The volume is named after the compose project (the folder): renaming or moving the folder starts a
new one, so keep the folder name or pass `-p <old project name>`.

Behind your own reverse proxy: copy `docker-compose.reverse-proxy.yml` and replace its `build:` block with
`image: ghcr.io/ultrathinker/beememorybank:${BMB_VERSION:-latest}`; nothing else changes.

Updating: make a snapshot (Admin → Snapshots), then `docker compose pull && docker compose up -d`. Data migrations only go
forward, so going back to an older tag means restoring that snapshot. A node on a version before 2.6 updates to 2.6.1 first and
to this version after that (this version refuses an older database and leaves it unchanged). The in-app "Apply update" button belongs to a
source deployment with its own update script (`scripts/bmb-self-update.sh`); with the image, update with `docker compose pull`.

The images are built by `.github/workflows/docker-publish.yml` when a release is published (the maintainer's approval of
the draft release), smoke-tested on each architecture before anything is tagged, and carry a GitHub build provenance
attestation: `gh attestation verify oci://ghcr.io/ultrathinker/beememorybank:X.Y.Z --owner ultrathinker`.
`scripts/smoke-docker.sh <image> <full|blind> <version>` is the same start-up test, for an image you built yourself. It
removes its container when the run ends, whether it succeeds, fails, or is interrupted; set `BMB_SMOKE_KEEP=1` to keep
the stopped container around for `docker logs` (the last line names it).
`scripts/test-entrypoint-term.sh <image> [rounds] [entrypoint]` starts the container and sends it SIGTERM a few milliseconds after
the start, round after round: each round must stop by itself, so a `docker stop` never has to end in SIGKILL. The
full node is the default; for the blind node image pass its entrypoint: `scripts/test-entrypoint-term.sh <blind-image> 40 /app/entrypoint.sh`.

### Exposure

The shipped `docker-compose.yml` deliberately publishes only the Web port, and only on this
computer's loopback (`127.0.0.1`) — the right choice for a purely local node. Docker's port
publishing skips `ufw`, so a mapping without a host address would put the login page on every network
interface of the host. (The desktop apps' "Devices on my network" switch, Admin → Devices on my network, does not apply to Docker: here the
published ports are the whole answer; see [internet-access.md](internet-access.md#do-you-need-this).) To expose it on purpose (a trusted LAN), set `BMB_WEB_BIND=0.0.0.0` in `.env`
and put TLS in front of it: the session cookie is `Secure`, so sign-in over plain HTTP from another
computer does not work anyway. For a node that must be reachable from other nodes or MCP agents, use
**`docker-compose.reverse-proxy.yml`** instead: it publishes both ports bound to the host's
loopback, for a reverse proxy to sit in front of.

```bash
docker compose -f docker-compose.reverse-proxy.yml up -d
```

Never publish the API port on `0.0.0.0`. Docker's port publishing writes DNAT rules directly and
bypasses `ufw`, so a `0.0.0.0` mapping is internet-reachable even on a host you believe is
firewalled — and the API surface includes a master-password oracle (see below). Personal
per-server compose files live under `deploy/`, which is gitignored; keep them in step with the
reverse-proxy reference file, because nothing in CI can check a file that isn't in the repository.

### AI assistants and the MCP address

`/mcp` is served by the API (port 5300), not by the Web page you browse. The Profile page shows a
ready-to-paste connection snippet when you create an agent key, and it prints the address like this,
first match wins:

1. `BMB_MCP_URL`, if you set it.
2. The address you browse from, when the page is served by the desktop app, the Windows service or
   the Mac app: their front door serves `/mcp` on the same address as the page.
3. The address you browse from, when the page is opened through a reverse proxy (it sent
   `X-Forwarded-Host` or `X-Forwarded-Proto`): the recipes in
   [internet-access.md](internet-access.md) forward `/mcp` to the API.
4. `http://127.0.0.1:<BMB_API_PUBLISHED_PORT>/mcp`, when the API port is published on the host's
   loopback (`docker-compose.reverse-proxy.yml` does; right for assistants on the same computer).
5. Nothing: the page says that AI assistants cannot reach this node yet, instead of a dead address.
   That is the case for the default `docker-compose.yml`, which does not publish the API port.

To switch assistants on for a default Docker node, uncomment the two marked lines in
`docker-compose.yml` (the loopback API port and `BMB_API_PUBLISHED_PORT`), or use
`docker-compose.reverse-proxy.yml` and a proxy that forwards `/mcp`.

## Reverse Proxy — What Is Exposed

Only the following endpoints should be publicly accessible:
- `/mcp` — MCP server (authentication via Bearer token at the application level)
- `/api/sync` — synchronization between nodes (Ed25519)
- `/api/join` and `/api/join/abort` — join protocol (the second one is how a node whose join failed takes back the row it left
  here; a proxy that does not forward it only makes that node tell its user which row to revoke)
- `/api/blind/replica` — only if blind copies (the Windows, macOS and Android blind apps) should be able to call this node
  (see "Blind copies can call this node" below)

Everything else (including `/api/articles`) should be restricted to trusted IPs or localhost.

**How the application enforces this.** The node keeps its own list of what a caller without
`BMB_INTERNAL_KEY` may reach — `PublicSurface` in the source, covering `/mcp`, `/api/sync/*`,
`POST /api/join`, `POST /api/join/abort`, `GET /api/blind/replica`, the snapshot-file and restore-progress routes, `/health` and
`GET /api/version`.
Anything else answers `404` to a keyless caller: not `403`, because "this endpoint exists but you may
not use it" is itself worth knowing to someone probing your node. The web UI and the desktop tray
present the internal key and are unaffected.

This matters because several endpoints deliberately skip the internal-key check — they have to be
callable before a session exists — and before the node enforced its own list, a mistake in the proxy
configuration was the only thing between them and the internet:

- `POST /api/session/unlock` — *processes* a master-password attempt, and a correct guess unlocks the
  vault **globally, for every user and agent on the node**, not just for the caller.
- `GET /api/session/status` — leaks whether the vault is currently unlocked.
- `POST /api/join` — master password grants mesh membership. This one is published on purpose; see the
  Trust Model section of [SECURITY.md](../SECURITY.md) for what a joined node can then do.
- `POST /api/join/abort` — the same master password, to take back the row of a join that failed on the joining node
  (it removes only the never-synced row of that attempt; see "Joining is authorised by the master password" in
  [SECURITY.md](../SECURITY.md)). Published with `/api/join`, rate-limited like it.

**Optional: guest accounts for other people.** Three more routes are public by design but forwarded by no
shipped configuration: `POST /api/auth/remote-token`, `GET /api/folders/accessible` and
`GET /api/folders/by-path/snapshot`. They exist so that another person's node can mirror folders of this one with a
user name and password (a "remote account"). They stay closed unless you add them to your proxy; what they mean
for security, what the owner has to provide and which messages the guest sees are in
[internet-access.md](internet-access.md#4-optional-guest-accounts-for-other-people). For Apache, add them before
`ProxyPass /`:

```apache
# OPTIONAL: guest accounts for other people (off unless you add this)
ProxyPassMatch "^/api/(auth/remote-token|folders/accessible|folders/by-path/snapshot)$" "http://127.0.0.1:5300/api/$1"
```

The "Forgot your password? Use a recovery key" flow adds no route to publish: `/RecoverAccess` is a page of the
Web UI (it travels to the Web port with the rest of the pages), and the API route behind it,
`POST /api/session/recover-access`, requires the internal key and is deliberately **not** in the node's public
surface. Do not forward it from the proxy.

**Keep the proxy path-filter anyway.** It is now the outer of two layers rather than the only one, and
it is the layer that stops the request before it reaches the application at all. Restrict the API port
to loopback and forward only `/mcp`, `/api/sync`, `/api/join`, `/api/join/abort`, `/api/blind/replica` (if you use blind copies) and
`/api/snapshots/restore` over TLS.

A node that receives a network-wide snapshot restore needs `GET /api/snapshots/restore/{id}/file`
forwarded (Bearer-authenticated with a sync token, like the rest of `/api/sync`). Without it the
restore initiator can publish the event but no peer can fetch the snapshot.

**Blind copies can call this node.** A blind copy never listens; it calls a node over https. For this node to be
that node, forward `/api/sync/*` (already there for other nodes) and `GET /api/blind/replica` — the signed package
of a copy's first load — and, in Admin on another node of the network, choose *Trusted Nodes → Let blind copies call
this node*: the address (https), and **Normal certificate** (a real certificate, e.g. Let's Encrypt behind your
proxy: the copy checks it through the system's certificate chain, name included) or **Pinned certificate** (the key
the node presents is recorded; every device then accepts only that key). The node checks the address and the
certificate before it saves. Apache:

```apache
# inside the VirtualHost, before "ProxyPass /": the API routes, then the Web UI
ProxyPassMatch "^(/mcp(?:/.*)?|/api/sync/.*|/api/join(?:/abort)?|/api/snapshots/restore/[^/]+/file|/api/blind/replica)$" "http://127.0.0.1:5300$1"
ProxyPass / http://127.0.0.1:5301/
```

Both blind routes need a sync token, which only a device that signed in with its own key (one you paired) gets, and
the whole whitelist row is re-checked on every request. What they serve is ciphertext plus the plaintext metadata
every node holds (titles, folder paths, tags; [ADR 0005](adr/0005-plaintext-metadata.md)) — never the master key. The
replica route also limits each device to 20 requests per ten minutes. A lost blind copy is revoked in Admin
(*Trusted Nodes → Revoke*), and its token stops working at once. Design and trade-offs:
[ADR 0007](adr/0007-blind-copies-call-a-full-node.md).

**Escape hatch.** `BMB_PUBLIC_SURFACE=off` disables the node-side gate if a deployment turns out to
need an endpoint the list does not know about. It is meant for a bad afternoon, not as a setting — the
startup log says which state the node is in. If you need it, please report which endpoint, so it can be
published properly instead.

## Resetting a Node

Wiping a node back to first-run Setup — every article, folder, user, key and sync-state row — is
available two ways. Both require the master password as confirmation; the password is verified
*without* unlocking the vault, so a rejected attempt leaves nothing open.

- **Web UI:** Admin → *Reset This Node* (`/Admin/ResetNode`). Superadmin only, and a human superadmin at that: the
  endpoint behind it also refuses agent bearer tokens, so a superadmin's MCP agent cannot wipe the
  node on its owner's behalf.
- **Host CLI:** `bmb init reset --master-password '<password>' --yes` — for when nobody can sign in
  any more (every superadmin account lost, or the Web layer itself broken). Under Docker:
  `docker exec -it <container> dotnet /app/cli/bmb.dll init reset --master-password '<password>' --yes`

Both write an append-only line to `<data>/reset-audit.log` before starting, because the wipe deletes
`tbl_audit_log` along with everything else — a record kept only inside the database could never
survive the event it describes.

**Restart the node after a CLI reset.** The CLI is a separate process operating on the same data
directory: it clears the tables, but the still-running API keeps the in-memory state it built from
the vault that no longer exists — its unlocked session (and therefore the old Master DEK) and its
process-wide caches. The Web UI path has no such gap, since it resets the very process serving it.
So under Docker, restart the container after the `docker exec` above, before opening `/Setup`.

> Earlier versions offered this from a *"Node out-of-sync? Reset & rejoin"* form on the anonymous
> Login page, and through an unauthenticated `POST /api-proxy/init/reset`. Both are gone: with the
> master password as the only credential, anyone who could load the login screen could grind it and,
> on a correct guess, destroy the node.

## Backing Up and Restoring the Data Folder

Two different things can be backed up:

- **Snapshots** (`<data>/snapshots/`, *Admin → Snapshots*, `bmb snapshot create`) are the node's own backups: one file each,
  encrypted under the master key, written complete and atomically. Copy them whenever you like, **while the node runs**.
  Bringing a node back from one is described in [snapshot-restore.md](snapshot-restore.md).
- **The data folder itself**, with restic, rsync, a volume backup or a file-system snapshot. Do this only with the node
  **stopped** (below).

### What is in the data folder

| File | What it is |
|---|---|
| `beememorybank.db` (and `-wal`, `-shm`) | The vault. Plain SQLite on a node that was never encrypted; encrypted pages on a node the owner encrypted (*Encrypt this node*). |
| `chat.db` | Chat, if the node has any. Encrypted the same way on an encrypted node. |
| `beememorybank.boot.db` | **Encrypted nodes only.** The *boot store*: a small plain SQLite file with what a locked node needs before anybody unlocks it. It holds copies of the ways in (the wrapped master keys of the key slots and of auto-unlock agents, with their salts), the wrapped database key, the node's id, name and public key, the list of peers (names, addresses, flags), and the changes and attachments peers sent while the node was locked. It holds no article, title, user name, password or open key. The encrypted database cannot be opened without it, so **a backup of an encrypted node always contains both**. |
| `snapshots/`, `media/`, `search-index-segments/`, `baselines/` | Encrypted already, on every node. Copy them in the same run as the databases when you restore a whole set. |
| `server-auto-unlock.key`, `server-auto-unlock.enabled`, `os-auto-unlock.dat` | Only when the owner switched an auto-unlock on. **Never in a backup** (see *Start Unlocked After a Restart* below). |
| `seal-journal.json`, `*.seal-new`, `*.seal-old*` | Only while *Encrypt this node* runs or after it was interrupted. Part of the set when present. |

### Stop, copy, start

The database files and the boot store change at different moments: locking or stopping the node rewrites the boot store and
checkpoints the databases, and a locked node keeps writing the boot store while peers send it changes. A copy of a **running**
node, locked or not, can therefore show a state no crash could produce. Stop the node first; a clean stop checkpoints and
closes everything, so the three files are each complete and belong to the same moment. Then copy the whole folder in one run,
including any `-wal` or `-shm` a crash left behind.

```bash
# Docker: "restart: unless-stopped" brings a stopped container back only when you start it again
docker compose stop bmb
restic backup /var/lib/beememorybank --exclude 'server-auto-unlock.*' --exclude os-auto-unlock.dat
# or: rsync -a --exclude 'server-auto-unlock.*' --exclude os-auto-unlock.dat /var/lib/beememorybank/ /backup/bmb-$(date +%F)/
docker compose start bmb
```

A volume backup (`bmb-data`) is the same: stop the container, back the volume up, start the container. The two `--exclude`
lines are part of the recipe: **a backup that carries the key that opens it is a plain copy** (see below). The update takes its own
pre-update copy for you, with the writes held for a few seconds, so an update does not need these steps.

### Restoring a set

1. Stop the node. Put **all** the files of one backup into the data folder; never a database from one set and a boot store
   from another.
2. Start the node. It comes up **locked**, and checks the set before it opens anything. A set that does not belong together
   (an encrypted database without its boot store, a boot store beside a plain database, a database cut inside a page, a plain
   `chat.db` beside an encrypted vault) is refused with a named reason and nothing is opened, created or deleted; the message
   names the files and the ways back.
3. Unlock it with the master password. Before the node meets its peers again, run `bmb vault verify`: it checks the databases
   and the boot store and prints `ok` or the check that failed.

A restored folder brings the node back **as it was at the backup moment, with its node id and its place in the event log**.
Whether that is safe for the other nodes is the question [snapshot-restore.md](snapshot-restore.md) answers; read it before
restoring a node that has peers. A set taken before *Encrypt this node* is a plain set: copied back it is simply the open node it was, and no
restore of the product (snapshot restore, network restore, the restore wizard) takes it. There is no way back to an open node.

If the boot store is lost and the database is not, the database cannot be opened. Restore a snapshot (it opens with the master
password and does not depend on the boot store), join again from a peer, or restore from a blind copy.

## Start Unlocked After a Restart (Servers)

By default a server that restarts stays **locked** until someone signs in with the master password. That is the safe behaviour and
it is what every node does after every update: the option below is **off** unless the owner switches it on, and while it is off
nothing of it runs (no key file is read, no code path of it is entered).

It exists for a server nobody is at to type a password. With it on, the key that opens the vault is kept **on the server itself**,
so anyone who gets the server's disk, a copy of its data folder or a backup of it can read the whole vault: the encryption of the
database no longer protects it. Turn it on only if you accept that.

**Roles.** The option belongs to a *server*: a node started by Docker, a service or `dotnet run`. The desktop app starts its node
as a *desktop* (it sets `BMB_HOST_ROLE=desktop` itself; nobody sets that by hand), and a desktop uses the *Auto-unlock* switch
of this computer instead (the Windows or macOS key store). On a server that switch (`POST /api/keys/auto-unlock/enable`) answers
*409 role_server*; on a desktop the server routes answer *409 role_desktop*. A plain server (one that was never encrypted) can
use the option too, with the same warnings.

**Turning it on** (superadmin, a person and not an agent, vault unlocked): *Admin → Database encryption* (the encryption page), or
`bmb vault auto-unlock enable`. It asks twice. First a warning with *Cancel* as the default; then the master password and the
phrase `START UNLOCKED`, typed exactly. The API route behind it is `POST /api/keys/server-auto-unlock/enable` with
`{ "password": "…", "confirmation": "START UNLOCKED" }`; `GET …/status` and `POST …/disable` sit beside it. An audit line
(`server_auto_unlock_enabled`, no names) is written.

**What it consists of.** Three things, all three must agree before the node reads or uses anything:

1. a key slot of type `server_auto_unlock` in the vault (the master key wrapped under a random 32-byte secret), one at most;
2. the secret itself, 64 hexadecimal characters in a **file**: the file named by `BMB_AUTO_UNLOCK_KEY_FILE` when that is set,
   otherwise `<data>/server-auto-unlock.key`, which the node creates for itself, readable by its user only. The node refuses to
   read a key file others can read (on Linux and macOS: no group or other permission bits; on Windows: an access list for the
   node's user only);
3. the *enable record* `<data>/server-auto-unlock.enabled`, written last. It holds no secret; it says "the owner enabled exactly
   this slot on this server". It is local to the server and is in no snapshot, baseline, package or backup.

**Putting the key file outside the data folder** (recommended; required for the "never in a backup" rule to be easy to keep):

```bash
openssl rand -hex 32 > bmb-auto-unlock.key && chmod 0400 bmb-auto-unlock.key
```

```yaml
services:
  bmb:
    environment:
      - BMB_AUTO_UNLOCK_KEY_FILE=/run/secrets/bmb-auto-unlock-key
    secrets:
      - source: bmb_auto_unlock_key
        target: bmb-auto-unlock-key
secrets:
  bmb_auto_unlock_key:
    file: ./bmb-auto-unlock.key
```

This is the same example, commented out, as in `docker-compose.yml`, `docker-compose.image.yml` and `.env.example`. The key
file must not be readable by other users (the shipped image runs as root, so `0400` is enough). Put the file in place **first**, then
switch the option on: the node takes the secret for the new slot from that file and writes nothing there. When the file is the
operator's, turning the option off never deletes it: remove it yourself. A key file the node created itself is wiped and deleted when the option is
turned off.

**Turning it off:** *Admin → OS Auto-Unlock* or `bmb vault auto-unlock disable`, with the master password. The enable record goes first,
then the slot, then a key file the node created.

**What a restore does to it.** A restore never turns the option on. The slot travels with the data, the enable record never does,
so after a restore the slot has no matching record and is *stale*: the server starts **locked**, nothing is unlocked, the key file
is not read, and the first password unlock removes the slot (an audit line `server_auto_unlock_retired_stale` is written and the
Web shows a notice: *Start unlocked after a restart was switched off because this data came from a backup or a copy; turn it on
again if you want it*). This holds **even when the key file is still mounted**.

| Restore | Result |
|---|---|
| Into an emptied or new data folder; on another machine; a copied data folder | Starts locked. Switch the option on again on the restored node if you want it. |
| A restore **in place** over the very server that enabled it, whose record and key file are still where they were, and whose restored slot is byte-identical to the one the record names | Stays as the owner left it. This is the owner's earlier choice staying in force; the restore created no slot, record or key file. |
| In place, but the option was switched off and on again (or the key rotated) since the backup, so the slot differs | Starts locked. |
| A snapshot restore, a restore from a blind copy or an Android backup, a join, a baseline | The slot is removed on the way in: the option is off afterwards, whatever the key file says. |
| A backup taken **without** the two `--exclude` lines | A plain copy. It carries the key file and the record; restoring it is the same as the stolen disk the warning describes. |

A DEK rotation also switches the option off (the slot is deleted with the other slots; the record and a node-created key file are
removed at once), and so does a node reset. A key file that appears by itself, or a `BMB_AUTO_UNLOCK_KEY_FILE` pointing at a file,
does nothing without the slot and the matching record; no environment variable, setting, flag or installer option turns the
option on.

An *Auto-unlock* slot the owner created earlier on a server keeps working; the card shows it with a *Turn off* button.

## A Locked Node Still Receives Changes

An encrypted node that is locked (after a restart, or after *Lock*) cannot open its database, but it is still a member of the
network. From the boot store alone it:

- answers its peers' identity, challenge and sign-in requests for the nodes that were in its peer list when it closed;
- **accepts sealed changes and attachments** from them into a holding area in the boot store, and serves them on to other peers
  with sequence numbers above the last one it closed with, so a peer's position on this node stays meaningful;
- takes a position report from a peer and keeps the highest;
- **refuses changes that still carry readable content** (nodes that have not applied metadata encryption send them): they are
  answered *deferred*, nothing is stored, and the peer sends them again after the unlock. No readable article text, title or
  tag is ever written to the boot store;
- suspends a node that a superadmin revokes or changes while it was locked: from that moment that node can no longer sign in,
  push or pull here;
- does not apply a node added while it was locked until it is unlocked (deferred).

Everything else, except the sign-in, unlock and recovery pages and the health and version checks (the web UI, articles, search,
the join and snapshot routes, readiness and status of sync), answers *503 vault_sealed* with `Retry-After: 30` until the node is
unlocked. A peer asking for events older than the point where this node
closed gets *503 vault_sealed_range* and gets those events from another peer or after the unlock.

At the unlock the holding area is emptied into the vault: every change is applied **once and in the order it arrived**, through
the same rules an unlocked node applies (a node revoked before the close is refused, a revoke received while locked takes effect at
its place), attachments first, position reports merged, and numbering continues above every sequence number the locked node
handed out. The login page says *Locked - N changes received, they will be applied after unlock*. A change that cannot be applied yet
stays in the holding area, is retried every minute, and holds back the ones after it so the order is kept.

The holding area is bounded: at 2 GiB of changes and attachments (`BMB_SEALED_INBOX_MAX_MB`), or when less than 1 GiB is free on
the data drive, the node answers *507 Insufficient Storage* and the peer tries again later.

While the node holds itself still for a file copy (the update's pre-update copy, a few seconds), pushes from peers are answered
*503 vault_sealed_busy* with `Retry-After: 5`; nothing is stored, the peer's position does not move, and the same push is stored
once when the copy is done.

## Deployment Procedure (Docker)

```bash
# 1. Update working copy
cd /path/to/BeeMemoryBank
git pull

# 2. Rebuild and restart (with the ready-made image instead: docker compose pull && docker compose up -d)
sudo docker compose -f deploy/<config>/docker-compose.yml up -d --build

# Verify
sudo docker compose -f deploy/<config>/docker-compose.yml ps
curl -f http://localhost:5300/health
```

## Deployment Procedure (From Source)

For systems without Docker, you can run API and Web as separate processes:

```bash
# 1. Build
dotnet publish server/BeeMemoryBank.Api/ -c Release -o publish/api
dotnet publish server/BeeMemoryBank.Web/ -c Release -o publish/web

# 2. Start API
BMB_DATA_PATH=/var/lib/beememorybank \
ASPNETCORE_URLS=http://localhost:5300 \
./publish/api/BeeMemoryBank.Api &

# 3. Start Web
BMB_API_URL=http://localhost:5300 \
ASPNETCORE_URLS=http://localhost:5301 \
./publish/web/BeeMemoryBank.Web
```

When running as separate processes, you **must** set `BMB_INTERNAL_KEY` to the same value for both API and Web:

```bash
# Generate once and reuse for both:
export BMB_INTERNAL_KEY=$(openssl rand -hex 32)

# Pass to API:
BMB_DATA_PATH=/var/lib/beememorybank \
ASPNETCORE_URLS=http://localhost:5300 \
BMB_INTERNAL_KEY=$BMB_INTERNAL_KEY \
./publish/api/BeeMemoryBank.Api &

# Pass to Web:
BMB_API_URL=http://localhost:5300 \
ASPNETCORE_URLS=http://localhost:5301 \
BMB_INTERNAL_KEY=$BMB_INTERNAL_KEY \
./publish/web/BeeMemoryBank.Web
```

The API will refuse to start in Production if `BMB_INTERNAL_KEY` is not set.

## Maintenance Page (Apache)

The file `server/BeeMemoryBank.Web/wwwroot/maintenance.html` is included in the project.
After `dotnet publish` it lands at `publish/web/wwwroot/maintenance.html`.

To have Apache serve it automatically when the backend is unavailable (502/503), add to your VirtualHost **before** `ProxyPass /`:

```apache
# Maintenance page — served directly by Apache when backend is down
Alias /maintenance.html /opt/beememorybank/publish/web/wwwroot/maintenance.html
<Directory /opt/beememorybank/publish/web/wwwroot>
    Require all granted
</Directory>
ProxyPass /maintenance.html !

ProxyErrorOverride On
ErrorDocument 502 /maintenance.html
ErrorDocument 503 /maintenance.html
```

The page polls `GET /` every 3 seconds and automatically redirects to `/` once the service is back up.

**Important:** The `Alias` block must appear **before** `ProxyPass /` and **before** `ProxyErrorOverride On`.

---

## Setting Up a New Node (Docker)

```bash
# 1. Install Docker

# 2. Clone the repository (or, without a clone: the ready-made image, docker-compose.image.yml)
git clone <repo-url> /path/to/BeeMemoryBank
cd /path/to/BeeMemoryBank

# 3. Create data directory
sudo mkdir -p /var/lib/beememorybank
sudo chown $USER /var/lib/beememorybank

# 4. Build and start (customize ports in .env if needed)
docker compose up -d --build

# 5. Join the network via bmb CLI (the CLI is /app/cli/bmb.dll in both images)
docker compose exec bmb dotnet /app/cli/bmb.dll join \
  --remote https://your-server.example.com \
  --password "..." --name "NewNode" \
  --data /app/data
```
## Stable Data Root and Multiple Storages (Windows Desktop)

In Windows Desktop installations (via Velopack), user data is isolated from the application binary files to prevent data loss during updates and repairs.

### Stable Data Root Directory
* **Data Path:** `%LOCALAPPDATA%\BeeMemoryBankData`
* **Why it is separate:** Previously, user data was stored within the versioned application directory (`current\data`). Velopack deletes and recreates the `current` folder during application updates and repairs, which leads to total loss of user data. Moving the data path to a separate root outside the application folder completely resolves this issue.
* **Uninstallation Behavior:** When the user uninstalls the Desktop application (either by running `Update.exe uninstall` or via the Windows "Apps & Features" Settings panel), Velopack only cleans up its own installation folder (`%LOCALAPPDATA%\BeeMemoryBank`). The stable data root (`%LOCALAPPDATA%\BeeMemoryBankData`) **is NOT deleted or modified during uninstallation**, meaning user profiles, credentials, and databases survive uninstallation.

### Directory Structure
Inside the stable data root, files are organized as follows:
* `profiles.json` — The profile registry which tracks all defined storages, their names, unique identifiers, and the last used profile.
* `desktop-settings.json` — Settings for the Desktop shell.
* `logs\` — Log files for both the Desktop shell and the backend node process (`bmbd`).
* `migration\` — Logs and markers for startup data rescue migrations.
* `vaults\<vaultId>\` — Individual directories for each created storage (vault). Each vault folder contains the standard data directory layout (such as `beememorybank.db`, `media\`, etc.).

### Transition and Test Scripts
* **Legacy Data Migration:** If you have an older installation with data locked inside the versioned folder, the Desktop application automatically attempts to rescue the data on startup. Alternatively, you can use the manual PowerShell script [rescue-velopack-data.ps1](../scripts/rescue-velopack-data.ps1) to copy data from a legacy path to the new stable directory before updating.
* **Update Verification:** The E2E update verification process is implemented in [smoke-update.ps1](../scripts/smoke-update.ps1). This script tests the full Velopack update cycle using a throwaway application package to guarantee that user data is preserved after update and repair procedures.

## Database Schema

The database is built by one migration, the baseline (`040_baseline.sql`, the whole schema of the 2.6 series); later versions add
migrations numbered `041` and up. A database written by 2.6.0 or 2.6.1 is adopted at its first start with this version (the structure is
checked, nothing is re-run, the migration history is replaced by the baseline's single row). **A database older than 2.6 must be updated to
2.6.1 first, then to this version**: this version refuses it, and leaves it unchanged, with a message that says so. The same goes for a
backup: one made by a release before 2.6 is refused by the restore, before anything is replaced. Key tables include:

| Table | Purpose |
|---|---|
| `tbl_article` | Article metadata (title, treePath — plaintext) |
| `tbl_article_body` | Encrypted article content (ciphertext_hash → `tbl_blob`, encrypted DEK) |
| `tbl_blob` | Content-addressed ciphertext store, keyed by the SHA-256 of its own bytes; shared by article bodies/versions and media |
| `tbl_role` / `tbl_role_folder_acl_entry` | Custom roles and their folder ACL rules (node-local, same shape as the per-user ACL table) |
| `tbl_sync_quarantine` | Persistent record of sync events that failed to apply, split into permanent vs. deferred failure counters |
| `tbl_article_version` | Encrypted article version history |
| `tbl_comment` | Comments with soft-delete and Lamport LWW |
| `tbl_event` | Sync event log (signed with Ed25519, actor tracking) |
| `tbl_key_slot` | Encrypted Master DEK (per-user / per-recovery slot) |
| `tbl_node_identity` | Node public key, **encrypted Ed25519 private key (v=1, master-DEK wrapped)**, sentinel value |
| `tbl_user` | User accounts with per-user key slots |
| `tbl_agent` | API agent keys with encrypted DEK, owner_user_id, **kdf_version (0 legacy SHA256 / 1 HKDF-SHA256), salt** |
| `tbl_folder` | First-class folders with Lamport timestamps |
| `tbl_folder_acl_entry` | Per-folder access control entries (allow/deny per user+folder) |
| `tbl_media` | Uploaded images (encrypted, same envelope pattern) |
| `tbl_audit_log` | Operation audit trail (covers DEK rotation + snapshot create/restore/upload/delete + user CRUD + admin password reset + agent create/delete; pruned by `AuditLogPruningHostedService` after 90 days) |
| `tbl_hard_delete_audit` | Per-entity hard-delete record with `lamport_ts`. Survives event-log compaction; gates against late `article_update`/`folder_*` events from peers that didn't see the hard-delete |
| `tbl_concept_tag` | Concept tag vocabulary |
| `tbl_article_concept_tag` | Article-to-tag associations |
| `tbl_sync_position` | Last received event sequence per node |
| `tbl_sync_push_position` | Last sent event sequence per node |
| `tbl_conflict_version` | Temporary storage for losing versions in conflicts |
| `tbl_tombstone` | Soft-deletion tracking for sync |
| `tbl_projection_matrix` | Embedding projection data |
| `tbl_key_slot` | Shared key slots for multi-user access |
| `tbl_dek_rotation_state` | DEK rotation state machine (Proposed/Committing/Applied/Cancelled/Failed/Rejected) |

### The boot store (`beememorybank.boot.db`, encrypted nodes only)

A second, plain SQLite file with its own migration ledger (`tbl_boot_migration`, scripts `B001_…` in
`libs/BeeMemoryBank.Storage/BootMigrations/`, listed in `docs/boot-migrations.golden.txt`) that the vault's migration runner never
sees. `PRAGMA application_id = 0x424D4253`. Its tables, all of them allow-listed column by column in
`docs/design/boot-store.golden.txt`:

| Table | Purpose |
|---|---|
| `tbl_boot_meta` | `node_id` (written once), `dbk_generation`, `seq_floor`, `sealed_head`, `clean_close`, `unlock_material_version`, … |
| `tbl_boot_door` | Copies of the ways in: key slots and auto-unlock agents (wrapped master key, iv, salt, Argon2 parameters, key hash), never a user or agent name |
| `tbl_boot_db_key` | The database key wrapped under a master key (one row per master-key fingerprint and generation) |
| `tbl_boot_identity` | The node's id, name and public key |
| `tbl_whitelist` | Mirror of the vault's peer list, same columns; status `S` = suspended while locked |
| `tbl_relay_event`, `tbl_relay_blob`, `tbl_relay_report` | The holding area of a locked node (events, attachments, position reports) |

The schema of an encrypted node (migration 40 of the 2.6 series, now part of the baseline) adds only triggers to the vault: any change to a key slot, a user's slot reference, an
auto-unlock agent or the node identity bumps `unlock_material_version` in the same transaction, and a watcher that checks every
10 seconds rewrites the boot store when it is behind.
