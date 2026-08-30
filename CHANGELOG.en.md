# Changelog

All notable changes to this project.
Format based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
versioning follows [Semantic Versioning](https://semver.org/).
Русская версия: [CHANGELOG.md](CHANGELOG.md).

## [Unreleased]

### Added
- **Custom night window in the bot.** The "🌙 Night" menu gains a "✏️ Custom window"
  button: the parent sends their own `HH:MM-HH:MM` window (e.g. `21:30-08:00`) instead of
  only presets. The current window is pinned as the first "⭐ … (now)" button and shown in
  📊 Status — mirroring custom intervals.

## [2.7.0] — 2026-08-30

### Added
- **Monitor: keyboard + browser URL (RFC-05, stage 3b).** Two sources join the window
  title: what the child **types** (a low-level keyboard hook — a short in-memory buffer,
  never persisted) and the active tab's **URL** (via UI Automation, Chromium/Firefox) — so
  porn **domains** by address and search queries are now caught too. The hook is installed
  only while the monitor is on and never disturbs the user's own typing.

## [2.6.0] — 2026-08-30

### Added
- **Content monitor (RFC-05).** The agent watches what the child **views** (active
  window/app title), matches it against backend-managed lists (profanity + adult keywords
  + porn domains, with false-positive exceptions) and, on a hit, **instantly** pushes the
  parent a Telegram alert with a screenshot. A "🛡️ Monitor" toggle in the Control menu
  (on by default), a line in 📊 Status, anti-spam (60s/device + 10/min), and metadata-only
  storage (no text, no screenshots) on the backend. The "keyboard" and "browser URL"
  sources were added in 2.7.0.
- **Custom intervals in the bot.** The "⚙️ Intervals" menu gains a "✏️ Custom interval"
  button — the parent sends their own play/rest value in one message (e.g. `50/10`,
  `50 10` or `50:10`) instead of picking a preset. Values are validated and clamped to
  1–1440 minutes so a malformed rule can't break policy application on the agent.
- **Current interval in quick-select.** The "⚙️ Intervals" menu pins the current rule
  (including a just-set custom one) as the first button "⭐ play/rest (now)" — one tap to
  re-apply it.
- **Current interval in status.** The "📊 Status" screen now shows the active rule
  ("Интервал: 🎮 play/rest min", or "♾️ off").

### Fixed
- **The bot no longer lags after a redeploy.** On a container restart the previous
  long-poll was still held by Telegram, so dropping the queue failed and the bot
  *replayed the whole backlog* (every queued command) — the source of the response
  delays. The queue-drop now retries until the old long-poll clears and the backlog is
  correctly skipped.

## [2.5.0] — 2026-08-29

### Added
- **Web dashboard** (`/dashboard`) — a grid of all devices and a detail card with history;
  admin-key login (`X-Admin-Key`), auto-refreshing. A self-contained
  `wwwroot/dashboard.html` over the `/admin/*` REST API.
- **Per-day screen-time charts** in the device card — how much time on the PC each day,
  with the period max and total. Active use accumulates into `device_usage_daily` (only
  the play phase, gaps > 3 min ignored, day boundary in Tashkent time).
- **Remote screenshot** — request a snapshot of the child's screen from Telegram; the
  image arrives in the chat.
- **Remote audio** — a voice note / audio sent to the bot plays on the child's PC.
- **Night-attempt alerts** — an attempt to use the PC during the night window sends the
  parent a Telegram notification (deduplicated).

### Changed
- **Off-time accounting.** While the PC is off or asleep only the **break** is consumed —
  play time is never spent (`Session.ApplyOfflineRest`). Shutting down mid-play preserves
  the remainder; the break ticks down in the background and, if it has elapsed by the next
  boot, play starts immediately. Crossing the night window's end starts a fresh full play
  day.

## [2.2.0] — 2026-08-29

### Fixed
- **Self-update no longer bricks the agent.** In v2.1 the service launched the installer
  as its own child, which stopped and `sc delete`-d that same service — a mid-swap kill
  left the machine with no service at all. The swap now runs in a **detached one-shot
  SYSTEM scheduled task** (outside the service's process tree) with a backup, a health
  check (Running + 8 s stability) and **rollback** on failure. Auto-install is on again by
  default. Validated live on a real PC (2.2.0 → 2.3.0 → 2.4.0 → 2.5.0, zero manual action).

### Changed
- **Reliable agent-version reporting** (E2) — the version is read from the executable's
  `ProductVersion` (robust for single-file), falling back to the attribute/AssemblyVersion;
  `0.0.0` and `+sha` are stripped. The real version shows in the bot and the dashboard.

## [2.1.0] — 2026-08-28

### Added
- **Fleet backend** — a managed fleet of agents: enrollment (bearer token), status
  heartbeat, command long-poll, desired-state and policies. ASP.NET Core minimal API,
  EF Core 8 + Npgsql (snake_case), migrations on startup.
- **Telegram control** — status, "add time", lock/unlock, edit rules (play/rest/night),
  emergency one-time-code unlock, pick the agent target version and update.
- **Deployment** — Docker Compose + Caddy (TLS), `deploy/DEPLOY.md`; the agent installs
  via `deploy.bat` and updates via `update.bat`.

## [2.0.0] — 2026-08-23

### Changed
- **Full v1 → v2 rewrite.** The monolithic installer (~1900-line `InstallerForm` mixing
  UI, service management, an HTTP client, ACLs, the registry, P/Invoke and self-update) was
  decomposed into small single-responsibility classes with a headless orchestration path.
  Domain rules were extracted into a pure, testable, dependency-free layer.

### Added
- **Layered architecture** (Domain · Contracts · Application · Infrastructure · Platform ·
  Hosts · Installer) with strict dependency rules.
- **Security** — an Authenticode self-update gate, an admin-only command pipe, a
  rate-limited OTP, atomic state writes in ACL-protected ProgramData, a protected service
  registry key. Full threat model in [SECURITY.md](SECURITY.md).
- **CI/CD** — build and tests on every push/PR; release on a `vX.Y.Z` tag with
  `SHA256SUMS.txt`.

### Removed
- Debug telemetry that wrote to a hardcoded `C:\kid-control\…\debug.log`.
