#!/usr/bin/env bash
# Run in Google Cloud Shell (or any machine with gcloud). Same intent as add_iap_ssh_firewall_and_tag.ps1:
# IAP TCP forwarding (35.235.240.0/20) -> tcp:22 on instances tagged allow-iap-ssh, on the VM's actual VPC.
# Fixes IAP error 4003 "failed to connect to port 22" when the rule was only on "default" but the VM uses another network.
#
# Usage:
#   bash add_iap_ssh_firewall_and_tag_cloudshell.sh
#   bash add_iap_ssh_firewall_and_tag_cloudshell.sh MY_PROJECT us-central1-f MY_INSTANCE
#
# If 4003 persists after tag + iap-allow-ssh-<vpc> (e.g. tag mismatch, org policy quirks), also create a
# VPC-wide IAP rule (no target tags — still only from 35.235.240.0/20):
#   TITANORBIT_IAP_SSH_ALL_VMS=1 bash add_iap_ssh_firewall_and_tag_cloudshell.sh
#
# If a higher-priority DENY hits tcp:22 before your ALLOW (see diagnose_iap_ssh_cloudshell.sh), force IAP ALLOW at priority 0:
#   TITANORBIT_IAP_SSH_PRIORITY0=1 bash add_iap_ssh_firewall_and_tag_cloudshell.sh
# With both: TITANORBIT_IAP_SSH_PRIORITY0=1 TITANORBIT_IAP_SSH_ALL_VMS=1 bash add_iap_ssh_firewall_and_tag_cloudshell.sh

set -euo pipefail

PROJECT_ID="${1:-titan-orbit}"
ZONE="${2:-us-central1-f}"
INSTANCE="${3:-titanorbitcp}"
TAG="${4:-allow-iap-ssh}"
IAP_RANGE="35.235.240.0/20"

echo "Project: $PROJECT_ID  Zone: $ZONE  Instance: $INSTANCE"
echo ""

NETWORK_URL="$(gcloud compute instances describe "$INSTANCE" \
  --zone="$ZONE" --project="$PROJECT_ID" \
  --format='value(networkInterfaces[0].network)')"
NETWORK="${NETWORK_URL##*/}"
if [[ -z "$NETWORK" ]]; then
  echo "ERROR: Could not read VPC from instance (empty networkInterfaces[0].network)." >&2
  exit 1
fi
echo "VM VPC network: $NETWORK"
echo ""

