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

# Dedicated Server (UNITY_SERVER) has no graphics module. Do NOT set
# SDL_VIDEODRIVER=dummy and do NOT pass -nographics — those force NullGfxDevice
# PresentAndWait (~300 ms/frame, wallSim≈11 Hz, ships snap back). Same lesson as
# tools/gce/run_titanorbit_server.sh. Unset dummy if the image inherited it.
unset SDL_VIDEODRIVER || true
export DISPLAY="${DISPLAY:-}"

# Edgegap injects ARBITRIUM_PORT_GAMEPORT_INTERNAL when the app version port is named "gameport".
PORT="${ARBITRIUM_PORT_GAMEPORT_INTERNAL:-7777}"

# Plugin "Test locally" sets ARBITRIUM_ENV_DEBUG + a dummy public IP. Clients on this PC
# must UDP-connect to the published Docker port (default 7777), not 162.254.141.66:31504.
if [ "${ARBITRIUM_ENV_DEBUG:-}" = "true" ] || [ "${ARBITRIUM_ENV_DEBUG:-}" = "1" ]; then
  export TITANORBIT_PUBLIC_ADDRESS="${TITANORBIT_PUBLIC_ADDRESS:-127.0.0.1}"
fi

echo "TITANORBIT_EDGEGAP exe=$EXE port=$PORT deployment=${ARBITRIUM_REQUEST_ID:-local}" >&2

exec "$EXE" -batchmode -logFile /dev/stdout \
  --maxPlayers="${TITANORBIT_MAX_PLAYERS:-60}" \
  --serverPort="$PORT" \
  --serverListenAddress=0.0.0.0 \
  --isLatest="${TITANORBIT_IS_LATEST:-1}" \
  --emptyMatchRecreateSeconds="${TITANORBIT_EMPTY_MATCH_RECREATE_SECONDS:-1800}" \
  --ageThresholdSeconds="${TITANORBIT_AGE_THRESHOLD_SECONDS:-900}" \
  --softFillMinPlayers="${TITANORBIT_SOFT_FILL_MIN_PLAYERS:-8}" \
  --maxConcurrentGames="${TITANORBIT_MAX_CONCURRENT_GAMES:-5}" \
  --serverExecutablePath="$(readlink -f "$EXE" || echo "$PWD/$EXE")" \
  ${UNITY_COMMANDLINE_ARGS:-}
