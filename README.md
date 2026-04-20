# Kid Control

Windows-based parental time-control system with Telegram bot management.

## Solution Layout

- `src/KidControl.ServiceHost` - Windows Worker Service host.
- `src/KidControl.UiHost` - WPF UI host (widget + lock screen).
- `src/KidControl.Application` - use cases and orchestration layer.
- `src/KidControl.Domain` - domain model and policies.
- `src/KidControl.Contracts` - IPC contracts and shared DTOs.
- `src/KidControl.Infrastructure` - adapters (Telegram, IPC, persistence, watchdog).
- `src/KidControl.Bootstrap` - installation/bootstrap utility.
- `tests/*` - domain, application, and infrastructure test projects.