SANITIZED="$(echo "$NETWORK" | tr '[:upper:]' '[:lower:]' | sed -e 's/[^a-z0-9-]/-/g' -e 's/^-*//' -e 's/-*$//')"
[[ -z "$SANITIZED" ]] && SANITIZED="net"
RULE="iap-allow-ssh-${SANITIZED}"
while [[ ${#RULE} -gt 63 ]]; do
  RULE="${RULE:0:63}"
done
while [[ "$RULE" == *- ]]; do RULE="${RULE%-}"; done

echo "[1/2] Firewall rule: $RULE (tcp:22 from $IAP_RANGE, target tag $TAG)"
if gcloud compute firewall-rules describe "$RULE" --project="$PROJECT_ID" &>/dev/null; then
  EXISTING_URL="$(gcloud compute firewall-rules describe "$RULE" --project="$PROJECT_ID" --format='value(network)')"
  EXISTING="${EXISTING_URL##*/}"
  echo "Rule already exists, attached to VPC network: $EXISTING"
  if [[ "$EXISTING" != "$NETWORK" ]]; then
    echo "WARNING: Rule '$RULE' is on network '$EXISTING' but this VM uses '$NETWORK'. Fix in Console or delete the wrong rule, then re-run." >&2
  fi
else
  echo "Creating firewall rule on network '$NETWORK'..."
  gcloud compute firewall-rules create "$RULE" \
    --project="$PROJECT_ID" \
    --network="$NETWORK" \
    --direction=INGRESS \
    --action=ALLOW \
    --rules=tcp:22 \
    --source-ranges="$IAP_RANGE" \
    --target-tags="$TAG" \
    --description="TitanOrbit-IAP-TCP22-from-35.235.240.0-slash-20"
  echo "Created."
fi

echo ""
echo "[2/2] Network tag '$TAG' on instance '$INSTANCE'..."
CURRENT="$(gcloud compute instances describe "$INSTANCE" --zone="$ZONE" --project="$PROJECT_ID" --format='value(tags.items)' 2>/dev/null || true)"
FOUND=0
# gcloud may join tags with ; or , depending on version
CURRENT_NORM="${CURRENT//,/;}"
IFS=';' read -r -a TAG_ARR <<< "${CURRENT_NORM:-}"
for t in "${TAG_ARR[@]}"; do
  t="${t// /}" # trim spaces
  [[ -z "$t" ]] && continue
  if [[ "$t" == "$TAG" ]]; then FOUND=1; break; fi
done
if [[ "$FOUND" -eq 1 ]]; then
  echo "Tag already present. Tags: ${CURRENT:-<none>}"
else
  echo "Adding tag (existing tags are kept)..."
  gcloud compute instances add-tags "$INSTANCE" --zone="$ZONE" --project="$PROJECT_ID" --tags="$TAG"
  echo "Added tag '$TAG'."
fi

echo ""
if [[ "${TITANORBIT_IAP_SSH_ALL_VMS:-}" == "1" ]]; then
  RULE_ALL="iap-allow-ssh-allvms-${SANITIZED}"
  while [[ ${#RULE_ALL} -gt 63 ]]; do RULE_ALL="${RULE_ALL:0:63}"; done
  while [[ "$RULE_ALL" == *- ]]; do RULE_ALL="${RULE_ALL%-}"; done
  echo "[3/3] VPC-wide IAP rule (no target tags): $RULE_ALL"
  if gcloud compute firewall-rules describe "$RULE_ALL" --project="$PROJECT_ID" &>/dev/null; then
    echo "Rule already exists: $RULE_ALL"
  else
    echo "Creating (applies to ALL VMs on VPC $NETWORK; source remains $IAP_RANGE only)..."
    gcloud compute firewall-rules create "$RULE_ALL" \
      --project="$PROJECT_ID" \
      --network="$NETWORK" \
      --direction=INGRESS \
      --action=ALLOW \
      --rules=tcp:22 \
      --source-ranges="$IAP_RANGE" \
      --description="TitanOrbit-IAP-TCP22-all-instances-on-vpc-no-target-tags"
    echo "Created."
  fi
  echo ""
fi

if [[ "${TITANORBIT_IAP_SSH_PRIORITY0:-}" == "1" ]]; then
  if [[ "${TITANORBIT_IAP_SSH_ALL_VMS:-}" == "1" ]]; then
    RULE_P0="iap-allow-ssh-allvms-${SANITIZED}-p0"
  else
    RULE_P0="iap-allow-ssh-${SANITIZED}-prio0"
  fi
  while [[ ${#RULE_P0} -gt 63 ]]; do RULE_P0="${RULE_P0:0:63}"; done
  while [[ "$RULE_P0" == *- ]]; do RULE_P0="${RULE_P0%-}"; done
  echo "[IAP priority 0] Firewall rule: $RULE_P0 (tcp:22 from $IAP_RANGE)"
  if gcloud compute firewall-rules describe "$RULE_P0" --project="$PROJECT_ID" &>/dev/null; then
    echo "Rule already exists: $RULE_P0 (check: gcloud compute firewall-rules describe $RULE_P0 --format=yaml(priority))"
  else
    echo "Creating with --priority=0 (evaluated before default ~1000 rules)..."
    if [[ "${TITANORBIT_IAP_SSH_ALL_VMS:-}" == "1" ]]; then
      gcloud compute firewall-rules create "$RULE_P0" \
        --project="$PROJECT_ID" \
        --network="$NETWORK" \
        --direction=INGRESS \
        --action=ALLOW \
        --rules=tcp:22 \
        --source-ranges="$IAP_RANGE" \
        --priority=0 \
        --description="TitanOrbit-IAP-TCP22-priority-0-all-vms"
    else
      gcloud compute firewall-rules create "$RULE_P0" \
        --project="$PROJECT_ID" \
        --network="$NETWORK" \
        --direction=INGRESS \
        --action=ALLOW \
        --rules=tcp:22 \
        --source-ranges="$IAP_RANGE" \
        --target-tags="$TAG" \
        --priority=0 \
        --description="TitanOrbit-IAP-TCP22-priority-0-tagged"
    fi
    echo "Created."
  fi
  echo ""
fi

echo "Done. Wait about 60 seconds, then:"
echo "  - Browser: VM -> SSH (IAP), or"
echo "  - Cloud Shell: bash cloudshell_install_titanorbit_unit.sh"
echo "If 4003 persists: bash diagnose_iap_ssh_cloudshell.sh"
echo "  Shared VPC: create IAP tcp:22 allow in the HOST project on the shared network."
echo "  Or try: TITANORBIT_IAP_SSH_ALL_VMS=1 bash add_iap_ssh_firewall_and_tag_cloudshell.sh \"$PROJECT_ID\" \"$ZONE\" \"$INSTANCE\""
echo "  Or try: TITANORBIT_IAP_SSH_PRIORITY0=1 bash add_iap_ssh_firewall_and_tag_cloudshell.sh \"$PROJECT_ID\" \"$ZONE\" \"$INSTANCE\""
