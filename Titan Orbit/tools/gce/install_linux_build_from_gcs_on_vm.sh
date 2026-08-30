#!/usr/bin/env bash
# Run ON the GCE VM (invoked by install_linux_build_from_gcs_remote.ps1 via gcloud compute ssh).
# Pull latest tarball from GCS using the VM service account (no gsutil required).
#
# Required env (set by the PowerShell wrapper):
#   TITANORBIT_GCS_BUCKET
#   TITANORBIT_GCS_OBJECT   e.g. titanorbit-linux-build/TitanOrbitLinux1-latest.tar.gz
#   TITANORBIT_INSTALL_ROOT e.g. /home/jason/titanorbit-server
#   TITANORBIT_EXTRACT_DIR  folder name inside the tarball (top-level directory name)

set -euo pipefail

: "${TITANORBIT_GCS_BUCKET:?missing TITANORBIT_GCS_BUCKET}"
: "${TITANORBIT_GCS_OBJECT:?missing TITANORBIT_GCS_OBJECT}"
: "${TITANORBIT_INSTALL_ROOT:?missing TITANORBIT_INSTALL_ROOT}"
: "${TITANORBIT_EXTRACT_DIR:?missing TITANORBIT_EXTRACT_DIR}"

# Disk is often full on small boot disks: do NOT write a full .tar.gz under /tmp (curl exit 23 = write error).
# Stop the service so deleted files release blocks; remove old tree; stream GCS -> tar.

if ! command -v curl >/dev/null 2>&1; then
  echo "ERROR: curl is required on the VM." >&2
  exit 1
fi
if ! command -v python3 >/dev/null 2>&1; then
  echo "ERROR: python3 is required on the VM (for token + URL encoding)." >&2
  exit 1
fi

ENC="$(python3 -c "import os, urllib.parse; print(urllib.parse.quote(os.environ['TITANORBIT_GCS_OBJECT'], safe=''))")"
TOKEN="$(curl -sf -H "Metadata-Flavor: Google" \
  "http://metadata.google.internal/computeMetadata/v1/instance/service-accounts/default/token" \
  | python3 -c "import sys, json; print(json.load(sys.stdin)['access_token'])")"

URL="https://storage.googleapis.com/storage/v1/b/${TITANORBIT_GCS_BUCKET}/o/${ENC}?alt=media"

mkdir -p "${TITANORBIT_INSTALL_ROOT}"

echo "== Stop titanorbit-server so files can be removed and disk space reclaimed =="
if command -v systemctl >/dev/null 2>&1; then
  sudo systemctl stop titanorbit-server 2>/dev/null || true
  sleep 2
fi

echo "== GCS install: gs://${TITANORBIT_GCS_BUCKET}/${TITANORBIT_GCS_OBJECT} (stream -> tar, no /tmp tarball)"
echo "== Removing previous tree: ${TITANORBIT_INSTALL_ROOT}/${TITANORBIT_EXTRACT_DIR}"
rm -rf "${TITANORBIT_INSTALL_ROOT:?}/${TITANORBIT_EXTRACT_DIR}"

if ! (
  curl -sfL -H "Authorization: Bearer ${TOKEN}" "${URL}" | tar -xzf - -C "${TITANORBIT_INSTALL_ROOT}"
); then
  echo "ERROR: download or extract failed; removing partial folder if present." >&2
  rm -rf "${TITANORBIT_INSTALL_ROOT:?}/${TITANORBIT_EXTRACT_DIR}" 2>/dev/null || true
  echo "If you saw 'No space left on device', run deploy with freeDisk useGcs (or resize the boot disk)." >&2
  echo 'On the VM, inspect usage:  du -xhd1 /home/$USER | sort -h | tail -20' >&2
  echo '                          sudo du -xhd1 /var | sort -h | tail -20' >&2
  exit 1
fi

TARGET="${TITANORBIT_INSTALL_ROOT}/${TITANORBIT_EXTRACT_DIR}"
chmod -R a+rX "${TARGET}" 2>/dev/null || true
chmod 755 "${TARGET}/TitanOrbitServer" "${TARGET}/TitanOrbitServer.x86_64" 2>/dev/null || true
chmod a+r "${TARGET}/GameAssembly.so" "${TARGET}/UnityPlayer.so" 2>/dev/null || true

META="${TARGET}/TitanOrbitServer_Data/il2cpp_data/Metadata/global-metadata.dat"
if [[ ! -f "${META}" ]]; then
  echo "FATAL: global-metadata.dat missing after extract: ${META}" >&2
  exit 1
fi
SZ="$(stat -c%s "${META}" 2>/dev/null || echo 0)"
if [[ "${SZ}" -lt 1000000 ]]; then
  echo "FATAL: global-metadata.dat too small (${SZ} bytes). Repack on Windows with Unity closed." >&2
  exit 1
fi

# Windows tar can ship CRLF scripts. systemd then exits 127 (env: 'bash\r').
if [ -f "${TARGET}/run_titanorbit_server.sh" ]; then
  sed -i 's/\r$//' "${TARGET}/run_titanorbit_server.sh"
  chmod 755 "${TARGET}/run_titanorbit_server.sh"
fi
if [ -f "${TARGET}/TitanOrbitServer" ] && ! grep -q $'\x7fELF' "${TARGET}/TitanOrbitServer" 2>/dev/null; then
  sed -i 's/\r$//' "${TARGET}/TitanOrbitServer" || true
fi

echo "== Extract OK under ${TARGET}"
ls -la "${TARGET}" | head -n 20
