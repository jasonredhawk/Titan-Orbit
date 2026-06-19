#!/usr/bin/env bash
# Run on the GCE VM (browser SSH or jason_redhawk@titanorbitcp). Collects evidence for status=1 / IL2CPP crashes.
set -u
BASE="${1:-/home/jason/titanorbit-server/TitanOrbitLinux1}"

echo "=== systemd unit (first 35 lines) ==="
systemctl cat titanorbit-server 2>/dev/null | sed -n '1,35p' || echo "(no titanorbit-server unit)"

echo ""
echo "=== service status ==="
systemctl status titanorbit-server --no-pager -l 2>/dev/null | sed -n '1,25p' || true

echo ""
echo "=== install dir ==="
ls -la "$BASE" 2>/dev/null | sed -n '1,30p' || { echo "MISSING: $BASE"; exit 1; }

echo ""
echo "=== IL2CPP / player file sizes ==="
for f in \
  "$BASE/GameAssembly.so" \
  "$BASE/UnityPlayer.so" \
  "$BASE/TitanOrbitServer_Data/il2cpp_data/Metadata/global-metadata.dat" \
  "$BASE/TitanOrbitServer" \
  "$BASE/TitanOrbitServer.x86_64" \
  "$BASE/run_titanorbit_server.sh"
do
  if [ -f "$f" ]; then
    stat -c '%a %U:%G %n %s bytes' "$f"
  else
    echo "MISSING $f"
  fi
done

echo ""
echo "=== disk space ==="
df -h "$BASE" / /home 2>/dev/null || df -h

echo ""
echo "=== Player.log (last 80 lines) ==="
if [ -f "$BASE/Player.log" ]; then
  tail -n 80 "$BASE/Player.log"
else
  echo "(no Player.log — Unity may not have written it yet)"
fi

echo ""
echo "=== TitanOrbitDedicatedServer.log (last 40 lines) ==="
if [ -f "$BASE/TitanOrbitDedicatedServer.log" ]; then
  tail -n 40 "$BASE/TitanOrbitDedicatedServer.log"
else
  echo "(no dedicated log yet — managed bootstrap did not run)"
fi

echo ""
echo "=== ldd GameAssembly.so (missing libs) ==="
if [ -f "$BASE/GameAssembly.so" ] && command -v ldd >/dev/null; then
  ldd "$BASE/GameAssembly.so" 2>&1 | grep -F 'not found' || echo "(no 'not found' lines from ldd)"
fi

echo ""
echo "=== journalctl (last 40, titanorbit-server) ==="
journalctl -u titanorbit-server -n 40 --no-pager 2>/dev/null || true

echo ""
echo "Done. If global-metadata.dat or GameAssembly.so is tiny, or Player.log says Failed to initialize IL2CPP, redeploy the Linux build (quit Unity before tar)."
