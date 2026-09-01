#!/usr/bin/env bash
# Titan Orbit — Edgegap container entrypoint.
# Starts the IL2CPP Linux dedicated server with the same CLI defaults as tools/gce/run_titanorbit_server.sh.
set -euo pipefail

cd /root/build

# Unity Dedicated Server output is usually ServerBuild.x86_64 (Edgegap plugin default name).
EXE="./ServerBuild.x86_64"
if [ ! -x "$EXE" ]; then
  EXE="./ServerBuild"
fi
if [ ! -x "$EXE" ]; then
  EXE="$(ls -1 ./*.x86_64 2>/dev/null | head -n1 || true)"
fi
if [ -z "${EXE:-}" ] || [ ! -x "$EXE" ]; then
  echo "FATAL: no executable in /root/build" >&2
  ls -la >&2
  exit 1
fi

# Headless Linux: avoid SDL/GPU init failures inside Docker.
export SDL_VIDEODRIVER="${SDL_VIDEODRIVER:-dummy}"
export DISPLAY="${DISPLAY:-}"

# Edgegap injects ARBITRIUM_PORT_GAMEPORT_INTERNAL when the app version port is named "gameport".
PORT="${ARBITRIUM_PORT_GAMEPORT_INTERNAL:-7777}"

echo "TITANORBIT_EDGEGAP exe=$EXE port=$PORT deployment=${ARBITRIUM_REQUEST_ID:-local}" >&2

exec "$EXE" -batchmode -nographics -logFile /dev/stdout \
  --maxPlayers="${TITANORBIT_MAX_PLAYERS:-60}" \
  --serverPort="$PORT" \
  --relayProtocol="${TITANORBIT_RELAY_PROTOCOL:-dtls}" \
  --serverListenAddress=0.0.0.0 \
  --isLatest="${TITANORBIT_IS_LATEST:-1}" \
  --serverExecutablePath="$(readlink -f "$EXE" || echo "$PWD/$EXE")" \
  ${UNITY_COMMANDLINE_ARGS:-}
