# Tailscale / WireGuard networking for BeeMemoryBank

Tailscale (built on WireGuard) is a **zero-config** way to connect two or more BeeMemoryBank
nodes across different networks as if they were on the same LAN. This doc covers the basic setup
and — more importantly — the **cookie/SAN trap** that bites anyone who naively opens a node over
a Tailscale IP in a browser.

## The basic idea

1. Install the Tailscale client on each device (laptop, phone, home server, VPS).
2. Log in with the same identity (or share the tailnet).
3. Every device now gets a **stable private IP** in the Tailscale CGNAT range (`100.x.y.z`),
   and the devices form an **encrypted WireGuard mesh**.

What you get for free:

- **No port forwarding.** Nothing needs to be exposed to the public internet.
- **No DDNS.** Tailscale IPs are stable per-device; they don't rotate like a home DHCP lease.
- **No ACME / public TLS certificate.** Traffic is already encrypted at the WireGuard layer.
- **Works from anywhere.** Two nodes on different Wi-Fi networks, or one at home and one on a
  VPS, can reach each other by Tailscale IP exactly as if they were on the same switch.

For node-to-node sync or an MCP agent this is a complete, zero-fuss setup. For **browser logins**
there is one catch — read the next section before you try.

---

## ⚠️ The cookie/SAN trap (read this before debugging "I can't log in over Tailscale")

BeeMemoryBank's Web session cookie is named `bee_session` and is set with
`CookieSecurePolicy.Always` — i.e. it is a **`Secure` cookie**. This is a deliberate, audited
decision (see `server/BeeMemoryBank.Web/Program.cs`, cookie setup): a `Secure` cookie is only
ever sent over **HTTPS**, never over plain HTTP, so a passive sniffer or a misconfigured proxy
can't lift the session.

**Here is the trap.** Browsers exempt `localhost` / `127.0.0.1` from the `Secure`-cookie
restriction — that's why logging in over `http://localhost:5301` during local development works
fine. A **Tailscale IP is not `localhost`** (`100.x.y.z` ≠ `127.0.0.1`). So over plain HTTP on a
Tailscale IP:

- the browser **refuses to store** the `Secure` `bee_session` cookie the server sets on login, and
- even if it had one, it would **refuse to send it** back on subsequent requests.

The result: you can load the login page at `http://100.x.y.z:5301`, type your credentials, the
server validates them, the redirect comes back… and you're **not logged in**. No error, no obvious
cause — it just silently fails. **Do not weaken the cookie** (no `SecurePolicy.None`, no
`SameSite=None`) to "fix" this; that would undo the deliberate security posture for every
deployment, not just Tailscale.

There are **two legitimate fixes**, depending on what you're trying to do.

### Fix (a): browser login → give the Tailscale IP real HTTPS via the local CA (Ярус-1)

If you want a human to **log in through a browser** at the Tailscale address, the Tailscale IP
needs a **trusted HTTPS certificate** so the `Secure` cookie is accepted.

BeeMemoryBank's **Ярус-1 local CA** (`LocalCaService`, the local-certificate-authority feature)
issues a leaf certificate for this node. **Add the node's Tailscale IP (and, ideally, its
Tailscale MagicDNS hostname) to that leaf certificate's SAN (Subject Alternative Name) list.** Once
the Tailscale IP is in the SAN list:

- the node serves real HTTPS on the Tailscale IP,
- the browser trusts it (the local CA is trusted on devices you control), and
- the `Secure` `bee_session` cookie works normally — full browser login over Tailscale.

This is the correct fix for any **interactive, cookie-based** access. (The internals of how the
local CA issues and rotates the leaf certificate are covered by the `LocalCaService`
task/feature — not re-explained here.) After wiring the SAN in, browse to
`https://100.x.y.z:5301` instead of `http://`.

### Fix (b): machine-to-machine access → no cookie needed, plain HTTP is fine

Not everything that talks to BeeMemoryBank uses the browser cookie. Two access patterns **don't
need the `Secure` cookie at all** and therefore work over a Tailscale IP with **plain HTTP and
zero certificate changes**:

1. **MCP agent access via a Bearer token.** Agents authenticate with a Bearer token in the
   `Authorization` header — not with `bee_session`. No cookie is involved, so the `Secure`-cookie
   restriction is irrelevant. Point the agent at `http://100.x.y.z:5300/mcp` (the API/MCP port)
   and it works.
2. **Node-to-node sync via Ed25519 signatures.** Sync (`/api/sync`) authenticates with
   challenge/response Ed25519 signatures tied to each node's identity key — again, no session
   cookie. Two nodes peered over Tailscale sync over plain HTTP with no changes.

So if your goal is **agent access or node-to-node sync**, you can skip the certificate work
entirely: plain HTTP over the Tailscale IP "just works". You only need Fix (a) when a **human
browser session** is involved.

### TL;DR decision table

| What you want to do over Tailscale              | Works over `http://`? | Recommended setup                         |
|-------------------------------------------------|-----------------------|-------------------------------------------|
| Browser login (human, `bee_session` cookie)     | **No** (Secure cookie) | Fix (a): add Tailscale IP to local-CA SAN → `https://` |
| MCP agent access (Bearer token)                 | **Yes**               | Fix (b): plain HTTP is fine               |
| Node-to-node sync (Ed25519 signatures)          | **Yes**               | Fix (b): plain HTTP is fine               |

---

## Quick setup checklist

1. Install Tailscale on each node and bring it up on the same tailnet.
2. Note each node's stable Tailscale IP (e.g. `100.64.0.5`).
3. **For sync / agent access:** point peers/agents at `http://<tailscale-ip>:<port>` — done.
4. **For browser logins:** add the Tailscale IP to the node's local-CA leaf certificate SAN list
   (Ярус-1 / `LocalCaService`), then browse to `https://<tailscale-ip>:<web-port>`.
5. (Optional) Enable Tailscale MagicDNS so you can use a hostname like `mylaptop` instead of the
   raw IP — and add that hostname to the SAN list too.
