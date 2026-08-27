# KidControl.Backend

Control-plane for the KidControl fleet (see [RFC-01](../../docs/RFC-01-fleet-backend.md)).
ASP.NET Core (.NET 8) + PostgreSQL (EF Core / Npgsql). Shares `KidControl.Fleet.Contracts`
and `KidControl.Domain` with the agent, so policy types never drift.

**Status (through T3):** boots, applies migrations + seeds on start, exposes health
endpoints, and owns the full §4 data model. Agent endpoints and the in-backend bot come in
later tasks (T4+).

## Data model (T3)
The §4 entities live in `Entities/` and are mapped by `Persistence/FleetDbContext.cs` to
snake_case PostgreSQL tables (`tenant`, `admin`, `device`, `device_policy`,
`device_desired`, `device_status`, `command`, `enroll_code`, `audit`). `command.payload_json`
and `audit.detail_json` are `jsonb`. Policy/desired carry a monotonic per-device `version`.

Migrations are in `Persistence/Migrations/`. On boot the app runs `Database.Migrate()` and
seeds the reserved single tenant (in the migration) plus the operator admin (runtime, from
`FLEET_ADMIN_CHAT_ID` — nothing committed). Disable the boot migrate with `Fleet:AutoMigrate=false`.

```bash
# create/apply the schema by hand (design-time; needs the dotnet-ef tool)
dotnet tool install --global dotnet-ef --version 8.0.10
dotnet ef database update --project src/KidControl.Backend \
  --connection "Host=localhost;Port=5432;Database=kidcontrol;Username=kidcontrol;Password=..."
```

## Enrollment & auth (T4)
An operator mints a **single-use code**; an agent redeems it for a **per-device bearer
token**. Only the token's SHA-256 hash is stored — the plaintext is returned once and never
persisted. Agent endpoints authenticate with `Authorization: Bearer <token>` (scheme
`DeviceToken`, resolved to a non-revoked device by `token_hash`).

- `POST /agent/enroll` — anonymous. Body `{code, machineName, osInfo?, agentVersion?}` →
  `{deviceId, token}`. `404` unknown code · `400` expired · `409` already used. Provisions
  the device's default policy/desired rows.
- `GET /agent/whoami` — **requires** a device token; returns `{deviceId, name}` (a probe).
- `POST /admin/enroll-code` — mints a code. **Temporary** operator surface until the bot
  (T11) owns it: guarded by `X-Admin-Key` matching `Fleet:AdminApiKey` / `FLEET_ADMIN_API_KEY`;
  returns `404` (disabled) when that key is unset.

## Heartbeat & policy sync (T6)
- `POST /agent/heartbeat` — **requires** a device token. Body `{status, policyVersion,
  desiredVersion}`; records the reported status + liveness and answers `{policy?, desired?,
  hasCommands}` — the `policy`/`desired` snapshot is present **only** when the agent's held
  version is behind (delta sync). An operator policy edit bumps the per-device version, so the
  change reaches the device on its next heartbeat.
- `GET /admin/devices` — list devices with live status + policy version (admin key).
- `POST /admin/devices/{id}/policy` — partial policy edit (`{playMinutes?, restMinutes?,
  nightEnabled?, nightStart?, nightEnd?, intervalsEnabled?, targetVersion?}`); bumps the
  version, returns `{policyVersion}` (admin key).

## Endpoints
- `GET /health` — liveness (no DB), returns `{status, service, version}`.
- `GET /health/db` — readiness, 200 if PostgreSQL is reachable, 503 otherwise.

## Run locally
```bash
# Postgres for local dev
docker run --rm -e POSTGRES_DB=kidcontrol -e POSTGRES_USER=kidcontrol \
  -e POSTGRES_PASSWORD=postgres -p 5432:5432 postgres:16

# then
dotnet run --project src/KidControl.Backend
# GET http://localhost:5000/health  and  /health/db
```
The dev connection string is in `appsettings.Development.json` (localhost default,
no real secret). In production the connection string comes from
`ConnectionStrings__Fleet` (env), rendered from Infisical.

## Deploy (Tashkent VM)
See [`deploy/`](../../deploy/): `docker-compose.yml` (postgres + backend + optional
Caddy), `Caddyfile`, `.env.template`.
```bash
cp deploy/.env.template deploy/.env   # or render from Infisical
# edit POSTGRES_PASSWORD, hostname in Caddyfile
docker compose -f deploy/docker-compose.yml up -d --build
```
Never commit `deploy/.env` (gitignored).
