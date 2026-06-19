#!/usr/bin/env bash
# Run in Google Cloud Shell when IAP SSH returns 4003 / failed to connect to port 22.
# Marker (stdout, first real line): if missing, Cloud Shell is not executing this file.
echo "TITANORBIT_IAP_DIAG_FILE=v3 — wrong output? run: pwd && ls -la diagnose_iap_ssh_cloudshell.sh && head -5 diagnose_iap_ssh_cloudshell.sh"
# Prints instance VPC, tags, subnetwork (Shared VPC hint), and firewall rules on that VPC.
#
# Usage:
#   bash diagnose_iap_ssh_cloudshell.sh
#   bash diagnose_iap_ssh_cloudshell.sh MY_PROJECT us-central1-f MY_INSTANCE

set -euo pipefail

sanitize_net() {
  local n="$1"
  n="$(echo "$n" | tr '[:upper:]' '[:lower:]' | sed -e 's/[^a-z0-9-]/-/g' -e 's/^-*//' -e 's/-*$//')"
  [[ -z "$n" ]] && n="net"
  echo "$n"
}

truncate63() {
  local s="$1"
  while [[ ${#s} -gt 63 ]]; do s="${s:0:63}"; done
  while [[ "$s" == *- ]]; do s="${s%-}"; done
  echo "$s"
}

PROJECT_ID="${1:-titan-orbit}"
ZONE="${2:-us-central1-f}"
INSTANCE="${3:-titanorbitcp}"
IAP_RANGE="35.235.240.0/20"
WANT_TAG="allow-iap-ssh"
# Bump when sections change — Cloud Shell must use this file from the repo, not an old upload.
DIAG_VERSION="3"

echo "========== IAP SSH diagnostics =========="
echo "diagnose_iap_ssh_cloudshell.sh v${DIAG_VERSION}"
echo "Full report includes, in order: Tags -> Expected IAP rule -> Effective firewalls -> Metadata -> Network firewall policies -> VPC firewall-rules list."
echo "If you jump straight from Tags to VPC rules, replace this script from Titan Orbit tools/gce/ (stale copy in Cloud Shell)."
echo "Project: $PROJECT_ID  Zone: $ZONE  Instance: $INSTANCE"
echo ""

if ! gcloud compute instances describe "$INSTANCE" --zone="$ZONE" --project="$PROJECT_ID" &>/dev/null; then
  echo "ERROR: Instance not found or no access. Check name, zone, project, and gcloud auth." >&2
  exit 1
fi

STATUS="$(gcloud compute instances describe "$INSTANCE" --zone="$ZONE" --project="$PROJECT_ID" --format='value(status)')"
echo "Instance status: $STATUS"
if [[ "$STATUS" != "RUNNING" ]]; then
  echo "WARNING: Instance is not RUNNING. Start the VM before IAP SSH." >&2
fi
echo ""

NETWORK_URL="$(gcloud compute instances describe "$INSTANCE" --zone="$ZONE" --project="$PROJECT_ID" --format='value(networkInterfaces[0].network)')"
NETWORK="${NETWORK_URL##*/}"
SUB_URL="$(gcloud compute instances describe "$INSTANCE" --zone="$ZONE" --project="$PROJECT_ID" --format='value(networkInterfaces[0].subnetwork)' || true)"
SUB_PROJECT=""
if [[ -n "$SUB_URL" ]]; then
  if [[ "$SUB_URL" =~ /projects/([^/]+)/ ]]; then
    SUB_PROJECT="${BASH_REMATCH[1]}"
  fi
fi

echo "NIC[0] VPC network: $NETWORK"
echo "NIC[0] subnetwork URL: ${SUB_URL:-<none>}"
if [[ -n "$SUB_PROJECT" && "$SUB_PROJECT" != "$PROJECT_ID" ]]; then
  echo ""
  echo "*** Shared VPC / host project? *** Subnetwork lives in project: $SUB_PROJECT"
  echo "    Ingress firewall for this VM may be evaluated in the HOST project ($SUB_PROJECT)."
  echo "    Create the IAP rule (35.235.240.0/20 -> tcp:22) there, on the same VPC network name, or use Console host project."
  echo ""
fi

echo "---------- Tags on this VM ----------"
TAGS_JSON="$(gcloud compute instances describe "$INSTANCE" --zone="$ZONE" --project="$PROJECT_ID" --format='json(tags)' 2>/dev/null || echo '{}')"
if command -v jq &>/dev/null; then
  echo "$TAGS_JSON" | jq -r '.tags.items // [] | if length == 0 then "(no tags — IAP rules that use target-tags will NOT match)" else .[] end'
  HAS_TAG="$(echo "$TAGS_JSON" | jq -r --arg t "$WANT_TAG" '(.tags.items // []) | index($t) != null')"
else
  TAGS_LINE="$(gcloud compute instances describe "$INSTANCE" --zone="$ZONE" --project="$PROJECT_ID" --format='value(tags.items)' 2>/dev/null || true)"
  echo "tags.items (raw): ${TAGS_LINE:-<empty>}"
  HAS_TAG="unknown"
fi

if command -v jq &>/dev/null; then
  if [[ "$HAS_TAG" == "true" ]]; then
    echo "OK: tag '$WANT_TAG' is present."
  else
    echo "MISSING: tag '$WANT_TAG' is NOT on this instance. Run add_iap_ssh_firewall_and_tag_cloudshell.sh"
  fi
fi
echo ""

SAN="$(sanitize_net "$NETWORK")"
RULE_EXPECTED="$(truncate63 "iap-allow-ssh-${SAN}")"
echo "---------- Expected TitanOrbit IAP rule: $RULE_EXPECTED ----------"
if gcloud compute firewall-rules describe "$RULE_EXPECTED" --project="$PROJECT_ID" &>/dev/null; then
  gcloud compute firewall-rules describe "$RULE_EXPECTED" --project="$PROJECT_ID" \
    --format='yaml(name,disabled,network,direction,priority,sourceRanges,targetTags,allowed,denied,description)'
else
  echo "No rule named $RULE_EXPECTED in this project. Run add_iap_ssh_firewall_and_tag_cloudshell.sh"
fi
echo ""

echo "---------- Effective firewalls on VPC \"$NETWORK\" (project + hierarchical policies) ----------"
set +e
EFF_OUT="$(gcloud compute networks get-effective-firewalls "$NETWORK" --project="$PROJECT_ID" 2>&1)"
EFF_EC=$?
set -e
if [[ "$EFF_EC" -eq 0 ]]; then
  echo "$EFF_OUT" | head -120
  echo ""
  echo "Scan above for INGRESS + DENY that could match tcp:22 before an ALLOW from $IAP_RANGE."
  echo "If you see org/folder DENY policies, fix them with your org admin — not in this repo."
else
  echo "$EFF_OUT" | head -20
  echo "(get-effective-firewalls exit $EFF_EC — try: gcloud components update)"
fi
echo ""

echo "---------- Instance metadata (SSH / OS Login / serial hints) ----------"
INST_JSON="$(gcloud compute instances describe "$INSTANCE" --zone="$ZONE" --project="$PROJECT_ID" --format=json 2>/dev/null || echo '{}')"
if command -v jq &>/dev/null; then
  echo "$INST_JSON" | jq -r '
    .metadata.items // []
    | map(select(.key | test("ssh|oslogin|serial|block-project|google-logging|startup"; "i")))
    | if length == 0 then "(no matching keys — OS defaults)"
      else (.[] | "\(.key)=\(.value | tostring | .[0:200])")
      end'
else
  echo "(Install jq to print filtered metadata keys.)"
fi
echo ""

echo "---------- Network firewall policies (project — extra DENY can live here) ----------"
REGION="${ZONE%-*}"
set +e
NFP_G="$(gcloud compute network-firewall-policies list --project="$PROJECT_ID" --global --format=json 2>&1)"
NFP_R="$(gcloud compute network-firewall-policies list --project="$PROJECT_ID" --regions="$REGION" --format=json 2>&1)"
set -e
if command -v jq &>/dev/null; then
  if echo "$NFP_G" | jq -e 'type == "array"' &>/dev/null; then
    GCNT="$(echo "$NFP_G" | jq 'length')"
    if [[ "$GCNT" == "0" ]]; then
      echo "Global network firewall policies: (none)"
    else
      echo "Global network firewall policies ($GCNT):"
      echo "$NFP_G" | jq -r '.[] | "- \(.name)"'
    fi
  else
    echo "Global list (non-JSON / error):"
    echo "$NFP_G" | head -8
  fi
  if echo "$NFP_R" | jq -e 'type == "array"' &>/dev/null; then
    RCNT="$(echo "$NFP_R" | jq 'length')"
    if [[ "$RCNT" == "0" ]]; then
      echo "Regional ($REGION) network firewall policies: (none)"
    else
      echo "Regional ($REGION) network firewall policies ($RCNT):"
      echo "$NFP_R" | jq -r '.[] | "- \(.name)"'
    fi
  else
    echo "Regional list (non-JSON / error):"
    echo "$NFP_R" | head -8
  fi
else
  echo "$NFP_G" | head -20
fi
echo "If any policies exist, open Console: Network Security -> Network firewall policies, and check rules + VPC associations."
echo ""

echo "---------- Firewall rules on VPC \"$NETWORK\" ----------"
if command -v jq &>/dev/null; then
  gcloud compute firewall-rules list --project="$PROJECT_ID" --format=json |
    jq -r --arg net "$NETWORK" '
      [.[] | select(.network | test("/" + $net + "$"))]
      | sort_by(.priority // 65534)
      | .[]
      | "name=\(.name) priority=\(.priority) SRC=\(.sourceRanges) TARGET_TAGS=\(.targetTags) ALLOW=\(.allowed) DENY=\(.denied)"
    '
else
  echo "(Install jq for a filtered view.) All rules:"
  gcloud compute firewall-rules list --project="$PROJECT_ID" --format='table(name,network,priority,sourceRanges,targetTags,allowed,denied)'
fi

echo ""
echo "Look for: SRC containing $IAP_RANGE and ALLOW including tcp:22."
echo "If the rule uses TARGET_TAGS, this VM must include one of those tags."
echo "If project VPC rules + tag look correct but 4003 remains, the block is usually:"
echo "  A) A DENY in **Effective firewalls** (org/folder policy or network firewall policy) — see section above."
echo "  B) **Guest OS**: sshd stopped, listening only on 127.0.0.1, or ufw/iptables DROP on 22."
echo "  C) **OS Login / metadata** oddities — see metadata section; compare with Google OS Login docs."
echo ""
echo "Try in order:"
echo "  1) TITANORBIT_IAP_SSH_PRIORITY0=1 bash add_iap_ssh_firewall_and_tag_cloudshell.sh"
echo "  2) TITANORBIT_IAP_SSH_ALL_VMS=1 bash add_iap_ssh_firewall_and_tag_cloudshell.sh"
echo "  3) Enable serial: VM -> EDIT -> check \"Enable connecting to serial ports\" -> Save, then:"
echo "       gcloud compute connect-to-serial-port $INSTANCE --project=$PROJECT_ID --zone=$ZONE"
echo "     On the serial shell: sudo ss -tlnp | grep :22   and   sudo systemctl status ssh sshd"
echo ""
echo "---------- Suggested troubleshoot command ----------"
echo "gcloud compute ssh $INSTANCE --project=$PROJECT_ID --zone=$ZONE --troubleshoot --tunnel-through-iap"
echo "=========================================="
echo "End v${DIAG_VERSION}. Missing sections above? Re-upload tools/gce/diagnose_iap_ssh_cloudshell.sh from the repo."
