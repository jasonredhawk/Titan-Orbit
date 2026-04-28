#!/bin/bash
# One-time GCE metadata "startup-script" when serial is log-spammed and metadata/SSH are broken.
# Adds default route via typical GCE gateway for 10.128.0.0/20 (10.128.0.1). Adjust if your subnet differs.
#
# DO NOT run:  bash guest_network_recovery_startup.sh   in Cloud Shell — that fixes nothing (wrong machine).
# DO run on your PC (paths adjusted):
#   gcloud compute instances add-metadata titan-orbit-compute-engine --project=titan-orbit --zone=us-central1-a --metadata-from-file=startup-script=guest_network_recovery_startup.sh
#   gcloud compute instances reset titan-orbit-compute-engine --project=titan-orbit --zone=us-central1-a
# After recovery: remove the startup-script metadata key so it does not run every boot.
#
# Logs (on the VM only): /var/log/titanorbit-network-recovery.log

# Abort if someone runs this script interactively on Cloud Shell (or any host whose metadata name is not your VM).
_INAME="$(curl -fsS -H "Metadata-Flavor: Google" --max-time 3 "http://169.254.169.254/computeMetadata/v1/instance/name" 2>/dev/null || true)"
if echo "${_INAME}" | grep -qi cloudshell; then
  echo "ERROR: This script is running on Cloud Shell (metadata instance name contains cloudshell)." >&2
  echo "It must be installed as startup-script on Compute Engine VM titan-orbit-compute-engine, then the VM reset — not executed with bash here." >&2
  exit 1
fi

exec >>/var/log/titanorbit-network-recovery.log 2>&1
set -x
date
sleep 10

GW="${TITANORBIT_GCE_GATEWAY:-10.128.0.1}"

for IF in ens4 enp0s3 eth0; do
  if ip link show "$IF" 2>/dev/null | grep -q "state UP"; then
    echo "Trying default route via $GW dev $IF"
    ip route replace default via "$GW" dev "$IF" || true
    dhclient -1 "$IF" 2>/dev/null || true
  fi
done

(systemctl restart systemd-networkd && sleep 3) 2>/dev/null || true
(systemctl restart networking && sleep 3) 2>/dev/null || true
(systemctl restart ssh || systemctl restart sshd) 2>/dev/null || true

echo -n "metadata check: "
curl -s --max-time 8 -H "Metadata-Flavor: Google" "http://169.254.169.254/computeMetadata/v1/instance/name" || echo "FAILED"
echo
date
