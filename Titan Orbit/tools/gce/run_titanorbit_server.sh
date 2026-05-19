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
exec "$EXE" "$@" -logFile /dev/stdout
