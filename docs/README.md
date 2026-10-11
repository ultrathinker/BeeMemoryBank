# BeeMemoryBank Documentation

This folder contains technical documentation for the BeeMemoryBank project.

## Documentation Index

| Document | Description |
|---|---|
| [architecture.md](architecture.md) | Project overview, technology stack, module structure, dependency graph, and key architectural decisions |
| [sync.md](sync.md) | Multi-node synchronization protocol, event sourcing, Lamport clocks, conflict resolution, push-on-save, Invisible Mode; metadata encryption (protocol 4), held events on a locked node, and the one-way activation switch |
| [encryption.md](encryption.md) | Encryption system: 3-level key hierarchy, per-article/media DEKs, session management, media encryption; the metadata key (MEK) and what metadata encryption changes; the node database key (DBK) of an encrypted node |
| [sealed-database.md](sealed-database.md) | Encryption at rest ("Encrypt this node", 2.6.0): what a sealed node protects and what it does not, the migration and its pre-encryption snapshot, locked versus unlocked restarts, the server's off-by-default auto-unlock checkbox, backups, key rotation, and what to do when something goes wrong |
| [account-recovery.md](account-recovery.md) | "Forgot your password? Use a recovery key": the Sign In page flow, `bmb user reset-password`, what it will not do, throttling, audit records |
| [mcp.md](mcp.md) | MCP server for AI agent integration: 33 tools in 7 groups, transport, truncation, configuration examples |
| [compaction.md](compaction.md) | Event log compaction and snapshot lifecycle |
| [snapshot-restore.md](snapshot-restore.md) | Restoring a snapshot: this node only (it becomes a new node) versus the whole network; what each does to identity, trusted nodes and blind copies, and what to do afterwards |
| [deployment.md](deployment.md) | Deployment guide: environment variables, systemd, Docker (the ready-made images on ghcr.io and building from source), reverse proxy, maintenance page, new node setup, audit log retention |
| [internet-access.md](internet-access.md) | Opening your node to the internet yourself: ports, router forwarding, Caddy / nginx with automatic certificates, tunnels and VPNs, security warnings |

## Other Project Files

| File | Description |
|---|---|
| [CHANGELOG.md](../CHANGELOG.md) | Release changelog |
| [CONTRIBUTING.md](../CONTRIBUTING.md) | Contribution guidelines |
| [SECURITY.md](../SECURITY.md) | Security policy and responsible disclosure |

## Reading Order

For someone new to the project:

1. **[architecture.md](architecture.md)** — understand what BeeMemoryBank is and how it's structured
2. **[encryption.md](encryption.md)** — understand the key hierarchy (critical for any code changes)
3. **[sync.md](sync.md)** — understand how data flows between nodes
4. **[mcp.md](mcp.md)** — understand the AI agent integration
5. **[deployment.md](deployment.md)** — understand how to deploy and operate

## Quick Links by Task

- "Understand the project" → [architecture.md](architecture.md)
- "Add a new API endpoint" → [architecture.md](architecture.md) (module structure), [CONTRIBUTING.md](../CONTRIBUTING.md) (build commands)
- "Work on sync" → [sync.md](sync.md)
- "Work on encryption" → [encryption.md](encryption.md)
- "Switch metadata encryption on" → [sync.md](sync.md) ("Switching Metadata Encryption On")
- "Encrypt a node's database" → [sealed-database.md](sealed-database.md) ("Encrypting a node")
- "My server restarted and is locked" → [sealed-database.md](sealed-database.md) ("Locked and unlocked: what a restart does")
- "Decide about the server's auto-unlock checkbox" → [sealed-database.md](sealed-database.md) ("Servers: “Start unlocked after a restart”")
- "Restore from a blind copy" → [snapshot-restore.md](snapshot-restore.md)
- "Someone forgot the administrator password" → [account-recovery.md](account-recovery.md)
- "Add an MCP tool" → [mcp.md](mcp.md)
- "Deploy to a new server" → [deployment.md](deployment.md)
- "Reach my node from outside my network" → [internet-access.md](internet-access.md)
- "Restore a snapshot" → [snapshot-restore.md](snapshot-restore.md)
- "Set up Docker" → [../README.md](../README.md) ("Docker: ready-made image"), then [deployment.md](deployment.md) ("The ready-made image", "Example Node Setup (Docker)")
- "Configure an AI agent" → [mcp.md](mcp.md) (Configuration Examples section)
- "Write mobile UI tests" → [CONTRIBUTING.md](../CONTRIBUTING.md) (Mobile UI Tests section)
