#!/usr/bin/env bash
# VM cleanup before Titan Orbit deploy. Windows: vm_free_disk_for_server_upload_gce.ps1 [useIap] [aggressive]
# TITANORBIT_SERVER_DIR=/path/to/TitanOrbitLinux1 to override game dir.
# --aggressive: apt lists, journal 32M, /var/crash, docker prune, rm entire game dir, rm ~/.cache

set -euo pipefail

AGGRESSIVE=false
for a in "$@"; do [[ "$a" == "--aggressive" ]] && AGGRESSIVE=true; done

GAME_DIR="${TITANORBIT_SERVER_DIR:-$HOME/titanorbit-server/TitanOrbitLinux1}"
SERVER_PARENT="$(dirname -- "$GAME_DIR")"

echo "== Disk before =="; df -h / /home /tmp 2>/dev/null || df -h /
echo "== du /home/$USER (largest last) =="; du -xhd1 "/home/$USER" 2>/dev/null | sort -h | tail -n 12 || true

echo "== stop titanorbit-server =="
command -v systemctl >/dev/null 2>&1 && { sudo systemctl stop titanorbit-server 2>/dev/null || true; sleep 2; }

echo "== truncate Unity logs under $SERVER_PARENT =="
if [[ -d "$SERVER_PARENT" ]]; then
  find "$SERVER_PARENT" -type f \( -name 'Player.log' -o -name 'TitanOrbitDedicatedServer.log' -o -name 'output.log' \) 2>/dev/null | while read -r f; do
    echo "  $f"; truncate -s 0 "$f" 2>/dev/null || true
  done
  find "$SERVER_PARENT" -path '*/TitanOrbitServer_Data/StreamingAssets/*.log' -type f -delete 2>/dev/null || true
fi

echo "== rm partial/broken install (tiny/missing global-metadata.dat) =="
META="$GAME_DIR/TitanOrbitServer_Data/il2cpp_data/Metadata/global-metadata.dat"
if [[ -d "$GAME_DIR" ]]; then
  sz=0; [[ -f "$META" ]] && sz=$(stat -c%s "$META" 2>/dev/null || echo 0)
  if [[ ! -f "$META" ]] || [[ "$sz" -lt 100000 ]]; then echo "  rm -rf $GAME_DIR"; rm -rf "$GAME_DIR"; fi
fi

echo "== rm IL2CPP/Burst junk dirs if present =="
if [[ -d "$GAME_DIR/TitanOrbitServer_BackUpThisFolder_ButDontShipItWithYourGame" ]]; then rm -rf "$GAME_DIR/TitanOrbitServer_BackUpThisFolder_ButDontShipItWithYourGame"; fi
if [[ -d "$GAME_DIR/Titan Orbit_BurstDebugInformation_DoNotShip" ]]; then rm -rf "$GAME_DIR/Titan Orbit_BurstDebugInformation_DoNotShip"; fi

echo "== /tmp Titan tarballs =="
sudo bash -c 'shopt -s nullglob; p=(/tmp/TitanOrbitLinux*.tar.gz /tmp/titan-latest.tgz /tmp/titan-*.tgz /tmp/titan-*.tar.gz); ((${#p[@]})) && rm -f "${p[@]}"'

echo "== ~/.titanorbit-upload-*.tar.gz =="; find "$HOME" -maxdepth 1 -name '.titanorbit-upload-*.tar.gz' -type f -delete 2>/dev/null || true

echo "== apt clean =="
if command -v apt-get >/dev/null 2>&1; then
  sudo apt-get clean; sudo apt-get autoclean -y || true; sudo DEBIAN_FRONTEND=noninteractive apt-get autoremove -y || true
fi
if $AGGRESSIVE && command -v apt-get >/dev/null 2>&1; then sudo rm -rf /var/lib/apt/lists/* /var/lib/apt/lists/partial/* 2>/dev/null || true; fi

echo "== journalctl vacuum =="
if command -v journalctl >/dev/null 2>&1; then
  if $AGGRESSIVE; then sudo journalctl --vacuum-size=32M || true; else sudo journalctl --vacuum-size=64M || true; fi
fi

if $AGGRESSIVE; then
  echo "== aggressive: /var/crash =="; [[ -d /var/crash ]] && sudo bash -c 'shopt -s nullglob; c=(/var/crash/*); ((${#c[@]})) && rm -rf "${c[@]}"'
  echo "== aggressive: docker prune =="; command -v docker >/dev/null 2>&1 && { sudo docker system prune -f 2>/dev/null || true; }
  echo "== aggressive: rm game $GAME_DIR =="; [[ -d "$GAME_DIR" ]] && rm -rf "$GAME_DIR"
fi

echo "== user cache =="; if $AGGRESSIVE; then [[ -d "$HOME/.cache" ]] && rm -rf "$HOME/.cache"; else [[ -d "$HOME/.cache/pip" ]] && rm -rf "$HOME/.cache/pip"; fi

echo "== Disk after =="; df -h / /home /tmp 2>/dev/null || df -h /
du -xhd1 "/home/$USER" 2>/dev/null | sort -h | tail -n 12 || true
echo "Done. Still full? Resize boot disk. Start server: sudo systemctl start titanorbit-server"
