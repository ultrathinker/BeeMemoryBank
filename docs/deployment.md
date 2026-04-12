# Deployment Guide

## Environment Variables

| Variable | Component | Description |
|---|---|---|
| `BMB_DATA_PATH` | API, CLI | Path to the data directory (database, snapshots) |
| `ASPNETCORE_URLS` | API, Web | Bind URL (e.g., `http://localhost:5300`) |
| `ASPNETCORE_ENVIRONMENT` | API, Web | Production / Development |
| `BMB_API_URL` | Web | Internal API URL (e.g., `http://localhost:5300`) |
| `BMB_DEPLOY_SCRIPT` | API | Path to a bash script for deployment (enables `/api/deploy` endpoints) |
| `BMB_INTERNAL_KEY` | API, Web | Shared secret for Web→API admin calls. **Required in production.** If unset, admin endpoints only accept requests from localhost. Set the same value on both API and Web services. |

## Example Node Setup

```
API:  localhost:5300  (beememorybank-api.service)
Web:  localhost:5301  (beememorybank-web.service)
Data: /var/lib/beememorybank
Bins: /opt/beememorybank-v2/publish/{api,web}
User: beememorybank (system, nologin)
```

## Reverse Proxy — What Is Exposed

Only the following endpoints should be publicly accessible:
- `/mcp` — MCP server (authentication via Bearer token at the application level)
- `/api/sync` — synchronization between nodes (Ed25519)
- `/api/join` — join protocol

Everything else (including `/api/articles`) should be restricted to trusted IPs and localhost.

## Systemd Service

```ini
[Unit]
Description=BeeMemoryBank v2 API
After=network.target

[Service]
Type=exec
User=beememorybank
Group=beememorybank
WorkingDirectory=/opt/beememorybank-v2/publish/api

Environment=BMB_DATA_PATH=/var/lib/beememorybank
Environment=ASPNETCORE_URLS=http://localhost:5300
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=BMB_INTERNAL_KEY=<generate-with: openssl rand -hex 32>

ExecStart=/opt/beememorybank-v2/publish/api/BeeMemoryBank.Api
Restart=on-failure
RestartSec=5

NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ReadWritePaths=/var/lib/beememorybank

[Install]
WantedBy=multi-user.target
```

## Deployment Procedure

Deployment is **manual** — there is no post-receive hook. After pushing to the git repository, run these steps:

```bash
# 1. Update working copy
cd /path/to/BeeMemoryBank-v2
git pull

# 2. Build
dotnet publish server/BeeMemoryBank.Api/ -c Release -o /tmp/bmb-publish/api
dotnet publish server/BeeMemoryBank.Web/ -c Release -o /tmp/bmb-publish/web

# 3. Stop services (IMPORTANT: stop BEFORE copying — the binary is in use)
sudo systemctl stop beememorybank-api beememorybank-web

# 4. Clean old native libraries (api and web)
sudo rm -f /opt/beememorybank-v2/publish/api/createdump
sudo rm -f /opt/beememorybank-v2/publish/api/lib*.so
sudo rm -f /opt/beememorybank-v2/publish/web/createdump
sudo rm -f /opt/beememorybank-v2/publish/web/lib*.so

# 5. Deploy binaries
sudo cp -r /tmp/bmb-publish/api/* /opt/beememorybank-v2/publish/api/
sudo cp -r /tmp/bmb-publish/web/* /opt/beememorybank-v2/publish/web/

# 6. Start
sudo systemctl start beememorybank-api beememorybank-web

# Verify
sudo systemctl status beememorybank-api beememorybank-web
```

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

## Self-contained vs Framework-dependent

The default build is **framework-dependent** (without a bundled .NET runtime). The system .NET runtime is used.

**Potential issue:** If native libraries from a previous **self-contained** deployment (`libclrjit.so`, `libcoreclr.so`, `createdump`, etc.) remain in the publish directory, the apphost thinks the runtime is bundled — but it is not there → the service crashes with `status=150`.

**Solution:** Clean old artifacts from both directories before deploying (step 4 above).

## Setting Up a New Node (Full Cycle)

```bash
# 1. Create user and directories
sudo useradd --system --no-create-home --shell /usr/sbin/nologin beememorybank
sudo mkdir -p /var/lib/beememorybank /opt/beememorybank-v2/publish/{api,web}
sudo chown beememorybank:beememorybank /var/lib/beememorybank

# 2. Build and copy binaries (see above)

# 3. Create systemd unit files (api + web)
# 4. Create nginx/apache config

# 5. Join the network
sudo -u beememorybank bmb join \
  --remote https://your-server.example.com \
  --password "..." --name "NewNode" \
  --data /var/lib/beememorybank

# 6. Start
sudo systemctl enable --now beememorybank-api beememorybank-web
# Sync will start automatically within 60 seconds
```

## Database Schema

The database currently contains **19 migrations**. Key tables include:

| Table | Purpose |
|---|---|
| `tbl_article` | Article metadata (title, tags, treePath — plaintext) |
| `tbl_article_body` | Encrypted article content (ciphertext, encrypted DEK) |
| `tbl_article_version` | Encrypted article version history (migration 017) |
| `tbl_folder_restriction` | Per-folder access control entries (migration 016) |
| `tbl_event` | Sync event log (signed with Ed25519) |
| `tbl_master_key_store` | Encrypted Master DEK (password slot) |
| `tbl_node_identity` | Node public key, sentinel value |
| `tbl_user` | User accounts with per-user key slots |
| `tbl_agent` | API agent keys with encrypted DEK |
| `tbl_media` | Uploaded images (encrypted, same envelope pattern) |
