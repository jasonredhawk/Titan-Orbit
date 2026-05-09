#!/usr/bin/env bash
# Free disk space on a GCE (or other) Linux VM before uploading a new Titan Orbit headless build.
# Run over SSH from Cloud Shell or your PC, e.g.:
#   bash vm_free_disk_for_server_upload.sh
#   TITANORBIT_SERVER_DIR=/home/jason_redhawk/titanorbit-server/TitanOrbitLinux1 bash vm_free_disk_for_server_upload.sh
#
# Optional: pass --aggressive for journal vacuum + larger apt cleanup (still avoids snap removal).

set -euo pipefail

AGGRESSIVE=false
for a in "$@"; do
  if [[ "$a" == "--aggressive" ]]; then AGGRESSIVE=true; fi
done

INSTALL_ROOT="${TITANORBIT_SERVER_DIR:-$HOME/titanorbit-server/TitanOrbitLinux1}"

echo "== Disk before =="
df -h / /home 2>/dev/null || df -h /

echo ""
echo "== Removing staged upload tarballs (~/.titanorbit-upload-*.tar.gz) =="
find "$HOME" -maxdepth 1 -name '.titanorbit-upload-*.tar.gz' -type f -print -delete 2>/dev/null || true

echo ""
echo "== Removing Unity IL2CPP / Burst debug trees under install (if present) =="
# These are huge and not needed on the VM; safe to delete even if upload tarball excluded them.
if [[ -d "$INSTALL_ROOT/TitanOrbitServer_BackUpThisFolder_ButDontShipItWithYourGame" ]]; then
  rm -rfv "$INSTALL_ROOT/TitanOrbitServer_BackUpThisFolder_ButDontShipItWithYourGame"
fi
if [[ -d "$INSTALL_ROOT/Titan Orbit_BurstDebugInformation_DoNotShip" ]]; then
  rm -rfv "$INSTALL_ROOT/Titan Orbit_BurstDebugInformation_DoNotShip"
fi

echo ""
echo "== APT package cache cleanup (needs sudo) =="
if command -v apt-get >/dev/null 2>&1; then
  sudo apt-get clean
  sudo apt-get autoclean -y || true
  sudo DEBIAN_FRONTEND=noninteractive apt-get autoremove -y || true
else
  echo "(no apt-get; skipping)"
fi

if $AGGRESSIVE; then
  echo ""
  echo "== Aggressive: journal vacuum (keep ~200M) =="
  if command -v journalctl >/dev/null 2>&1; then
    sudo journalctl --vacuum-size=200M || true
  fi
fi

echo ""
echo "== Disk after =="
df -h / /home 2>/dev/null || df -h /

echo ""
echo "Done. Re-upload your headless build, then: sudo systemctl restart titanorbit-server"
