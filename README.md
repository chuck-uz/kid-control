# KidControl v2

A .NET 8 Windows parental-control application (enforcement service + per-user UI +
Telegram bot + hardened installer).

Full documentation lives in **[docs/README.md](docs/README.md)**:

- [docs/README.md](docs/README.md) — overview, architecture, build/install/uninstall
- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) — layers, IPC, update trust, restart recovery
- [docs/SECURITY.md](docs/SECURITY.md) — threat model and hardening

> [!NOTE]
> This code was rewritten on macOS without a .NET SDK and has **not** been compiled.
> Treat it as a design/reference implementation to build and test on Windows.

Quick start (Windows, .NET 8 SDK):

```powershell
./build.ps1   # clean → restore → build → test → publish to ./publish
```
