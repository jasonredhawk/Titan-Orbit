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
#   PROJECT_ID=my-proj ZONE=us-central1-a INSTANCE_SSH=jason@my-vm bash cloudshell_restart_titanorbit_server.sh

set -euo pipefail

PROJECT_ID="${PROJECT_ID:-titan-orbit}"
ZONE="${ZONE:-us-central1-a}"
INSTANCE_SSH="${INSTANCE_SSH:-jason@titan-orbit-compute-engine}"

gcloud config set project "${PROJECT_ID}"

echo "Restarting titanorbit-server on VM (${INSTANCE_SSH}, zone ${ZONE}) via SSH..."
gcloud compute ssh "${INSTANCE_SSH}" \
  --project="${PROJECT_ID}" \
  --zone="${ZONE}" \
  --tunnel-through-iap \
  --strict-host-key-checking=no \
  --quiet \
  --command='sudo systemctl restart titanorbit-server && sudo systemctl is-active titanorbit-server && sudo systemctl status titanorbit-server --no-pager -l | head -n 30'

echo "Done."
