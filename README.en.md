<div align="center">

# KidControl

**Screen-time parental control for Windows — rules, night lock, and a full view of the kid's PC, all driven from Telegram and a web dashboard.**

[![Release](https://img.shields.io/github/v/release/chuck-uz/kid-control?color=0891B2)](https://github.com/chuck-uz/kid-control/releases/latest)
[![Windows](https://img.shields.io/badge/Windows-10%2F11-blue)](#requirements)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4)](#requirements)
[![CI](https://img.shields.io/github/actions/workflow/status/chuck-uz/kid-control/ci.yml?branch=main&label=CI)](https://github.com/chuck-uz/kid-control/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow)](LICENSE)

[Русская версия](README.md)

</div>

## Why

Parental control usually lives on one PC: to change anything you have to sit at the
kid's machine. KidControl is built as a **fleet of managed agents** — each child PC runs
a background agent, and the parent manages every machine remotely, from a **Telegram bot**
or a **web dashboard**. Grant 15 minutes, turn on a lock, pull a screenshot, check how
much time was spent today — all from a phone, without walking over to the PC.

The agent is a LocalSystem Windows service: it counts screen time by rules (play/rest),
locks the screen during the night window, survives reboots, and updates itself from the
releases in this repository with no manual step.

## Features

- **Interval-based screen time** — a "play N minutes / rest M minutes" rule with an
  on-screen timer; when play runs out a break overlay appears. While the PC is off or
  asleep only the *break* is consumed — play time is never spent — so shutting down
  mid-play preserves the remainder and the break ticks down in the background.
- **Night lock** — inside a configured window (e.g. 22:00–07:00) the screen is locked;
  the first boot after the night starts a fresh full play day.
- **Telegram control** — PC status, "add time", lock/unlock, edit rules
  (play/rest/night), emergency one-time-code unlock, pick the agent target version and
  update in place.
- **Night-attempt alerts** — if the child tries to use the PC during the night window,
  the parent gets a Telegram notification (deduplicated, so it doesn't spam).
- **Remote screenshot** — request a snapshot of the child's screen from the bot; the
  image arrives in the chat.
- **Remote audio** — play a voice note / audio sent to the bot on the child's PC (call
  them to the phone, warn by voice).
- **Web dashboard** — a grid of all devices, a detail card with history and **per-day
  screen-time charts**; login by admin key, auto-refreshing.
- **Crash-safe self-update** — the agent picks up new releases itself. The binary swap
  runs in a detached one-shot SYSTEM scheduled task (outside the service's process tree),
  with a backup and rollback on failure — an update can't brick the agent.
- **Survives reboots** — timer state lives in an ACL-protected `%ProgramData%` folder and
  is written atomically; after a reboot/update the remaining time is restored, not reset.

## How it works

```
┌─────────────────────┐        HTTPS (enroll · heartbeat · long-poll)      ┌──────────────────────┐
│  Agent on kid's PC   │ ───────────────────────────────────────────────► │   Fleet backend       │
│  ServiceHost (SYSTEM)│ ◄─────────────────────────────────────────────── │   ASP.NET Core + EF   │
│  UiHost (per-user)   │        desired-state · policy · commands          │   PostgreSQL          │
└─────────────────────┘                                                    └──────────┬───────────┘
        timer, night,                                                                  │
        screenshot, audio                                          Telegram bot ◄───────┤
                                                                   Web dashboard ◄──────┘
                                                                   (parent runs the fleet)
```

- **Agent** (`KidControl.ServiceHost` + `KidControl.UiHost`) applies rules locally, sends
  a status heartbeat and long-polls for commands; the pure interval engine
  (`KidControl.Domain` / `Application`) is clock-free and unit-tested offline.
- **Backend** (`KidControl.Backend`) — an ASP.NET Core minimal API, EF Core + Npgsql
  (snake_case), migrations applied on startup; it also hosts the Telegram bot and serves
  `wwwroot/dashboard.html`.
- **Releases** build on GitHub Actions (windows-latest) on a `vX.Y.Z` tag, and the agent
  updates itself. Details in [ARCHITECTURE.md](ARCHITECTURE.md).

## Installing the agent (child PC)

Requires Windows 10/11 and administrator rights. One script installs everything (pulls
the .NET 8 Desktop Runtime via winget if needed, registers the service, wires up the
fleet enrollment and enables self-update):

```bat
deploy.bat
```

It downloads the latest release, installs the agent silently and writes a protected
`%ProgramData%\KidControl\appsettings.json` (fleet URL, enroll code, auto-update). Update
an already-installed agent manually with:

```bat
update.bat
```

The full fleet-deployment story (backend, DNS, TLS) is in [deploy/DEPLOY.md](deploy/DEPLOY.md).

## Managing (parent)

| Task | Where |
|---|---|
| PC status, time left | Telegram: `/status` |
| Add time / lock / unlock | Bot buttons |
| Edit rules (play / rest / night) | Bot → device policy |
| Emergency code unlock | Bot → one-time OTP |
| Screenshot of the child's screen | Bot → "Screenshot" |
| Play voice/audio on the PC | Send a voice note to the bot |
| Pick agent version / update | Bot → versions |
| All devices + per-day charts | Web dashboard `/dashboard` (admin-key login) |

## Requirements

- **Agent:** Windows 10/11, .NET 8 Desktop Runtime (installed automatically), admin
  rights to install.
- **Backend:** Docker + PostgreSQL (or the ready `docker-compose` under `deploy/`), a
  domain with TLS.
- A Telegram bot token (from [@BotFather](https://t.me/BotFather)).

## For developers

The internals, documented in depth:

- **[ARCHITECTURE.md](ARCHITECTURE.md)** — layers and dependency rules, the IPC model
  (named pipes + ACLs), update-channel trust, restart recovery, the domain interval
  engine, and the fleet design (enroll/heartbeat/desired-state).
- **[CONTRIBUTING.md](CONTRIBUTING.md)** — how to build, test, add a feature and cut a
  release; conventions and project layout.
- **[SECURITY.md](SECURITY.md)** — the threat model (the adversary is the child, possibly
  an admin), how v2 closes each risk, and the honest limitations.
- **[docs/](docs/)** — subsystem RFCs (`RFC-01` fleet backend, `RFC-02` Phase 2, `RFC-03`
  Android agent, `RFC-04` shared budget) and the [release checklist](docs/RELEASE-CHECKLIST.md).

## Build

```powershell
# Windows, .NET 8 SDK
./build.ps1        # clean → restore → build (Release) → tests → publish to ./publish
```

`build.ps1` fails hard on any error and produces framework-dependent single-file win-x64
binaries. The backend (`KidControl.Backend`, `net8.0`) and the domain layers build on
Linux/macOS too; the agent projects (`net8.0-windows`) build only on Windows. CI
(`.github/workflows/ci.yml`) runs the same build + tests on every push and PR.

Fleet-backend deployment is in [deploy/DEPLOY.md](deploy/DEPLOY.md) (Docker Compose +
Caddy for TLS).

## Security & privacy

The adversary in the threat model is **the child at the console**, possibly with local
administrator rights. KidControl makes protection robust against a standard user and a
meaningful, auditable obstacle (not an absolute wall) against an admin-level child:
Authenticode-signed updates, an admin-only command pipe, a rate-limited OTP, atomic state
writes in ACL-protected ProgramData, a protected service registry key. The bot token and
the admin list are secrets — they live only in the protected `appsettings.json` and
**never** land in code, logs, or commits. The full threat model and honest limitations
are in [SECURITY.md](SECURITY.md).

## License

[MIT](LICENSE)
