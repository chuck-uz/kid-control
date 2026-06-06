# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

KidControl is a Windows parental time-control system. It runs as a Windows NT Service and manages children's computer usage via Telegram bot commands. The system enforces play/rest time schedules and locks the screen when time is up.

**Platform:** Windows 10+ only (net8.0-windows). Build and run only on Windows.

## Commands

**Build (full release):**
```powershell
.\build.ps1
```
Publishes ServiceHost and UiHost to `publish/`, then bundles them into `KidControl.Installer.exe` under `Build/InstallerPublish/`.

**Build (individual project):**
```powershell
dotnet build KidControl.sln -c Release
```

**Run all tests:**
```powershell
dotnet test KidControl.sln
```

**Run a single test project:**
```powershell
dotnet test tests/KidControl.Domain.Tests/KidControl.Domain.Tests.csproj
dotnet test tests/KidControl.Application.Tests/KidControl.Application.Tests.csproj
dotnet test tests/KidControl.Infrastructure.Tests/KidControl.Infrastructure.Tests.csproj
```

**Install locally (requires admin):**
```powershell
.\install.ps1
```
Stops existing processes, removes the old service, copies executables to `C:\Program Files\KidControl`, and registers a new NT Service named `KidControlv0.4`.

**Release:** Push a `v*` git tag — GitHub Actions (`release.yml`) builds and publishes the release automatically. Versioning is driven by MinVer from git tags.

## Architecture

Clean Architecture with four layers:

```
Domain → Application → Infrastructure
                     ↑
              ServiceHost / UiHost (entry points)
```

### Domain (`src/KidControl.Domain/`)
Pure business logic with no external dependencies. Core types:
- `ComputerSession` — session state entity (tracks play/rest elapsed time)
- `ScheduleRule` — value object defining allowed play/rest intervals
- `LockStatus` — enum of session states (Playing, Resting, Locked, etc.)
- `SessionPolicy` — applies rules to a session to determine transitions

### Application (`src/KidControl.Application/`)
Orchestration layer. `SessionOrchestrator` is the central coordinator — it runs a tick loop, evaluates session state against policy, and dispatches notifications. Depends only on interfaces (`ITimeControlService`, `IUiNotifier`, `ITelegramNotifier`, `ISessionStateRepository`, `IUpdateService`), all implemented in Infrastructure.

### Infrastructure (`src/KidControl.Infrastructure/`)
Adapter implementations grouped by concern:
- **Telegram/** — `TelegramBotBackgroundService` + `TelegramNotifier`: Telegram Bot API integration for parent commands and alerts
- **Ipc/** — Named Pipe server/client (`NamedPipeCommandServer`, `NamedPipeUiNotifier`): IPC between ServiceHost and UiHost
- **Persistence/** — `JsonFileStateRepository`: persists session state to JSON in `%ProgramData%\KidControl\`
- **Windows/** — `ProcessWatchdog`, `TamperDetector`, `TaskSchedulerManager`: Windows-specific protection and persistence
- **Update/** — `UpdateBackgroundService` + `GitHubReleaseClient`: polls GitHub releases for auto-updates
- `InfrastructureModule.cs` — single DI registration entry point for the layer

### Contracts (`src/KidControl.Contracts/`)
Shared IPC types: `ControlCommand` (commands sent over Named Pipes) and `SessionStateDto` (state snapshots sent to UiHost).

### ServiceHost (`src/KidControl.ServiceHost/`)
Windows Worker Service — the primary process, runs as NT Service `KidControlv0.4`.
- `Program.cs` — DI setup, Serilog, DACL protection (denies PROCESS_TERMINATE), critical process marking (BSOD-on-kill)
- `Worker.cs` — hosts the orchestrator tick loop and watchdog
- `IndependentTimer.cs` — high-precision timer used for session ticks
- Configuration loaded from `appsettings.json` in install dir, overridable from `%ProgramData%\KidControl\appsettings.json`

### UiHost (`src/KidControl.UiHost/`)
WPF application — widget and lock screen shown to the child. Receives state updates from ServiceHost over Named Pipes. Uses CommunityToolkit.MVVM (MVVM pattern).

### Installer (`src/KidControl.Installer/`)
Bundles ServiceHost.exe + UiHost.exe into a single self-extracting installer executable.

## Key Design Decisions

- **Named Pipes IPC**: ServiceHost (system account) and UiHost (user session) communicate exclusively via Named Pipes. UiHost never calls domain logic directly.
- **Process protection**: ServiceHost marks itself as a Windows critical process and sets restrictive DACLs to prevent the child from killing it. Tampering is detected and logged.
- **OTP verification**: Control commands sent via Telegram require OTP verification before being executed.
- **No database**: All state is a single JSON file in `%ProgramData%\KidControl\`.
- **MinVer versioning**: Assembly version is derived from git tags — no manual version bumps needed.

## Global Project Properties

Defined in `Directory.Build.props` and applied to all projects:
- `<Nullable>enable</Nullable>` — nullable reference types enforced everywhere
- `<ImplicitUsings>enable</ImplicitUsings>`
- `<LangVersion>latest</LangVersion>`
- Target framework: `net8.0` (domain/application/contracts/tests) or `net8.0-windows` (infrastructure/hosts)
