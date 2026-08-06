# KidControl v2

A .NET 8 Windows parental-control application. A LocalSystem **service** enforces
screen-time rules and reports state; a per-user **UI** shows the timer; a **Telegram
bot** lets a parent grant time, lock, or run an emergency unlock; a signed **installer**
deploys and hardens the whole thing.

> [!IMPORTANT]
> **This code was rewritten on macOS without a .NET SDK; it has NOT been compiled.**
> Treat it as a design/reference implementation to build and test on Windows. The
> `net*-windows` target framework, the ACL/registry/service APIs and the WinForms UI
> can only be validated on a Windows machine with the .NET 8 SDK.

---

## Why v2 exists

v1 collapsed the entire installer into a single ~1900-line `InstallerForm` that mixed
UI, service management, a GitHub HTTP client, an ACL engine, registry edits,
process-killing, P/Invoke, config generation and a silent self-updater — plus leftover
AI-debug telemetry writing to a hardcoded `C:\kid-control\…\debug-9d75ca.log`.

Silent updates had to construct a hidden `Form`, external-process calls could deadlock
on full pipe buffers, and there was no CI gate. v2 decomposes the installer into small,
single-responsibility, unit-testable classes with a headless orchestration path.

---

## Layered architecture

```
Domain            pure rules, no dependencies
Contracts         cross-process names + DTOs (KidControlNames, pipe protocol)
Application        use-cases; depends only on Domain + Contracts
Infrastructure     implements Application ports (Telegram, IPC, update, config)
Platform           shared Win32 (e.g. ProcessProtection)
Hosts              ServiceHost (LocalSystem) + UiHost (per-user) wire it together

Installer.Core     testable install logic (NO WinForms)  ← this work
Installer          thin WinForms wizard + headless entry point  ← this work
```

See [ARCHITECTURE.md](ARCHITECTURE.md) for the dependency rules, IPC model, update
trust model and restart-recovery design, and [SECURITY.md](SECURITY.md) for the threat
model.

### Installer decomposition

| Class (`KidControl.Installer.Core`) | Single responsibility |
| --- | --- |
| `ServiceInstaller` | install / uninstall / start / stop the Windows service |
| `AclManager` | lock/unlock install dir + protect ProgramData (managed `DirectorySecurity`) |
| `ProcessKiller` | terminate running KidControl processes (plain `Process.Kill`) |
| `RegistryProtector` | hide from Add/Remove, protect the service registry key |
| `AppSettingsWriter` | write `appsettings.json` into `%ProgramData%\KidControl` |
| `PayloadDeployer` | copy payload binaries, delete trees on uninstall |
| `InstallOrchestrator` | **sequence** the above; progress via `Action<string>` |
| `ProcessRunner` | deadlock-safe external-process helper (drains both pipes) |

Everything above is WinForms-free. The `KidControl.Installer` project adds only
`Program.cs` (entry point / mode selection), `HeadlessArgs.cs` and a thin
`InstallerForm` that does UI and nothing else.

---

## Build

```powershell
# Windows, .NET 8 SDK
./build.ps1
```

`build.ps1` runs clean → restore → build (Release) → **test** → publish. It fails hard
on any error and produces framework-dependent single-file win-x64 binaries in
`./publish`, with the payload EXEs staged next to the installer for offline installs.

CI (`.github/workflows/ci.yml`) runs the same build + test with coverage on every push
and PR. Releases (`.github/workflows/release.yml`) fire on a `v*` tag, test before
packaging, sign the executables, and publish a GitHub Release with a `SHA256SUMS.txt`.

---

## Install

Run the installer elevated (the manifest requests Administrator).

### Interactive

Launch `KidControl.Installer.exe`, enter the Telegram **bot token**, the **admin chat
IDs**, and the **night window**, then click **Install**.

### Silent / headless

No window is constructed — the same orchestrator runs from the console:

```powershell
# Full install
KidControl.Installer.exe /silent `
  --token "123456:ABC-DEF…" `
  --admin-ids "111111111,222222222" `
  --night-start 22:00:00 --night-end 07:00:00 `
  --source "C:\path\to\payloads"

# Binary-only update (preserves appsettings.json + session_state.json)
KidControl.Installer.exe /update --source "C:\staged\update"
```

If not already elevated, the process self-relaunches through UAC and returns the child
exit code.

---

## Uninstall

Interactive: click **Uninstall**. Headless uninstall runs through
`InstallOrchestrator.Uninstall`. Uninstall removes the service, the registry hardening,
the install directory and (by default) the ProgramData tree.

---

## Security posture (summary)

- **Signed + hash-verified self-updates** — the update path runs as SYSTEM, so releases
  are Authenticode-signed and gated by `UpdateConfig.RequireSignature` +
  `TrustedThumbprint`; the release workflow emits `SHA256SUMS.txt`.
- **Least privilege on disk** — install dir and ProgramData have inheritance removed and
  are restricted via managed ACLs.
- **Admin-only command pipe**, rate-limited OTP unlock, atomic state writes, optional
  critical-process protection.

Full detail, threat model and honest limitations are in [SECURITY.md](SECURITY.md).
