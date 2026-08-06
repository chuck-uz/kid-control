# Security

> Reference implementation — not yet compiled. See [README.md](README.md).

## Threat model

**The adversary is the child** — the person sitting at the console, motivated and
patient, who may reach a **local Administrator** account (a shared family PC, a known
password, or a stock-Windows box where the only account is an admin). This is a hostile
local-user model, which is much harder than the usual "keep remote attackers out": the
adversary owns the machine.

Assets to protect:

- **Enforcement** — the service must keep running and keep time.
- **Timer state** — `session_state.json` must not be editable to grant free time.
- **Config** — the bot token and admin allow-list must not be readable/replaceable.
- **The update channel** — it runs as SYSTEM, so it must not become an RCE.

## How v2 addresses each risk (vs v1)

| Risk | v1 | v2 |
| --- | --- | --- |
| Malicious/spoofed update runs as SYSTEM | downloaded + executed with no verification | **Authenticode signature required** (`UpdateConfig.RequireSignature` + `TrustedThumbprint`); assets limited to `AllowedAssetHosts`; releases publish `SHA256SUMS.txt` |
| Anyone drives emergency commands | pipe present | **Admin-only command pipe** (`KidControl.Command`) with a tiny explicit protocol |
| Brute-forcing the unlock code | — | **rate-limited 6-digit OTP with lockout** (`RATE_LIMITED` response) |
| Corrupt/edited timer state | plain writes | **atomic state writes** in protected ProgramData |
| Killing the service frees the machine | best-effort restart | **critical-process option** (`ProtectionConfig.CriticalProcess`) with crash-safe flag clearing + SCM failure actions + watchdog restart |
| Deleting config disables protection | config beside binaries | config lives in **ACL-protected `%ProgramData%\KidControl`**, loaded as an optional override so the service still boots |
| Tampering with binaries | none | **tamper detection** watcher (`ProtectionConfig.TamperDetection`) + locked install-dir ACL |
| Non-SYSTEM admin deletes the service | version-baked service name orphaned it | version-free `KidControlNames.ServiceName`; **service registry key protected** (SYSTEM-only modify) |
| Debug telemetry leak | wrote to hardcoded `C:\kid-control\…\debug-9d75ca.log` | **removed entirely** — no `AgentDebugLog`, no debug paths |

## Config knobs that matter

```jsonc
// %ProgramData%\KidControl\appsettings.json  (ACL-protected)
{
  "Telegram":   { "BotToken": "…", "AdminChatIds": [ 111, 222 ] },  // command authority
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
  update path safe; leaving `RequireSignature` on and pinning a real thumbprint is
  mandatory for a production deployment.
- `ProtectionConfig.ApplyProcessDacl` / `TamperDetection` / `CriticalProcess` set the
  runtime hardening posture.

## Data handling

The **bot token is a secret.** `AppSettingsWriter` writes it only into the protected
`appsettings.json`; it is never written to a log, the console, or the installer's
progress callback. Never commit a real token or `appsettings.json` to source control.

## Remaining limitations (honest)

Against a **determined Administrator-level child**, several defences reduce to
speed bumps, not walls:

- An admin can boot to Safe Mode or WinRE, where the service does not run, and edit
  ProgramData offline. Filesystem ACLs do not survive an offline attacker.
- An admin can take ownership of files/keys (`SeTakeOwnershipPrivilege`) and rewrite the
  ACLs the installer set. The install-dir and service-key protection raise the bar; they
  do not stop ownership takeover.
- `CriticalProcess` deters casual `Kill`, but an admin can disable the service via SCM,
  registry, or Safe Mode, and can defeat process DACLs with `SeDebugPrivilege`.
- **Tamper detection is detection, not prevention** — it can log/alert and attempt
  restart, but cannot guarantee the binary was never modified.
- The child controls the network and can block update checks or the Telegram callback
  entirely (offline denial), and can uninstall given enough privilege.

The realistic security goal is therefore: **robust against a standard-user child, and a
meaningful, auditable obstacle — not an absolute barrier — against an admin-level child.**
The strongest available hardening (a separate non-admin child account, BitLocker to blunt
offline edits, and a pinned signed-update certificate) should be combined with these
application-level controls; the app alone cannot make an Administrator not an
Administrator.
