# BeeMemoryBank Documentation

This folder contains technical documentation for the BeeMemoryBank project.

## Documentation Index

| Document | Description |
|---|---|
| [architecture.md](architecture.md) | Project overview, technology stack, module structure, dependency graph, and key architectural decisions |
| [sync.md](sync.md) | Multi-node synchronization protocol, event sourcing, Lamport clocks, conflict resolution, push-on-save, Invisible Mode |
| [encryption.md](encryption.md) | Encryption system: 3-level key hierarchy, per-article/media DEKs, session management, media encryption |
| [account-recovery.md](account-recovery.md) | "Forgot your password? Use a recovery key": the Sign In page flow, `bmb user reset-password`, what it will not do, throttling, audit records |
| [mcp.md](mcp.md) | MCP server for AI agent integration: 33 tools in 7 groups, transport, truncation, configuration examples |
| [compaction.md](compaction.md) | Event log compaction and snapshot lifecycle |
| [snapshot-restore.md](snapshot-restore.md) | Restoring a snapshot: this node only (it becomes a new node) versus the whole network; what each does to identity, trusted nodes and blind copies, and what to do afterwards |
| [deployment.md](deployment.md) | Deployment guide: environment variables, systemd, Docker, reverse proxy, maintenance page, new node setup, audit log retention |
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
- "Someone forgot the administrator password" → [account-recovery.md](account-recovery.md)
- "Add an MCP tool" → [mcp.md](mcp.md)
- "Deploy to a new server" → [deployment.md](deployment.md)
- "Reach my node from outside my network" → [internet-access.md](internet-access.md)
- "Restore a snapshot" → [snapshot-restore.md](snapshot-restore.md)
- "Set up Docker" → [deployment.md](deployment.md) (Docker Deployment section)
- "Configure an AI agent" → [mcp.md](mcp.md) (Configuration Examples section)
- "Write mobile UI tests" → [CONTRIBUTING.md](../CONTRIBUTING.md) (Mobile UI Tests section)
