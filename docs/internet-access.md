# Opening your node to the internet yourself

Earlier versions had a built-in "Access from the Internet" wizard (dynamic DNS, a Let's Encrypt
certificate, a reachability test). It was removed: opening a machine to the internet is better done
with standard tools you control and can audit, and this page shows how. The node itself never needs
to be changed; everything below happens in front of it.

## Do you need this?

You need it only to reach your node **from outside your home or office network**: a laptop on
hotel Wi-Fi, an AI agent running in the cloud that must call `/mcp`, or a second node (a VPS, a
friend's machine) that must sync with this one.

You do **not** need it for your own devices on the same network. The desktop app's local CA already
covers the home network, phones and blind-node pairing: use the **Connect a device** page and the
node's HTTPS listener on the LAN. Keep it closed if you can; a node nobody can reach is a node
nobody can attack.

## Which ports, and which to expose

| Port | What it is | Expose it? |
|---|---|---|
| **5301** | Web UI (Docker, systemd/launchd/NSSM installs) | Only to your reverse proxy, on the host's loopback. Never directly. |
| **5300** | Raw API (same installs) | Never to the internet. Your reverse proxy may forward a few paths to it (below), over loopback. |
| **5310** | Desktop app / Windows service front door, `127.0.0.1` only: serves the UI and the API paths together | Only to a proxy on the same machine, and block `/node` in the proxy (see the section below). |
| **5311** | The node's own HTTPS listener (certificate from the node's local CA), meant for the LAN | Never to the internet. Browsers on the internet cannot trust a local CA, and it is not a public-facing listener. |
| **443** (and **80** if your proxy needs it for certificates) | Your reverse proxy | Yes: this is the only thing the router should forward. |

Published Docker ports skip `ufw` (Docker writes its own firewall rules), so a `0.0.0.0` mapping is
reachable from the internet even on a host you believe is firewalled. Use
[`docker-compose.reverse-proxy.yml`](../docker-compose.reverse-proxy.yml): it binds 5300 and 5301 to the
host's loopback only. The reasoning is in [deployment.md](deployment.md#reverse-proxy--what-is-exposed).

## 1. Router and DNS

1. Give the machine that runs the proxy a fixed LAN address (a DHCP reservation in the router).
2. In the router's port-forwarding settings, forward external TCP **443** (and **80**, if you use
   Let's Encrypt's HTTP challenge) to that LAN address, same port numbers. Nothing else.
3. Create a DNS `A` record (for example `bee.example.com`) pointing at your public IP. If your
   provider changes your IP now and then, use any dynamic-DNS service you trust: it is separate from
   the node, which no longer ships one.

If the WAN address shown in your router is private, or starts with `100.64.` to `100.127.`, your
provider puts you behind CGNAT and no port forwarding will work. Use a tunnel or a VPN instead (last
section).

## 2. Reverse proxy with automatic certificates

The session cookie is `Secure`, so the browser needs HTTPS on any host except `localhost`. Both proxies
below get and renew the certificate for you.

Split the paths the way the compose file describes: the few routes that peers and agents need go to
the API port, everything else goes to the Web port.

| Path | Goes to | Who calls it |
|---|---|---|
| `/mcp` | API (5300) | AI agents (Bearer `bee_` key) |
| `/api/sync/*` | API (5300) | other nodes (signed sync handshake) |
| `/api/join` | API (5300) | a node joining with the master password |
| `/api/snapshots/restore/<id>/file` | API (5300) | a peer fetching the snapshot of a network-wide restore (sync-token authenticated) |
| everything else | Web (5301) | browsers |

