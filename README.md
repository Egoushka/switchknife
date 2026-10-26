# Switchknife

Small .NET libraries for services that run on their own: each package does one job, works with Native AOT, and has no dependency on the others unless it says so. MIT.

| Package | What it does | Status |
|---|---|---|
| [Switchknife.Hosting](src/Switchknife.Hosting) | `PollingService`, a `BackgroundService` that polls on start and then every interval and never stops the host: a failed poll is logged and retried with exponential backoff, health hooks are optional and guarded, shutdown is clean. `TimeProvider` throughout. | 0.x |
| [Switchknife.Journal](src/Switchknife.Journal) | A durable step journal on SQLite: a side effect records its intent before it runs and its result after, so a restarted process finishes or cleans up what it left. Events per flow, replayable. | 0.x |
| [Switchknife.McpGuard](src/Switchknife.McpGuard) | Guards an MCP endpoint on streamable HTTP: any `Origin` header refused (the DNS-rebinding guard), a bearer token compared in fixed time, closed when no token is configured. | 0.x |
| [Switchknife.Migrations](src/Switchknife.Migrations) | Applies the SQL scripts embedded in an assembly to PostgreSQL with DbUp, once each, journalled in the database; creates the database when missing, logs through `ILogger`, throws naming the failed script. | 0.x |
| [Switchknife.Testing.Http](src/Switchknife.Testing.Http) | A stub `HttpMessageHandler` for tests: answers from a function or a scripted sequence and records each request with its body and headers, read before the caller disposes it. No test-framework dependency. | 0.x |
| [Switchknife.Testing.Postgres](src/Switchknife.Testing.Postgres) | A PostgreSQL server in a Testcontainers container for integration tests: started once per run, migrated by a callback, emptied between tests or split into a database per test; reports a missing Docker outside CI so tests can skip. No test-framework dependency. | 0.x |

Designed, not built: a bi-temporal facts store for Nytka and feed-eater, in [`docs/adr/0001-facts-library.md`](docs/adr/0001-facts-library.md) with its [spec](docs/specs/2026-10-26-facts-library.md), [benchmark plan](docs/benchmarks/facts-library.md) and a [harness](bench/Switchknife.Facts.Bench).

More packages arrive as their code proves itself in more than one project: Docker engine client, GitHub and GitLab clients with app auth, egress proxy, MCP hosting, service defaults, a test kit.

## Rules for every package

- Native AOT and trimming: `IsAotCompatible`, no reflection-based JSON, and `scripts/aot-smoke.sh` publishes and runs a sample of each package without the JIT. Exception: a test-support package (`Switchknife.Testing.*`) that depends on a library without AOT support sets `IsAotCompatible` to false and has no sample; its README says so.
- One job per package; its public API stays small and is checked against the last release (package validation).
- Versioned on its own: a tag `<package>/v<version>` (e.g. `journal/v0.1.0`) releases that package alone. `0.x` until two projects use it.

## Build

```bash
dotnet build
dotnet test
scripts/aot-smoke.sh
```
