#!/usr/bin/env bash
# Redeploy the fleet backend to the Oracle Cloud VM (Chicago, Ampere A1 = arm64), in one
# command, from this Mac.
#
# The image is built here for linux/arm64 -- natively, the Mac is Apple Silicon -- and
# shipped over ssh (docker save | load). See deploy/DEPLOY.md.
#
# Oracle images only allow ssh as ubuntu, and docker needs root there, hence sudo on
# every remote docker call.
#
# The tag keeps its historical name "amd64": the VM's docker-compose.override.yml (not in
# git) runs image kidcontrol-backend:amd64 with pull_policy: never. The image itself is
# arm64. Renaming the tag means editing that file on the VM in the same step.
#
#   ./deploy/redeploy-vm.sh            # publish, build, ship, restart, verify
#   SKIP_PUBLISH=1 ./deploy/redeploy-vm.sh   # reuse ./publish-backend as it is
#
# Needs HEALTH (env or deploy/redeploy.local.env).
set -euo pipefail

VM="${VM:-ubuntu@147.224.169.237}"
KEY="${KEY:-$HOME/.ssh/oracle_uz}"
IMAGE=kidcontrol-backend:amd64
REMOTE_DIR=/opt/kidcontrol/deploy
cd "$(dirname "$0")/.."

# Real hostnames stay out of git: HEALTH (the public health URL, e.g.
# https://kidcontrol.example.com/health/db) comes from the environment or from the
# untracked deploy/redeploy.local.env (see deploy/redeploy.local.env.example).
# A HEALTH already in the environment wins over the file.
if [ -f deploy/redeploy.local.env ]; then
  env_health="${HEALTH:-}"
  # shellcheck source=/dev/null
  . deploy/redeploy.local.env
  HEALTH="${env_health:-${HEALTH:-}}"
fi
: "${HEALTH:?set HEALTH=https://<backend-host>/health/db, or put it in deploy/redeploy.local.env}"

say() { printf '\n==> %s\n' "$1"; }

# The SDK is a side-by-side install here, so it is missing from PATH in non-login shells.
DOTNET="${DOTNET:-$(command -v dotnet || true)}"
[ -n "$DOTNET" ] || DOTNET="$HOME/.dotnet/dotnet"

if [ -z "${SKIP_PUBLISH:-}" ]; then
  [ -x "$DOTNET" ] || { echo "no dotnet SDK: set DOTNET=/path/to/dotnet, or SKIP_PUBLISH=1 to reuse ./publish-backend" >&2; exit 1; }
  say 'Publishing the backend (framework-dependent, portable IL)'
  "$DOTNET" publish src/KidControl.Backend -c Release -o ./publish-backend
fi

say "Building $IMAGE for linux/arm64"
docker buildx build --platform linux/arm64 -f deploy/Dockerfile.runtime -t "$IMAGE" --load .

say 'Shipping the image to the VM'
docker save "$IMAGE" | gzip | ssh -i "$KEY" "$VM" 'gunzip | sudo docker load'

say 'Restarting the backend'
ssh -i "$KEY" "$VM" "cd $REMOTE_DIR && sudo docker compose up -d backend"

# Every 'docker load' installs a new image and leaves the previous one untagged.
# Nothing collects those: on 5 Sep 2026 eighteen of them (~365 MB each) had piled up
# and the 30 GB disk was 92% full. Prune right here, while the context is obvious --
# a weekly cron on the VM only catches what this step misses.
say 'Removing the image this deploy replaced'
ssh -i "$KEY" "$VM" 'sudo docker image prune -f; df -h / | tail -1'

# The backend needs a few seconds to open the port (host start, EF migrations, bot login),
# so a single immediate probe reports 502 on a deploy that went perfectly well.
say 'Verifying'
code=""
for _ in $(seq 1 30); do
  code=$(curl -s -o /dev/null -w '%{http_code}' --max-time 10 "$HEALTH" || true)
  [ "$code" = "200" ] && break
  sleep 3
done
if [ "$code" = "200" ]; then
  echo "OK: $HEALTH -> 200"
else
  echo "FAILED: $HEALTH -> ${code:-no answer} after 90s" >&2
  ssh -i "$KEY" "$VM" "cd $REMOTE_DIR && sudo docker compose logs --tail 40 backend" >&2
  exit 1
fi
