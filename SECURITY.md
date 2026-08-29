# Security

The threat model and hardening posture for KidControl. For the design read
[ARCHITECTURE.md](ARCHITECTURE.md).

## Threat model

**The adversary is the child** — the person sitting at the console, motivated and
patient, who may reach a **local Administrator** account (a shared family PC, a known
password, or a stock-Windows box where the only account is an admin). This is a hostile
local-user model, much harder than keeping remote attackers out: the adversary owns the
machine.

Assets to protect:

- **Enforcement** — the service must keep running and keep time.
- **Timer state** — `session_state.json` must not be editable to grant free time.
- **Config** — the bot token and admin allow-list must not be readable/replaceable.
- **The update channel** — it runs as SYSTEM, so it must not become an RCE.
- **The control plane** — the fleet backend, its admin API and Telegram bot must only
  answer to the parent.

## How v2 addresses each risk (vs v1)

| Risk | v1 | v2 |
| --- | --- | --- |
| Malicious/spoofed update runs as SYSTEM | downloaded + executed with no verification | **Authenticode signature gate** (`UpdateConfig.RequireSignature` + `TrustedThumbprint`); assets limited to `AllowedAssetHosts`; releases publish `SHA256SUMS.txt`; swap runs **detached & crash-safe** with rollback |
| Anyone drives emergency commands | pipe present | **Admin-only command pipe** (`KidControl.Command`) with a tiny explicit protocol |
| Brute-forcing the unlock code | — | **rate-limited 6-digit OTP with lockout** (`RATE_LIMITED` response) |
| Corrupt/edited timer state | plain writes | **atomic state writes** in protected ProgramData |
| Killing the service frees the machine | best-effort restart | **critical-process option** + crash-safe flag clearing + SCM failure actions + watchdog restart |
| Deleting config disables protection | config beside binaries | config lives in **ACL-protected `%ProgramData%\KidControl`**, loaded as an optional override so the service still boots |
| Tampering with binaries | none | **tamper-detection** watcher (`ProtectionConfig.TamperDetection`) + locked install-dir ACL |
| Non-SYSTEM admin deletes the service | version-baked service name orphaned it | version-free `KidControlNames.ServiceName`; **service registry key protected** (SYSTEM-only modify) |
| Debug telemetry leak | wrote to a hardcoded `C:\kid-control\…\debug.log` | **removed entirely** |

## Config knobs that matter

```jsonc
// %ProgramData%\KidControl\appsettings.json  (ACL-protected)
{
  "Telegram":   { "BotToken": "…", "AdminChatIds": [ 111, 222 ] },  // command authority
  "Fleet":      { "Url": "https://…", "EnrollCode": "…" },          // control-plane binding
  "Update":     {
    "RequireSignature": true,          // gate SYSTEM-level self-update
    "TrustedThumbprint": "ABC…",       // pin the publisher certificate
    "AllowedAssetHosts": [ "github.com", "objects.githubusercontent.com" ]
  },
  "Protection": {
    "CriticalProcess":  false,         // opt-in: killing the service BSODs the box
    "ApplyProcessDacl": true,          // deny PROCESS_TERMINATE to non-SYSTEM
    "TamperDetection":  true           // watch the install directory
  }
}
```

- `TelegramConfig.AdminChatIds` is the command allow-list; only these chats may issue
  commands (`TelegramConfig.IsAdmin`).
- `UpdateConfig.RequireSignature` / `TrustedThumbprint` are the two knobs that make the
  update path safe; for a production deployment, sign releases and pin a real thumbprint.
- `ProtectionConfig.*` set the runtime hardening posture.

## Backend / control plane

- The `/admin/*` API and the web dashboard are gated by an `X-Admin-Key` header
  (`FLEET_ADMIN_API_KEY`); with no key set the admin surface returns 404, with a wrong key
  401. The key is a secret — it lives in the server `.env` (chmod 600, not in git), not in
  the repo.
- The Telegram bot only acts for chats in the admin allow-list.
- Server secrets (`POSTGRES_PASSWORD`, `FLEET_ADMIN_API_KEY`, `TELEGRAM_BOT_TOKEN`) live
  in a git-ignored `.env` and a secrets store, rendered at deploy time — never committed.

## Current signing status

Releases are **unsigned** — the repository secret `CODE_SIGNING_PFX_BASE64` is not set.
The self-update path (auto-install and the bot's "update now", both via `UpdateService`)
is signature-gated by `Update:RequireSignature`; the `.bat` installers (`/silent`,
`/update`) are not. The shipped `deploy.bat` / `update.bat` default
`KC_REQUIRE_SIGNATURE=false`, so auto-update runs unsigned. This is a **deliberate
trade-off** for a fast-moving private fleet with a locked asset host — but it is weaker
(SYSTEM-level code install with no signature check). To harden: run `setup-signing.ps1` on
Windows (sets the two `CODE_SIGNING_PFX_*` secrets and prints the thumbprint), put it in
`KC_THUMBPRINT`, trust the `.cer` (`KC_CERT_FILE`), flip `KC_REQUIRE_SIGNATURE=true`, and
re-release.

## Data handling

The **bot token is a secret.** `AppSettingsWriter` writes it only into the protected
`appsettings.json`; it is never written to a log, the console, or the installer's progress
callback. Never commit a real token or `appsettings.json`. Screenshots and audio pulled
from a device travel over TLS to the parent's Telegram chat and are not retained by the
backend beyond delivery.

## Remaining limitations (honest)

Against a **determined Administrator-level child**, several defences reduce to speed
bumps, not walls:

- An admin can boot to Safe Mode or WinRE, where the service does not run, and edit
  ProgramData offline. Filesystem ACLs do not survive an offline attacker.
- An admin can take ownership of files/keys (`SeTakeOwnershipPrivilege`) and rewrite the
  ACLs. The install-dir and service-key protection raise the bar; they do not stop
  ownership takeover.
- `CriticalProcess` deters casual `Kill`, but an admin can disable the service via SCM,
  registry, or Safe Mode, and can defeat process DACLs with `SeDebugPrivilege`.
- **Tamper detection is detection, not prevention.**
- The child controls the network and can block update checks or the Telegram callback
  (offline denial), and can uninstall given enough privilege.

The realistic goal: **robust against a standard-user child, and a meaningful, auditable
obstacle — not an absolute barrier — against an admin-level child.** Combine the strongest
available hardening (a separate non-admin child account, BitLocker to blunt offline edits,
a pinned signed-update certificate) with these controls; the app alone cannot make an
Administrator not an Administrator.

## Reporting

Found a vulnerability? Open a [security advisory](https://github.com/chuck-uz/kid-control/security/advisories/new)
or a private issue rather than a public one. Never include a real bot token, admin key, or
`appsettings.json` in a report.
