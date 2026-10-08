# Privacy

Bee Memory Bank keeps your data on your own devices. It has no telemetry, no analytics, no crash
reporting and no account with us. Nothing is sent to the project or to anyone else unless you set
it up.

## What happens without any setup

A fresh Windows desktop install makes exactly one kind of outbound connection:

- **Update check (desktop app only).** Two minutes after start and then once a day, the app asks
  GitHub for the newest published release of
  [ultrathinker/BeeMemoryBank](https://github.com/ultrathinker/BeeMemoryBank/releases). The request
  carries no user data, only what every web request carries (your IP address and a user agent), and
  goes to GitHub, not to the project. A newer release is downloaded in the background and installed
  only when you choose **Restart to update** in the tray menu, or at the next start of the app.

Everything else listens on your own computer (`127.0.0.1`) only, until you open it yourself: see "Devices on my network" and "Connect a device" below.

## What happens only when you set it up

| Feature | Goes to | What is sent |
|---|---|---|
| Sync between your nodes | The peer nodes you add | Signed sync events. Article and media bodies are end-to-end encrypted; titles, folder paths, tags, names and timestamps are not (see [ADR-0005](docs/adr/0005-plaintext-metadata.md)) |
| Joining an existing network | The node address you enter | Your master password, the new node's id, name and public key |
| Remote accounts | The remote node you add | Username and password once, then a token |
| AI chat | [OpenRouter](https://openrouter.ai), with your own API key | The conversation, including article text the chat tools read for it, and attached images |
| Server update feed | The feed URL an operator configures | A plain download request |
| The ready-made Docker images | GitHub's container registry, `ghcr.io` | A plain download request (`docker pull`), like any image. The search model is inside the full-node image, so a running node never downloads it; the image is built on GitHub's servers, which fetch the model from huggingface.co. Building the image from source fetches it from huggingface.co on your machine instead |
| "Scan my network" when joining | Your local network (mDNS) | A standard discovery query |
| "Devices on my network" switched on (Admin > Nodes, off by default) | Everyone on your local network | The node announces its id, name, version and port (mDNS) and serves its web page and API over HTTPS on port 5311, so anyone on that network can reach the sign-in page |
| "Connect a device" (Admin > Nodes) | The device that has the join code, for 15 minutes | The few calls a join needs on port 5311, behind a one-time token; the joining device sends the master password only to a server holding the key the code pins |

## Components by other vendors

The desktop window uses Microsoft Edge WebView2, which is maintained by Microsoft and has its own
update and security services. Their privacy statement applies to them.

The iPhone blind copy (in development, not distributed yet; see [docs/blind-node/IOS.md](docs/blind-node/IOS.md)) talks only to the node
named in its call code. Its "no contact with the node" warning is a local notification the phone schedules for itself: there is no push
server, and nothing goes to Apple beyond what iOS itself does for every app.

The iPhone regular app (in development, not distributed yet; see [docs/full-node/IOS.md](docs/full-node/IOS.md)) talks only to the nodes
of your own network that it syncs with, and while joining, to the computer or server you named. It listens on nothing, has no analytics
and no push server. Its data stays in the app's own folder on the phone, excluded from iCloud and computer backups; the key for Face ID
unlock is kept by the phone's Keychain on that phone only.

## Questions

Open an issue at <https://github.com/ultrathinker/BeeMemoryBank/issues>.
