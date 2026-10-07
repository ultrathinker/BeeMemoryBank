# Opening your node to the internet yourself

Earlier versions had a built-in "Access from the Internet" wizard (dynamic DNS, a Let's Encrypt
certificate, a reachability test). It was removed: opening a machine to the internet is better done
with standard tools you control and can audit, and this page shows how. The node itself never needs
to be changed; everything below happens in front of it.

## Do you need this?

You need it only to reach your node **from outside your home or office network**: a laptop on
hotel Wi-Fi, an AI agent running in the cloud that must call `/mcp`, or a second node (a VPS, a
friend's machine) that must sync with this one.

You do **not** need it for your own devices on the same network. A desktop node (the Windows app, the
Mac app, the Windows service) answers this computer only by default: its front door, port 5310, is on
`127.0.0.1`. The app has two ways to let devices on your own network in, both under
**Admin → Nodes**, and both work in the Windows app, the Mac app and the Windows service:

- **Devices on my network** is a setting, **off by default** and remembered per profile. Switched on, the
  node also serves its whole web page and API over HTTPS on port 5311 (a certificate from the node's own
  local CA) and announces itself on the local network by mDNS (only while the listener really listens: if
  port 5311 is taken, nothing is announced and the card says so), so another computer's first-run page
  (**Find devices on my network**) can see it and a phone can open it in a browser. It takes effect at
  once, without a restart, and the vault stays unlocked. What it exposes: everything the node serves, so
  anyone on the same network can reach the sign-in page and try passwords (the node limits attempts). A
  phone or another computer must trust the node's CA once to open the page without a warning (the
  **Connect a device** page offers `ca.crt` and the steps). Use it on a home or office network you trust,
  not on a public one. Setting `BMB_HTTPS_ENABLED=1` for the node still switches the same listener on by
  itself, for existing setups; the setting then has nothing to change, and the card says so.
- **Connect a device** is a temporary door instead of a permanent setting: port 5311 opens for 15
  minutes with a one-time token, serves only the few calls a join needs (not the web page, not MCP) and
  closes by itself once the device has joined. It shows a **join code**: the address, the token and the
  pin of the node's certificate key. A phone pastes it under **Join**; another computer pastes it on its
  first-run page under **Connect to another device**, or runs `bmb join --code <code>`. The joiner sends
  the master password only to a server holding the pinned key, so no certificate has to be trusted first.
  The door is not announced by mDNS: a node is found on the network only while Devices on my network is
  on, and a node that only opens this door is joined by its code. To keep two computers in step after
  such a join, the one that was joined has to stay reachable: switch Devices on my network on there (or
  use a reverse proxy, below).

The firewall: the Windows app asks once, with the usual Windows permission prompt, to add one inbound
rule for port 5311 limited to the local network (and can remove it again from the same card); it never
changes the firewall without asking. The Windows service has no desktop to ask on: its installer's
"Configure Windows Firewall Exception" option opens the ports. On a Mac the app changes nothing:
macOS asks whether "Bee Memory Bank" may accept incoming connections when the first device connects;
choose Allow. In Docker there is no such switch: the ports the compose file publishes decide
(`docker-compose.yml`, or `docker-compose.image.yml` for the ready-made image; both publish only the Web port, on the
host's loopback), see [deployment.md](deployment.md). Keep the node closed if you can; a node nobody can reach is a node
nobody can attack.

## Which ports, and which to expose

| Port | What it is | Expose it? |
|---|---|---|
| **5301** | Web UI (Docker, systemd/launchd/NSSM installs) | Only to your reverse proxy, on the host's loopback. Never directly. |
| **5300** | Raw API (same installs) | Never to the internet. Your reverse proxy may forward a few paths to it (below), over loopback. |
| **5310** | Desktop app / Windows service front door, `127.0.0.1` only: serves the UI and the API paths together | Only to a proxy on the same machine, and block `/node` in the proxy (see the section below). |
| **5311** | The node's own HTTPS listener (certificate from the node's local CA), meant for the LAN: opened by the "Devices on my network" setting (off by default) or, for 15 minutes, by Connect a device; see above | Never to the internet. Browsers on the internet cannot trust a local CA, and it is not a public-facing listener. |
| **443** (and **80** if your proxy needs it for certificates) | Your reverse proxy | Yes: this is the only thing the router should forward. |

