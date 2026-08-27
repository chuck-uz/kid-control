# KidControl.Backend

Control-plane for the KidControl fleet (see [RFC-01](../../docs/RFC-01-fleet-backend.md)).
ASP.NET Core (.NET 8) + PostgreSQL (EF Core / Npgsql). Shares `KidControl.Fleet.Contracts`
and `KidControl.Domain` with the agent, so policy types never drift.

**T2 status (skeleton):** boots, connects to Postgres, exposes health endpoints. Data
model + migrations are T3; agent endpoints and the in-backend bot come in later tasks.

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
