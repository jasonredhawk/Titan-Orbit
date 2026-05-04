#!/usr/bin/env bash
# Run this IN Google Cloud Shell (or any machine with gcloud configured).
#
# IMPORTANT: Do NOT run "sudo systemctl ..." alone in Cloud Shell — that talks to Cloud Shell's
# container (no your-VM systemd). This script uses "gcloud compute ssh ... --command" so the
# restart runs ON your Compute Engine VM.
#
# Usage:
#   bash cloudshell_restart_titanorbit_server.sh
# Optional overrides:
#   PROJECT_ID=my-proj ZONE=us-central1-f INSTANCE_SSH=jason@my-vm bash cloudshell_restart_titanorbit_server.sh

set -euo pipefail

PROJECT_ID="${PROJECT_ID:-titan-orbit}"
ZONE="${ZONE:-us-central1-f}"
INSTANCE_SSH="${INSTANCE_SSH:-jason@titanorbitcp}"

gcloud config set project "${PROJECT_ID}"

echo "Restarting titanorbit-server on VM (${INSTANCE_SSH}, zone ${ZONE}) via SSH..."
# Match restart_server_remote.ps1: Windows tar / umask can leave ELFs non-executable → systemd 203/EXEC.
gcloud compute ssh "${INSTANCE_SSH}" \
  --project="${PROJECT_ID}" \
  --zone="${ZONE}" \
  --tunnel-through-iap \
  --strict-host-key-checking=no \
  --quiet \
  --command "$(cat <<'REMOTE'
set -euo pipefail
BASE=/home/jason/titanorbit-server/TitanOrbitLinux1
shopt -s nullglob
for f in "$BASE"/*.x86_64; do
  if [ -f "$f" ]; then chmod 755 "$f" || true; fi
done
if [ -f "$BASE/TitanOrbitServer" ]; then
  chmod 755 "$BASE/TitanOrbitServer" || true
fi
chmod a+r "$BASE/GameAssembly.so" "$BASE/UnityPlayer.so" 2>/dev/null || true
chmod -R a+rX "$BASE" 2>/dev/null || true
UNIT=/etc/systemd/system/titanorbit-server.service
EXE=""
if [ -f "$BASE/TitanOrbitServer.x86_64" ]; then
  EXE=TitanOrbitServer.x86_64
elif [ -f "$BASE/TitanOrbitServer" ]; then
  EXE=TitanOrbitServer
else
  EXE=TitanOrbitServer.x86_64
fi
if [ -f "$UNIT" ] && [ -n "$EXE" ]; then
  sudo sed -i -E "s|(ExecStart=/home/jason/titanorbit-server/TitanOrbitLinux1/)[^[:space:]]+([[:space:]])|\1${EXE}\2|" "$UNIT" || true
  sudo systemctl daemon-reload || true
fi
sudo systemctl restart titanorbit-server
sudo systemctl is-active titanorbit-server
sudo systemctl status titanorbit-server --no-pager -l | head -n 30
REMOTE
)"

echo "Done."
