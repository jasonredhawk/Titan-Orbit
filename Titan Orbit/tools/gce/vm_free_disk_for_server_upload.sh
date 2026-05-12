#!/usr/bin/env bash
# Free disk space on a GCE (or other) Linux VM before uploading a new Titan Orbit headless build.
# Windows: tools\gce\vm_free_disk_for_server_upload_gce.bat (or .ps1) [useIap] [aggressive] — no gcloud scp/pscp
# On the VM (browser SSH / Cloud Shell), e.g.:
#   bash vm_free_disk_for_server_upload.sh
#   TITANORBIT_SERVER_DIR=/home/jason_redhawk/titanorbit-server/TitanOrbitLinux1 bash vm_free_disk_for_server_upload.sh
#
# Optional:
#   --aggressive   journal smaller cap, apt lists cache, optional docker prune, /var/crash
#
# Staged OpenSSH uploads land in /tmp (see upload_linux_build_to_gce_openssh.ps1); partial files
# must be removed or the next scp can fail with "write remote ... Failure".
# Deploy archives exclude TitanOrbitServer_BackUpThisFolder_* and Titan Orbit_BurstDebugInformation_*;
# if an old tarball on the VM still contained them, rm the trees here and redeploy.

set -euo pipefail

AGGRESSIVE=false
for a in "$@"; do
  if [[ "$a" == "--aggressive" ]]; then AGGRESSIVE=true; fi
done

INSTALL_ROOT="${TITANORBIT_SERVER_DIR:-$HOME/titanorbit-server/TitanOrbitLinux1}"

echo "== Disk before =="
df -h / /home /tmp 2>/dev/null || df -h /

echo ""
echo "== Removing staged SCP / upload tarballs in /tmp (Titan Orbit) =="
# Partial or completed uploads from tools/gce upload scripts and README Cloud Shell examples.
sudo bash -c 'shopt -s nullglob
paths=(/tmp/TitanOrbitLinux*.tar.gz /tmp/titan-latest.tgz /tmp/titan-*.tgz /tmp/titan-*.tar.gz)
if ((${#paths[@]})); then rm -fv "${paths[@]}"; else echo "(no matching tarballs in /tmp)"; fi'

echo ""
echo "== Removing staged upload tarballs in home (~/.titanorbit-upload-*.tar.gz) =="
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
  echo "== Aggressive: remove apt lists cache (regenerated on next apt update) =="
  if command -v apt-get >/dev/null 2>&1; then
    sudo rm -rf /var/lib/apt/lists/* /var/lib/apt/lists/partial/* 2>/dev/null || true
  fi
fi

echo ""
echo "== Journal vacuum (needs sudo) =="
if command -v journalctl >/dev/null 2>&1; then
  if $AGGRESSIVE; then
    sudo journalctl --vacuum-size=50M || true
  else
    sudo journalctl --vacuum-size=150M || true
  fi
else
  echo "(no journalctl; skipping)"
fi

if $AGGRESSIVE; then
  echo ""
  echo "== Aggressive: crash dumps under /var/crash =="
  if [[ -d /var/crash ]]; then
    sudo bash -c 'shopt -s nullglob; c=(/var/crash/*); ((${#c[@]})) && rm -rfv "${c[@]}"'
  fi

  echo ""
  echo "== Aggressive: Docker unused data (prune; not a full image wipe) =="
  if command -v docker >/dev/null 2>&1; then
    sudo docker system prune -f 2>/dev/null || docker system prune -f 2>/dev/null || true
  else
    echo "(no docker; skipping)"
  fi
fi

echo ""
echo "== Optional user caches (pip) =="
if [[ -d "$HOME/.cache/pip" ]]; then
  rm -rfv "$HOME/.cache/pip"
fi

echo ""
echo "== Disk after =="
df -h / /home /tmp 2>/dev/null || df -h /

echo ""
echo "Done. Re-upload your headless build, then: sudo systemctl restart titanorbit-server"
