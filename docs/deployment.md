# Deployment Guide

## Environment Variables

| Variable | Component | Description |
|---|---|---|
| `BMB_DATA_PATH` | API, CLI | Path to the data directory (database, snapshots) |
| `ASPNETCORE_URLS` | API, Web | Bind URL (e.g., `http://localhost:5300`) |
| `ASPNETCORE_ENVIRONMENT` | API, Web | Production / Development |
| `BMB_API_URL` | Web | Internal API URL (e.g., `http://localhost:5300`) |
| `BMB_INTERNAL_KEY` | API, Web | Shared secret for Web→API authentication. Every request from Web to API must carry this key in the `X-Internal-Key` header. **In Docker:** `docker-entrypoint.sh` auto-generates and exports the key before starting both processes — you do not need to set it manually. **From source (separate processes):** you must set it explicitly and pass the same value to both API and Web (see below). The API refuses to start in Production if the key is missing. |
| `BMB_AUDIT_RETENTION_DAYS` | API | Optional. How long to keep `tbl_audit_log` rows. Default `90`. Set to `0` to disable pruning entirely. The pruning service runs ~24 h after process start and once a day after; it skips when the session is locked (no operator present to react to anomalies) and writes a meta-audit row recording the deletion so the prune itself shows up in the audit trail. |
| `BMB_COMPACTION_KEEP_COUNT` | API | Optional. How many of the most recent events survive an event-log compaction. Default `1500`, minimum `100` (a lower value is ignored and the default used instead). This number is also how far a peer may fall behind before compacting would strand it, so it trades two things off against each other: raise it and `tbl_event` stays large but a phone that was off for a week can still resume; lower it and the log shrinks but a peer that misses a busy day has to wipe and rejoin. With ~20 active writers 1500 events is roughly a day or two. Compaction refuses outright while any peer would be stranded and names them; `acceptCuttingOffPeers` on `POST /api/admin/compact` is the deliberate override, and it logs which peers it stranded. |
| `BMB_TRUSTED_PROXIES` | API, Web | **Set this whenever a reverse proxy (including Docker port publishing) sits in front of the node.** Comma- or whitespace-separated IP addresses and/or CIDR networks whose `X-Forwarded-For` header is believed — e.g. `172.16.0.0/12` for the Docker bridge range. Without it every per-IP rate limit keys on the proxy's own address, so all clients share one bucket: a single anonymous caller can exhaust the sync-challenge budget and stall synchronization for the whole mesh, or the login budget for every user at once. Only one hop is trusted (`ForwardLimit = 1`), and an unparsable entry is logged and ignored rather than failing startup. Trust here is transitive — anything that can reach the port from a listed address can claim any client IP — so name the proxy or its network and nothing wider. Both processes announce what they ended up trusting on startup — `[forwarded-headers] Trusting X-Forwarded-For from: …`, or a line saying nothing is configured — so check the log after changing this rather than assuming it took. |
| `BMB_TRUST_LOOPBACK_FORWARDED_HEADERS` | API, Web | Optional, `true`/unset. Believe `X-Forwarded-For` from loopback. For a proxy sharing the host with the node (the desktop app sets this itself). Independent of `BMB_TRUSTED_PROXIES`; either or both may be set. Not sufficient under Docker — published-port traffic arrives on the bridge interface, never on loopback. |

## Example Node Setup (Docker)

```
Container: bmb  (docker compose)
Web:  localhost:5301  → container :5301   (published)
API:  container :5300                     (NOT published — see below)
Data: /var/lib/beememorybank  (bind mount to /app/data)
Image: multi-stage build from Dockerfile
```

The shipped `docker-compose.yml` deliberately publishes only the Web port — the right choice for a
purely local node. For a node that must be reachable from other nodes or MCP agents, use
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

## Reverse Proxy — What Is Exposed

Only the following endpoints should be publicly accessible:
- `/mcp` — MCP server (authentication via Bearer token at the application level)
- `/api/sync` — synchronization between nodes (Ed25519)
- `/api/join` — join protocol

Everything else (including `/api/articles`) should be restricted to trusted IPs or localhost.

**How the application enforces this.** The node keeps its own list of what a caller without
`BMB_INTERNAL_KEY` may reach — `PublicSurface` in the source, covering `/mcp`, `/api/sync/*`,
`POST /api/join`, the snapshot-file and restore-progress routes, `/health` and `GET /api/version`.
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

**Keep the proxy path-filter anyway.** It is now the outer of two layers rather than the only one, and
it is the layer that stops the request before it reaches the application at all. Restrict the API port
to loopback and forward only `/mcp`, `/api/sync`, `/api/join` and `/api/snapshots/restore` over TLS.

A node that receives a network-wide snapshot restore needs `GET /api/snapshots/restore/{id}/file`
forwarded (Bearer-authenticated with a sync token, like the rest of `/api/sync`). Without it the
restore initiator can publish the event but no peer can fetch the snapshot.

**Escape hatch.** `BMB_PUBLIC_SURFACE=off` disables the node-side gate if a deployment turns out to
need an endpoint the list does not know about. It is meant for a bad afternoon, not as a setting — the
startup log says which state the node is in. If you need it, please report which endpoint, so it can be
published properly instead.

## Resetting a Node

Wiping a node back to first-run Setup — every article, folder, user, key and sync-state row — is
available two ways. Both require the master password as confirmation; the password is verified
*without* unlocking the vault, so a rejected attempt leaves nothing open.

- **Web UI:** `/Admin` → *Reset This Node*. Superadmin only, and a human superadmin at that: the
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

## Deployment Procedure (Docker)

```bash
# 1. Update working copy
cd /path/to/BeeMemoryBank
git pull

# 2. Rebuild and restart
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

# 2. Clone the repository
git clone <repo-url> /path/to/BeeMemoryBank
cd /path/to/BeeMemoryBank

# 3. Create data directory
sudo mkdir -p /var/lib/beememorybank
sudo chown $USER /var/lib/beememorybank

# 4. Build and start (customize ports in .env if needed)
docker compose up -d --build

# 5. Join the network via bmb CLI
docker compose exec bmb dotnet /app/api/BeeMemoryBank.Cli.dll join \
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

The database starts from a consolidated schema file (`001_initial_schema.sql`) plus incremental
migrations (`002`–`024` as of this writing — roles, a content-addressed blob store, DEK-rotation
resilience, whitelist versioning, persistent sync quarantine, and more). Key tables include:

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
