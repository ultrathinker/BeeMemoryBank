# BeeMemoryBank Documentation

This folder contains technical documentation for the BeeMemoryBank project.

## Documentation Index

| Document | Description |
|---|---|
| [architecture.md](architecture.md) | Project overview, technology stack, module structure, dependency graph, and key architectural decisions |
| [sync.md](sync.md) | Multi-node synchronization protocol, event sourcing, Lamport clocks, conflict resolution, push-on-save, Invisible Mode |
| [encryption.md](encryption.md) | Encryption system: 3-level key hierarchy, per-article/media DEKs, session management, media encryption |
| [mcp.md](mcp.md) | MCP server for AI agent integration: 16 tools in 6 groups, transport, truncation, configuration examples |
| [deployment.md](deployment.md) | Deployment guide: environment variables, systemd, Docker, reverse proxy, maintenance page, new node setup |

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
- "Add a new API endpoint" → [architecture.md](architecture.md) (module structure)
- "Work on sync" → [sync.md](sync.md)
- "Work on encryption" → [encryption.md](encryption.md)
- "Add an MCP tool" → [mcp.md](mcp.md)
- "Deploy to a new server" → [deployment.md](deployment.md)
- "Set up Docker" → [deployment.md](deployment.md) (Docker Deployment section)
- "Configure an AI agent" → [mcp.md](mcp.md) (Configuration Examples section)
- "Write mobile UI tests" → [CONTRIBUTING.md](../CONTRIBUTING.md) (Mobile UI Tests section)
