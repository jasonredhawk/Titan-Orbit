#!/usr/bin/env bash
# Run on titanorbitcp (serial console or browser SSH). Diagnose IL2CPP + systemd, then start if safe.
set -u
BASE="${1:-/home/jason/titanorbit-server/TitanOrbitLinux1}"
META="$BASE/TitanOrbitServer_Data/il2cpp_data/Metadata/global-metadata.dat"

echo "=== titanorbit-server unit ==="
systemctl is-enabled titanorbit-server 2>/dev/null || echo "is-enabled: (no unit?)"
systemctl is-active titanorbit-server 2>/dev/null || true
if systemctl is-masked titanorbit-server 2>/dev/null | grep -q yes; then
  echo "MASKED — server will not start on boot. Run:"
  echo "  sudo systemctl unmask titanorbit-server"
  echo "  sudo systemctl enable --now titanorbit-server"
fi
systemctl status titanorbit-server --no-pager -l 2>/dev/null | sed -n '1,20p' || echo "(no titanorbit-server.service installed)"

echo ""
echo "=== IL2CPP file sizes ==="
for f in "$BASE/GameAssembly.so" "$BASE/UnityPlayer.so" "$META" "$BASE/TitanOrbitServer" "$BASE/TitanOrbitServer.x86_64"; do
  if [ -f "$f" ]; then
    stat -c '%n %s bytes' "$f"
  else
    echo "MISSING $f"
  fi
done

SZ=$(stat -c%s "$META" 2>/dev/null || echo 0)
if [ "$SZ" -lt 1000000 ]; then
  echo ""
  echo "FATAL: global-metadata.dat is ${SZ} bytes (need >= 1000000)."
  echo "Player.log will show 'Failed to initialize IL2CPP'. Fix on Windows PC:"
  echo "  1. Quit Unity. 2. tools\\gce\\upload_linux_build_to_gce.bat  (or patch_global_metadata_to_gce.bat)"
  exit 1
fi

echo ""
echo "=== Starting server ==="
sudo systemctl unmask titanorbit-server 2>/dev/null || true
sudo systemctl enable titanorbit-server 2>/dev/null || true
sudo systemctl restart titanorbit-server
sleep 2
systemctl is-active titanorbit-server || true
echo ""
echo "=== journal (last 30 lines) ==="
journalctl -u titanorbit-server -n 30 --no-pager 2>/dev/null || true
echo ""
echo "=== Player.log tail ==="
tail -n 40 "$BASE/Player.log" 2>/dev/null || echo "(no Player.log yet)"
