#!/usr/bin/env bash
# Redeploy the fleet backend to the Tashkent VM, in one command, from this Mac.
#
# The VM has no .NET SDK and too little RAM to build the image, so the image is built
# here for linux/amd64 and shipped over ssh (docker save | load). See deploy/DEPLOY.md.
#
#   ./deploy/redeploy-vm.sh            # publish, build, ship, restart, verify
#   SKIP_PUBLISH=1 ./deploy/redeploy-vm.sh   # reuse ./publish-backend as it is
set -euo pipefail

VM="${VM:-root@157.22.133.185}"
KEY="${KEY:-$HOME/.ssh/servercore_smm}"
IMAGE=kidcontrol-backend:amd64
REMOTE_DIR=/opt/kidcontrol/deploy
HEALTH="${HEALTH:-https://kidcontrol.oresh.in/health/db}"
cd "$(dirname "$0")/.."

say() { printf '\n==> %s\n' "$1"; }

if [ -z "${SKIP_PUBLISH:-}" ]; then
  say 'Publishing the backend (framework-dependent, portable IL)'
  dotnet publish src/KidControl.Backend -c Release -o ./publish-backend
fi

say "Building $IMAGE for linux/amd64"
docker buildx build --platform linux/amd64 -f deploy/Dockerfile.runtime -t "$IMAGE" --load .

say 'Shipping the image to the VM'
docker save "$IMAGE" | gzip | ssh -i "$KEY" "$VM" 'gunzip | docker load'

say 'Restarting the backend'
ssh -i "$KEY" "$VM" "cd $REMOTE_DIR && docker compose up -d backend"

# Every 'docker load' installs a new image and leaves the previous one untagged.
# Nothing collects those: on 5 Sep 2026 eighteen of them (~365 MB each) had piled up
# and the 30 GB disk was 92% full. Prune right here, while the context is obvious --
# a weekly cron on the VM only catches what this step misses.
say 'Removing the image this deploy replaced'
ssh -i "$KEY" "$VM" 'docker image prune -f; df -h / | tail -1'

say 'Verifying'
code=$(curl -s -o /dev/null -w '%{http_code}' --max-time 20 "$HEALTH" || true)
if [ "$code" = "200" ]; then
  echo "OK: $HEALTH -> 200"
else
  echo "FAILED: $HEALTH -> ${code:-no answer}" >&2
  ssh -i "$KEY" "$VM" "cd $REMOTE_DIR && docker compose logs --tail 40 backend" >&2
  exit 1
fi
