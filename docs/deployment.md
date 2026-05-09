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

## Example Node Setup (Docker)

```
Container: bmb  (docker compose)
API:  localhost:5300  → container :5300
Web:  localhost:5301  → container :5301
Data: /var/lib/beememorybank  (bind mount to /app/data)
Image: multi-stage build from Dockerfile
```

## Reverse Proxy — What Is Exposed

Only the following endpoints should be publicly accessible:
- `/mcp` — MCP server (authentication via Bearer token at the application level)
- `/api/sync` — synchronization between nodes (Ed25519)
- `/api/join` — join protocol

Everything else (including `/api/articles`) should be restricted to trusted IPs or localhost.

**How the application enforces this:** All non-public endpoints require the `X-Internal-Key` header matching `BMB_INTERNAL_KEY`. Requests without it receive `403 Forbidden` regardless of where they come from — so even if your reverse proxy accidentally exposes an endpoint, the API will block unauthorized access. The IP restriction at the proxy level is defense-in-depth, not the sole protection.

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

## Database Schema

The database is initialized from a single consolidated schema file (`001_initial_schema.sql`). Key tables include:

| Table | Purpose |
|---|---|
| `tbl_article` | Article metadata (title, treePath — plaintext) |
| `tbl_article_body` | Encrypted article content (ciphertext, encrypted DEK) |
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