This is the same list as in [`docker-compose.reverse-proxy.yml`](../docker-compose.reverse-proxy.yml)
and [deployment.md](deployment.md#reverse-proxy--what-is-exposed). Other restore routes, such as the
progress poll of the locked splash screen, are called by the Web UI itself and need no forwarding.
The node also refuses what is not on its own public list (keyless callers get `404`), so this is the
outer of two layers. Keep both.

**Only agents and sync, no web UI?** Leave out the Web upstream and answer `404` for everything that
is not in the table. Then the (always public) login page is not exposed at all.

### Caddy

```
bee.example.com {
    @api path /mcp /mcp/* /api/sync/* /api/join /api/snapshots/restore/*/file
    handle @api {
        reverse_proxy 127.0.0.1:5300
    }
    handle {
        reverse_proxy 127.0.0.1:5301
    }
}
```

Caddy requests and renews the certificate on its own and sends `X-Forwarded-For` by default.

### nginx (with certbot)

```nginx
server {
    listen 443 ssl;
    server_name bee.example.com;
    ssl_certificate     /etc/letsencrypt/live/bee.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/bee.example.com/privkey.pem;

    client_max_body_size 500m;                 # the node accepts large uploads
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto https;

    location ~ ^/(mcp($|/)|api/sync/|api/join$|api/snapshots/restore/[^/]+/file$) {
        proxy_pass http://127.0.0.1:5300;
        proxy_http_version 1.1;
        proxy_buffering off;                   # MCP streams responses
        proxy_read_timeout 1h;
    }
    location / {
        proxy_pass http://127.0.0.1:5301;
    }
}
```

Then `sudo certbot --nginx -d bee.example.com` (certbot renews it by itself). Apache works the same
way: see the `ProxyPass` pattern and the maintenance-page example in [deployment.md](deployment.md).

### Tell the node whose `X-Forwarded-For` to believe

Without this, every visitor looks like the proxy and shares one rate-limit bucket (one stranger can
lock everybody out of login or stall sync). Proxy on the same host and node not in Docker:
`BMB_TRUST_LOOPBACK_FORWARDED_HEADERS=true`. Docker: `BMB_TRUSTED_PROXIES=172.16.0.0/12`. Proxy on
another machine: `BMB_TRUSTED_PROXIES=<its IP>`. Details and the startup log line to check are in the
[README](../README.md#https-reverse-proxy) and [deployment.md](deployment.md).

### Desktop app or Windows service: block `/node`

The desktop app and the Windows service have no fixed 5300/5301; they have one front door on
`127.0.0.1:5310` that splits UI and API paths itself. Point the proxy at it, with **one important
addition**. The front's `/node/*` endpoints (status, lock, sync-now, LAN control and the update
passthrough) are protected by "the caller is on this machine"; lock, LAN control and the update
routes also need the node's internal key, status and sync-now do not. A proxy on the same machine makes every internet visitor
look like that, so the proxy must refuse the prefix itself, as the first line of defence. The match
must ignore case, because the node's routes do (`/Node/status` reaches the same endpoint), and
must also catch `/node` itself:

```
# Caddy (its path matcher ignores case)
bee.example.com {
    @node path /node /node/*
    handle @node {
        respond 404
    }
    handle {
        reverse_proxy 127.0.0.1:5310
    }
}
```

```nginx
# nginx, inside the server block (~* = case-insensitive regular expression)
location ~* ^/node(/|$) { return 404; }
location /              { proxy_pass http://127.0.0.1:5310; }
```

The node also refuses on its own: any `/node/*` request that carries a forwarding header
(`X-Forwarded-For`, `X-Forwarded-Host`, `X-Forwarded-Proto`, `Forwarded` or `X-Real-IP`, which proxies
add) is answered `404`, even from the same machine. Keep the proxy rule anyway.

## 3. No open port at all: tunnel or VPN

If you are behind CGNAT, or do not want a hole in the router, nothing needs to be forwarded.

- **Cloudflare Tunnel** (`cloudflared`) makes an outbound connection and publishes a hostname. Same
  path split with an ingress file:
  ```yaml
  ingress:
    - hostname: bee.example.com
      path: '^/(mcp($|/)|api/sync/|api/join$|api/snapshots/restore/[^/]+/file$)'
      service: http://127.0.0.1:5300
    - hostname: bee.example.com
      service: http://127.0.0.1:5301
    - service: http_status:404
  ```
  Cloudflare terminates TLS, so it sees everything your browser or your agents see: the plaintext of
  every article and file the node returns (your node decrypts for anyone who is signed in or presents
  an agent key), search queries, the master password when you unlock, session cookies and agent keys.
  It does not see the data at rest on your machine. Only the sync traffic between nodes carries
  ciphertext bodies; even there titles, folder paths, tags and names are not encrypted (see
  [ADR-0005](adr/0005-plaintext-metadata.md)). Decide if you are comfortable with that. Your own
  proxy on your own machine (Caddy, nginx) or a VPN (below) has no such third party.
- **A VPN such as Tailscale** keeps the node private: only your own devices on the tailnet can reach
  it. `tailscale serve --bg 5301` gives the Web UI an HTTPS name inside the tailnet (do not use
  Funnel, it publishes to the whole internet). Nothing is exposed to strangers, so this is the safest
  choice when only people and devices you control need access.

## Before you open it

- Never publish the raw API port (5300) or the Web port (5301) directly. The API has routes that
  process a master-password attempt and, on success, unlock the vault for every user and agent.
- Use a strong, unique master password. Anyone who can reach the login page can try to guess it.
- Your articles are encrypted on disk and in the sync traffic between nodes, but the node decrypts
  them for whoever signs in or presents an agent key. So anyone who gets in through the public login
  page, and any hosted service that terminates TLS for you, can read them. **The login page and the
  sync endpoints are public** once you open the node. Keep the node and the proxy updated, and watch
  the logs.
- Prefer the narrowest setup that works: a VPN, or the path split with no web UI, over a public UI.
- Take a snapshot (Admin, Snapshots) before changing network setup.
