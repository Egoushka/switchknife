# CLAUDE.md — switchknife

Public MIT repository of small .NET 10 libraries, one package per folder under `src/`. Consumers: chargehand (public) and the owner's other services.

## Commands

```bash
dotnet build -c Release -warnaserror   # analyzers, AOT and trim warnings are errors
dotnet test                            # xUnit v3 on Microsoft.Testing.Platform
dotnet format --verify-no-changes
scripts/aot-smoke.sh                   # publishes samples/Switchknife.AotSmoke with Native AOT and runs it
```

## Rules

- A package has no chargehand or other consumer types in it; it enters here when a second project needs it, or when chargehand needs it and it has nothing chargehand-specific.
- Every package: `IsAotCompatible`, no reflection-based JSON (callers pass `JsonTypeInfo<T>`), a case in `samples/Switchknife.AotSmoke`, a README, tests in `tests/<Package>.Tests`.
- Versions come from tags only (MinVer, `<MinVerTagPrefix>` per project). Never put a `<Version>` in a project.
- Package versions live in `Directory.Packages.props`. Actions are pinned by commit SHA.
- Public repository: no hostnames, IP addresses, key names, home paths or employer names in any file.
- Commits: Conventional Commits.
