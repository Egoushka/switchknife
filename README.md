# Switchknife

Small .NET libraries for services that run on their own: each package does one job, works with Native AOT, and has no dependency on the others unless it says so. MIT.

| Package | What it does | Status |
|---|---|---|
| [Switchknife.Journal](src/Switchknife.Journal) | A durable step journal on SQLite: a side effect records its intent before it runs and its result after, so a restarted process finishes or cleans up what it left. Events per flow, replayable. | 0.x |

More packages arrive as their code proves itself in more than one project: Docker engine client, GitHub and GitLab clients with app auth, egress proxy, MCP hosting, service defaults, a test kit.

## Rules for every package

- Native AOT and trimming: `IsAotCompatible`, no reflection-based JSON, and `scripts/aot-smoke.sh` publishes and runs a sample of each package without the JIT.
- One job per package; its public API stays small and is checked against the last release (package validation).
- Versioned on its own: a tag `<package>/v<version>` (e.g. `journal/v0.1.0`) releases that package alone. `0.x` until two projects use it.

## Build

```bash
dotnet build
dotnet test
scripts/aot-smoke.sh
```
