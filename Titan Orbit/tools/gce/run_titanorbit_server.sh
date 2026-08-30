#!/usr/bin/env bash
# systemd wrapper: log IL2CPP sizes, run player with Unity log on stdout (journal / serial console).
set -u
# Dedicated Server (UNITY_SERVER) has no graphics module. SDL dummy + Unity -nographics
# still created NullGfxDevice and PresentAndWait (~850 ms/frame, wallSim≈5 Hz, 100% of
# one core on an empty match). Do not set SDL_VIDEODRIVER=dummy.
BASE="${TITANORBIT_SERVER_DIR:-/home/jason/titanorbit-server/TitanOrbitLinux1}"
cd "$BASE" || { echo "FATAL: cannot cd to $BASE" >&2; exit 1; }
for f in GameAssembly.so UnityPlayer.so TitanOrbitServer_Data/il2cpp_data/Metadata/global-metadata.dat; do
  if [ -f "$f" ]; then
    echo "IL2CPP_CHECK $(stat -c '%n %s bytes' "$f")" >&2
  else
    echo "IL2CPP_CHECK MISSING $f" >&2
  fi
done
EXE=./TitanOrbitServer.x86_64
[ -x "$EXE" ] || EXE=./TitanOrbitServer
[ -x "$EXE" ] || { echo "FATAL: no TitanOrbitServer binary in $BASE" >&2; exit 1; }
# Advertise this VM's public IPv4 so clients connect UDP directly (no Unity Relay).
if [ -z "${TITANORBIT_PUBLIC_ADDRESS:-}" ]; then
  META_IP=$(curl -s -m 2 -H "Metadata-Flavor: Google" \
    http://metadata.google.internal/computeMetadata/v1/instance/network-interfaces/0/access-configs/0/external-ip || true)
  if [ -n "${META_IP:-}" ]; then
    export TITANORBIT_PUBLIC_ADDRESS="$META_IP"
  fi
fi
echo "TITANORBIT_START exe=$EXE pid=$$ user=$(id -un) cwd=$(pwd) publicAddress=${TITANORBIT_PUBLIC_ADDRESS:-unset}" >&2
set +e
"$EXE" "$@" -logFile /dev/stdout
rc=$?
set -e
if [ "$rc" -ne 0 ]; then
  echo "FATAL: TitanOrbitServer exited with code $rc (check journal above for IL2CPP / Player.log / TitanOrbitDedicatedServer.log)" >&2
  if command -v ldd >/dev/null 2>&1; then
    echo "=== ldd GameAssembly.so (missing system libs) ===" >&2
    ldd ./GameAssembly.so 2>&1 | grep -F 'not found' >&2 || true
  fi
  if [ -f TitanOrbitDedicatedServer.log ]; then
    echo "=== TitanOrbitDedicatedServer.log (last 15 lines) ===" >&2
    tail -n 15 TitanOrbitDedicatedServer.log >&2 || true
  fi
fi
exit "$rc"
