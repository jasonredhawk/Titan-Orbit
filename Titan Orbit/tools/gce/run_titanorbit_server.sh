#!/usr/bin/env bash
# systemd wrapper: log IL2CPP sizes, run player with Unity log on stdout (journal / serial console).
set -u
# Headless GCE VMs have no display; avoid SDL/GPU init failures on some Unity Linux players.
export SDL_VIDEODRIVER="${SDL_VIDEODRIVER:-dummy}"
export DISPLAY="${DISPLAY:-}"
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
echo "TITANORBIT_START exe=$EXE pid=$$ user=$(id -un) cwd=$(pwd)" >&2
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
