# KidControl fleet backend — deploy (Tashkent VM)

Brings up the fleet control-plane behind HTTPS and points one real agent at it. See
[RFC-01](../docs/RFC-01-fleet-backend.md) for the design.

## What runs where
- **VM (Tashkent, `157.22.133.185`)** — `postgres` + `backend` (+ Caddy for TLS), via
  `docker compose`. Secrets rendered from **Infisical** into `deploy/.env`.
- **Windows PC(s)** — the KidControl agent in **managed mode** (`Fleet:Url` set), enrolled
  with a one-time code from the bot.

Meta is blocked in RF but this VM is in **Uzbekistan** — Telegram long-poll works outbound.

## 1. Prerequisites
- DNS: point a hostname (e.g. `fleet.oresh.in`) at the VM's public IP.
- A Telegram bot token (from @BotFather) and your Telegram chat id (send `/myid` to the bot
  once it runs, or to @userinfobot).
- Docker + Docker Compose on the VM.

## 2. Render secrets (Infisical)
On the VM, in the repo's `deploy/` dir:
```bash
cp .env.template .env       # then fill it, or render from Infisical:
# infisical export --env=prod --path=/kidcontrol > .env
```
Required in `.env`:
- `POSTGRES_PASSWORD` — strong, unique.
- `TELEGRAM_BOT_TOKEN` — the bot token (enables the operator bot).
- `FLEET_ADMIN_CHAT_ID` — your Telegram chat id (seeded as the first admin on boot).
- `FLEET_ADMIN_API_KEY` — optional; only if you want the `/admin/*` HTTP API (the bot doesn't
  need it). Leave empty to keep those endpoints disabled (404).

`deploy/.env` is gitignored — never commit it.

## 3. TLS / hostname
Edit `deploy/Caddyfile`: replace `fleet.example.com` with your hostname. Caddy auto-provisions
a Let's Encrypt certificate on first start (ports 80+443 must be open to the VM).

## 4. Bring it up
```bash
cd deploy
# Host already runs Caddy → just postgres + backend (backend on 127.0.0.1:8088):
docker compose up -d --build
# OR let compose run Caddy too (needs ports 80+443 free on the VM):
docker compose --profile caddy up -d --build

docker compose ps
docker compose logs -f backend        # watch for "Application started" + migrate/seed
```
The backend applies EF migrations and seeds the tenant + your admin on boot. If the host runs
Caddy, proxy your hostname to `127.0.0.1:8088` there instead of using the `caddy` profile.

## 5. Smoke test (the DoD)
1. **HTTPS up:** `curl -s https://<host>/health` → `{"status":"ok",...}`;
   `curl -s -o /dev/null -w '%{http_code}' https://<host>/health/db` → `200`.
2. **Bot answers:** open the bot in Telegram, send `/start` → device list (empty), then
   `/enroll` → a one-time code.
3. **Enroll a real agent:** on the Windows PC, set in
   `C:\ProgramData\KidControl\appsettings.json`:
   ```json
   "Fleet": { "Url": "https://<host>", "EnrollCode": "<CODE-FROM-BOT>" }
   ```
   Restart the service (`sc stop KidControlService && sc start KidControlService`, or
   reboot). The agent enrolls once and stores its token (DPAPI, `%ProgramData%`).
   The device appears in the bot's device list within a heartbeat.
4. **Policy change:** in the bot, select the device → ⚙️ Интервалы → `40/20`. Within a
   heartbeat the agent applies it (widget shows the new rule).
5. **Command:** select the device → ➕ Время → `+30`. The agent adds 30 min once.
6. **Offline test:** disconnect the PC from the network. The agent keeps enforcing the
   cached policy (timer/night still work). Queue `+15` in the bot — it waits (TTL). Reconnect:
   the agent applies policy → desired → the queued command in order (TTL-expired ones are
   dropped).

**DoD met** when: backend is reachable over HTTPS, and one real PC is controlled from the bot.

## 6. Update / rollback
Pushing to the release repo and rebuilding on the VM (`docker compose up -d --build`) updates
the backend. Agent binaries still come from GitHub Releases; pin a version per device with
📦 Версия in the bot (`policy.targetVersion`), or `latest` to track newest (RFC §9).

## Content-monitor lists (RFC-05)
The word/adult-content lists are seeded from files on the VM (kept out of the public repo) and
cached in the DB (versioned). On this VM they live in `/opt/kidcontrol/monitor-lists/`, mounted
read-only into the backend at `/monitor-lists` via `deploy/docker-compose.override.yml`
(`MONITOR_LISTS_DIR=/monitor-lists`). Files (one entry per line, `#` comments ignored):

- `profanity.txt` — from `bars38/Russian_ban_words` (`words.txt`).
- `adult_domains.txt` — popular porn domains (`chadmayfield` top-1M ranked list, ~12k).
- `adult_keywords.txt` — curated adult keywords (RU + EN).
- `exceptions.txt` — false-positive suppressions (banned root inside an allowed word).

Seeding runs on startup **only when the DB has no terms**. To refresh: replace the files and
clear `monitor_term`, or `POST /admin/monitor-lists` (`X-Admin-Key`) with the full lists — that
bumps the version and every agent re-fetches on its next heartbeat.

## Rollback to standalone
Clear `Fleet:Url` in the PC's appsettings and restart — the agent returns to the embedded bot
and local JSON, exactly as before.
