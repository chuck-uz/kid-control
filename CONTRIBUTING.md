# Contributing / Development guide

How to build, run, test and extend KidControl. For the system design read
[ARCHITECTURE.md](ARCHITECTURE.md); for the threat model read [SECURITY.md](SECURITY.md);
for product usage read [README.md](README.md).

## Prerequisites

- **.NET 8 SDK.**
- The **agent** projects target `net8.0-windows` (Win32/WinForms) and build **only on
  Windows**. The **backend** (`KidControl.Backend`) and the domain/application/contracts/
  infrastructure layers target `net8.0` and build/test on **Linux, macOS and Windows**.
- For the backend: **Docker** (Postgres via `deploy/docker-compose.yml`), or a local
  PostgreSQL.
- `dotnet ef` for backend migrations (`dotnet tool install --global dotnet-ef`).

## Project layout

```
src/
  KidControl.Domain            pure rules, no dependencies (Session interval engine)
  KidControl.Contracts         cross-process names + DTOs (KidControlNames, pipe protocol)
  KidControl.Fleet.Contracts   agent ↔ backend DTOs (Status, policy, commands)
  KidControl.Application        use-cases / ports (SessionService)
  KidControl.Infrastructure    Telegram, IPC, update, fleet client, config (net8.0)
  KidControl.Platform          shared Win32 (ProcessProtection)
  KidControl.ServiceHost       LocalSystem enforcement host           (net8.0-windows)
  KidControl.UiHost            per-user timer UI                       (net8.0-windows)
  KidControl.Installer.Core    testable install logic (NO WinForms)   (net8.0-windows)
  KidControl.Installer         thin WinForms wizard + headless entry  (net8.0-windows)
  KidControl.Backend           fleet API + Telegram bot + dashboard   (net8.0)
tests/
  KidControl.Domain.Tests · Application.Tests · Backend.Tests ·
  Infrastructure.Tests · Fleet.Contracts.Tests
deploy/                        docker-compose, Caddyfile, Dockerfile.runtime, DEPLOY.md
docs/                          RFCs + RELEASE-CHECKLIST
build.ps1                      clean → restore → build → test → publish
deploy.bat / update.bat        install / update the agent on a target PC
```

## Build & test

```powershell
# Full agent build (Windows, .NET 8 SDK)
./build.ps1                      # clean → restore → build (Release) → test → publish ./publish
```

```bash
# Backend + testable layers, any platform
dotnet build src/KidControl.Backend
dotnet test  tests/KidControl.Backend.Tests
dotnet test  tests/KidControl.Domain.Tests tests/KidControl.Application.Tests \
             tests/KidControl.Infrastructure.Tests
```

CI (`.github/workflows/ci.yml`) runs the same build + tests with coverage on every push
and PR. The agent projects won't compile off Windows — on macOS/Linux build and test the
`net8.0` projects only.

## Backend: run & migrate

```bash
cd deploy && docker compose up -d            # Postgres (+ backend if you want the full stack)
dotnet run --project src/KidControl.Backend  # API + bot + dashboard on the configured port
```

Migrations are applied automatically on startup (`Fleet:AutoMigrate`, default true). To
add one:

```bash
dotnet ef migrations add <Name> \
  --project src/KidControl.Backend --startup-project src/KidControl.Backend
```

A design-time factory (`FleetDbContextDesignTimeFactory`) lets `dotnet ef` run without
booting the app or hitting a real DB. The dashboard is served at `/dashboard`; the
`/admin/*` API is gated by the `X-Admin-Key` header (`FLEET_ADMIN_API_KEY`).

## Adding a feature — the usual shape

1. **Domain/logic first, clock-free.** Pure rules go in `KidControl.Domain` (see
   `Session`); orchestration that needs time/IO goes in `KidControl.Application` behind an
   injected `IClock` and a port interface. This is what keeps the engine unit-testable.
2. **Cross-process contract** — if the agent and backend must agree on a shape, put the
   DTO in `KidControl.Fleet.Contracts` (or `KidControl.Contracts` for on-box IPC). Bump
   the relevant version so stale agents get a fresh snapshot.
3. **Infrastructure implements the port** — Telegram/IPC/update/fleet/config live here;
   wire it in the host's DI container. Never let Application depend on Infrastructure.
4. **Backend surface** — a minimal-API endpoint in `Program.cs` + a service; keep secrets
   out of logs and the response.
5. **UI** — the per-user timer/overlay in `UiHost`, or a card in the dashboard
   (`wwwroot/dashboard.html`, self-contained, dark theme, 8-pt spacing).
6. **Tests** — cover the risky part. The engine and the backend are the well-tested
   seams; add cases alongside the existing suites.

## Testing conventions

- **TDD for pure functions** — the interval engine, plan/slot math and output parsing were
  written test-first. Domain and Application tests use a hand-rolled `TestClock`
  (`TimeProvider`) so time is explicit.
- **Backend tests** use EF Core's in-memory provider and a disabled Telegram client
  (`new TelegramBotClient("0:DISABLED")`) so no network is touched.
- Prefer proving behaviour (offline accounting, night boundary, usage accumulation) over
  trusting that it compiles.

## Releasing

Releases are fully automated — pushing a `vX.Y.Z` tag triggers the `Release` workflow.

```bash
git checkout main && git pull
git tag -a v2.6.0 -m "KidControl 2.6.0"
git push origin v2.6.0            # ← this runs .github/workflows/release.yml
```

The workflow (windows-latest) runs tests, stamps the version from the tag via MinVer,
signs the executables if `CODE_SIGNING_PFX_BASE64` is set (it currently is **not** — see
[SECURITY.md](SECURITY.md)), and publishes a GitHub Release with the setup zip, the
payload EXEs and `SHA256SUMS.txt`. Installed agents auto-update from there. The full
checklist is in [docs/RELEASE-CHECKLIST.md](docs/RELEASE-CHECKLIST.md).

The **backend** deploys separately via Docker (`deploy/DEPLOY.md`) — build the amd64 image
locally, ship it to the server and `docker compose up -d --no-build`.

## Conventions

- User-facing strings and most comments are in **Russian**; identifiers in **English**.
- Commits do **not** include Co-Authored-By trailers.
- **Secrets only in `.env` / the protected `appsettings.json`** — never in code, logs, or
  commits. No real bot token or `appsettings.json` in source control.
- Keep the agent self-contained; the backend self-migrates and self-serves the dashboard.