Published Docker ports skip `ufw` (Docker writes its own firewall rules), so a `0.0.0.0` mapping is
reachable from the internet even on a host you believe is firewalled. Use
[`docker-compose.reverse-proxy.yml`](../docker-compose.reverse-proxy.yml): it binds 5300 and 5301 to the
host's loopback only (with the ready-made image, replace its `build:` block by
`image: ghcr.io/ultrathinker/beememorybank:${BMB_VERSION:-latest}`). The image does not even declare 5300 as an exposed
port, so `docker run -P` and NAS port dialogs do not offer it. The reasoning is in
[deployment.md](deployment.md#reverse-proxy--what-is-exposed).

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
| `/api/blind/replica` | API (5300) | a blind copy downloading its first package (sync-token authenticated) — only if blind copies call this node, see [below](#let-blind-copies-call-this-node) |
| everything else | Web (5301) | browsers |

**Optional, off unless you add it:** three more routes for [guest accounts for other people](#4-optional-guest-accounts-for-other-people)
(`/api/auth/remote-token`, `/api/folders/accessible`, `/api/folders/by-path/snapshot`). No recipe below forwards them
by default; each one has a clearly marked optional block.

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
    @api path /mcp /mcp/* /api/sync/* /api/join /api/snapshots/restore/*/file /api/blind/replica
    handle @api {
        reverse_proxy 127.0.0.1:5300
    }
    handle {
        reverse_proxy 127.0.0.1:5301
    }
}
```

Caddy requests and renews the certificate on its own and sends `X-Forwarded-For` by default.

**Optional: guest accounts for other people.** Add this inside the site block, before the last `handle`
(what it means and when you want it: [section 4](#4-optional-guest-accounts-for-other-people)):

```
    @guests path /api/auth/remote-token /api/folders/accessible /api/folders/by-path/snapshot
    handle @guests {
        reverse_proxy 127.0.0.1:5300
    }
```

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

    location ~ ^/(mcp($|/)|api/sync/|api/join$|api/snapshots/restore/[^/]+/file$|api/blind/replica$) {
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
way: see the `ProxyPassMatch` block in [deployment.md](deployment.md#reverse-proxy--what-is-exposed) and the
maintenance-page example there.

### Let blind copies call this node

A blind copy (the Windows, macOS or Android blind app) never listens and needs no address of its own: it
calls a node. Your server behind the proxy above can be that node — a *hub*. Two things make it one:

1. **The proxy forwards what a blind copy uses.** `/api/sync/*` is already in the blocks above (other nodes use
   it too); `/api/blind/replica` — the signed package of a copy's first load — is the one the blocks add. Copy
   the `/api/blind/replica` entry into your Caddy matcher, nginx `location` regex, Apache `ProxyPassMatch` or
   Cloudflare Tunnel `path`, as shown in this guide. Nothing else is needed for the copy's later syncs.
2. **The network knows the node is callable.** In Admin on any other node of the network that lists your server
   (your PC, for instance), *Trusted Nodes → "Let blind copies call this node"* on the server's row: its
   address (`https://bee.example.com`) and how a copy should trust the certificate:
   - **Normal certificate** — for the Let's Encrypt (or any public) certificate your proxy serves. A blind copy
     accepts only a certificate the system's chain vouches for, issued for exactly this name; it never follows a
     redirect or falls back to `http`. Nothing to renew by hand. The one weakness: a certificate authority that
     issues a certificate for your name by mistake (or by order) lets someone in the middle pose as your server to
     a blind copy — though not read its data, see below.
   - **Pinned certificate** — for a node with a certificate of its own. The key it presents is recorded and every
     device accepts only that key. It is read from one connection, so compare it with the key the node shows for
     itself if you can (paste it in the dialog). A public certificate that is renewed with a new key breaks the
     pin — choose *Normal certificate* for those.

   The node checks the address and the certificate before it saves: the address answers as that very node (its
   id and key), and a *Normal* certificate must validate (expired, self-signed or wrongly named ones are refused
   with the reason). Then *Blind nodes → Add an Android blind copy* (also used for the Windows and macOS copies)
   lists the server as a hub, with its certificate mode, and the call code it gives the copy carries that mode.

**What this exposes.** The two routes need a sync token, which a device gets only by signing in with its own key
— one you paired — and the device's whitelist entry is checked on every request. What they serve is ciphertext
plus the metadata every node holds in plaintext (titles, folder paths, tags, file names; see
[ADR-0005](adr/0005-plaintext-metadata.md)); a blind copy never gets the master key. The replica route also
limits each device to 20 requests every ten minutes. If a copy is lost, revoke it in Admin (*Trusted Nodes →
Revoke*); its token stops working at once. Turning the hub off again (the same dialog) stops new pairings.

**Optional: guest accounts for other people.** Add this next to the first `location` above (what it means
and when you want it: [section 4](#4-optional-guest-accounts-for-other-people)):

```nginx
    location ~ ^/api/(auth/remote-token|folders/accessible|folders/by-path/snapshot)$ {
        proxy_pass http://127.0.0.1:5300;
        proxy_http_version 1.1;
    }
```

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
      path: '^/(mcp($|/)|api/sync/|api/join$|api/snapshots/restore/[^/]+/file$|api/blind/replica$)'
      service: http://127.0.0.1:5300
    - hostname: bee.example.com
      service: http://127.0.0.1:5301
    - service: http_status:404
  ```
  **Optional: guest accounts for other people** ([section 4](#4-optional-guest-accounts-for-other-people)):
  add one more entry above the `service: http://127.0.0.1:5301` one.
  ```yaml
    - hostname: bee.example.com
      path: '^/api/(auth/remote-token|folders/accessible|folders/by-path/snapshot)$'
      service: http://127.0.0.1:5300
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

## 4. Optional: guest accounts for other people

A **remote account** (the Remote Accounts page) lets one node mirror folders of another node, read-only, signing in
there with an ordinary user name and password. The node that holds the folders is the **owner**; the node that
mirrors them is the **guest's**. This is optional and **off unless you add the block to your proxy**: if you only
use your own devices, skip it.

**What the owner must do** (the node that is to be mirrored):

1. Forward exactly these three routes to the API port (5300), with the optional block of your recipe above:
   `POST /api/auth/remote-token` (user name and password for a token that lasts 90 days),
   `GET /api/folders/accessible` (the folders the guest may read) and
   `GET /api/folders/by-path/snapshot` (the folder and its articles, polled every minute). Nothing else is needed.
2. Be reachable under an `https://` **name**. The guest's node refuses `http://` (except `localhost`) and IP
   addresses on private networks (10.x, 172.16-31.x, 192.168.x, 169.254.x and the like), so that it cannot be used to
   probe the guest's own network.
3. Create the guest a user account and give it folder access rules: a guest who is not a superadmin sees only the
   folders its account may read.
4. Keep the vault unlocked: a locked node answers "Owner session is locked" and nothing is mirrored.

**What it means for security.** The first route takes a user name and password from anyone who can reach it, so you
are putting a password check on the internet; the node rate-limits it (5 attempts per 5 minutes per client address,
which only works per person if the node believes your proxy's `X-Forwarded-For`, see above) and answers the same to a
wrong user and a wrong password. The two other routes need the token that route issued. Read-only: nothing is
written through the token. Do not add the block unless you want this.

**What the guest sees when it fails.** On the guest's node the Remote Accounts page says, in words:

| Message | What it means |
|---|---|
| "The other node does not let guest sign-ins through. Its reverse proxy must forward /api/auth/remote-token, /api/folders/accessible and /api/folders/by-path/snapshot: see docs/internet-access.md" | The other node answered 404, 403 or 405 where its API would not have: its proxy does not forward the routes (step 1 above). |
| "Use an https:// name for the other node…" | The address starts with `http://` or is a private IP address (step 2). |
| "The other node did not accept that user name and password." | Wrong user name or password, or the account is deactivated. |
| "The other node limits sign-in attempts (5 in 5 minutes)…" | Too many attempts from this address: wait. |
| "The other node is locked: its owner has to unlock it first." | Step 4. |
| "This node could not reach the other node at that address…" | The name does not resolve, nothing answers, or TLS failed. |

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
