# Contributing to BeeMemoryBank

Thank you for your interest in contributing to BeeMemoryBank! This document will help you get started.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- SQLite (bundled with the project via `Microsoft.Data.Sqlite`)

## Getting Started

### Clone and Build

```bash
git clone https://github.com/ultrathinker/BeeMemoryBank.git
cd BeeMemoryBank
dotnet build BeeMemoryBank.slnx
```

### Run Tests

```bash
dotnet test
```

### Mobile UI Tests (Maestro)

The project includes Maestro YAML tests in `mobile/maestro-tests/`. These run against a physical Android device with a BeeMemoryBank test node.

Tests reference the unlock password via the `${BMB_TEST_PASSWORD}` environment variable. Copy `mobile/maestro-tests/.env.example` to `.env` and fill in your own test-node credentials (never a production password) before running.

```bash
cp mobile/maestro-tests/.env.example mobile/maestro-tests/.env
# edit .env

# Run a single test
~/.maestro/bin/maestro test -e BMB_TEST_PASSWORD=... mobile/maestro-tests/<test>.yaml

# Run all tests
~/.maestro/bin/maestro test -e BMB_TEST_PASSWORD=... mobile/maestro-tests/
```

Key conventions:
- Files prefixed with `_` are helpers (e.g. `_go_home.yaml`), not standalone tests
- `z_` prefix = tests that run last (e.g. `z_lock_and_reunlock.yaml`)
- Each test is self-contained: creates its own data, never depends on other tests
- Dynamic names use `'a' + Date.now()` / `'f' + Date.now()` (Gboard-safe, no CamelCase)
- Always `pressKey: back` after `inputText` to dismiss keyboard before tapping buttons
- Use `scrollUntilVisible` with `timeout: 30000` instead of `extendedWaitUntil` for list items
- AutomationId in XAML → `com.beememorybank.mobile:id/XxxYyy` in Maestro
- ToolbarItem AutomationId → tap by `text:` (not `id:`), e.g. `text: "SaveToolbarButton"`

### Run the Application

```bash
# API server (port 5300)
dotnet run --project server/BeeMemoryBank.Api

# Web frontend (proxies to API, port 5301)
dotnet run --project server/BeeMemoryBank.Web
```

## Project Structure

```
BeeMemoryBank/
├── server/
│   ├── BeeMemoryBank.Api/     REST API backend (minimal APIs, port 5300)
│   ├── BeeMemoryBank.Web/     Razor Pages frontend (proxies to Api, port 5301)
│   └── BeeMemoryBank.Cli/     CLI tool
├── libs/
│   ├── BeeMemoryBank.Core/    Business logic, services, models, interfaces
│   ├── BeeMemoryBank.Crypto/  AES-256-GCM, Argon2id, Ed25519, key management
│   ├── BeeMemoryBank.Storage/ SQLite repositories (Dapper), migrations
│   └── BeeMemoryBank.Sync/    Multi-node synchronization
├── mobile/
│   └── BeeMemoryBank.Mobile/  .NET MAUI Android app
├── tests/                      xUnit test projects
│   ├── BeeMemoryBank.Core.Tests/
│   ├── BeeMemoryBank.Crypto.Tests/
│   ├── BeeMemoryBank.Storage.Tests/
│   ├── BeeMemoryBank.Sync.Tests/
│   ├── BeeMemoryBank.Cli.Tests/
│   ├── BeeMemoryBank.Integration.Tests/
│   └── BeeMemoryBank.Migrator.Tests/
└── tools/
    └── BeeMemoryBank.Migrator/ Database migration tool
```

## Code Style

The project uses these conventions (enforced via `Directory.Build.props` and `.editorconfig`):

- **Language version:** C# latest (`LangVersion=latest`)
- **Nullable reference types:** enabled (`Nullable=enable`)
- **Implicit usings:** enabled
- **Indentation:** 4 spaces (no tabs)
- **Charset:** UTF-8 with LF line endings
- **System directives:** sorted first (`dotnet_sort_system_directives_first = true`)
- **Trailing whitespace:** trimmed (except in Markdown files)
- **Final newline:** inserted automatically

### Key Patterns

- All repositories inherit `BaseRepository(DbConnectionFactory)` and use [Dapper](https://github.com/DapperLib/Dapper)
- Interfaces live in `libs/BeeMemoryBank.Core/Interfaces/`
- Models live in `libs/BeeMemoryBank.Core/Models/`
- API endpoints are minimal APIs (`MapGroup`/`MapGet`/`MapPost`) in `server/BeeMemoryBank.Api/Endpoints/`
- Request/Response records in `server/BeeMemoryBank.Api/Models/Requests.cs` and `Responses.cs`
- Migrations are embedded SQL files in `libs/BeeMemoryBank.Storage/Migrations/NNN_name.sql`
- UI uses [Shoelace 2](https://shoelace.style/) web components
- All code, comments, and UI text must be in English

## Pull Request Process

1. **Fork** the repository
2. Create a **feature branch** from `main` (`git checkout -b feature/my-feature`)
3. Make your changes and add tests where applicable
4. Ensure all tests pass (`dotnet test`)
5. Ensure the solution builds without errors (`dotnet build BeeMemoryBank.slnx`)
6. Open a **Pull Request** against `main`
7. Address review feedback

### PR Guidelines

- Keep PRs focused on a single concern
- Write clear commit messages
- Add tests for new functionality
- Update documentation if your change affects public APIs or user-facing behavior

## Reporting Issues

- Use [GitHub Issues](../../issues) to report bugs or request features
- Use the provided bug report and feature request templates
- Include version, OS, and reproduction steps for bug reports

## License

By contributing, you agree that your contributions will be licensed under the [GNU Affero General Public License v3.0](LICENSE).
