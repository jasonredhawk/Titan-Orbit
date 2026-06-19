# Deploy (upload + restart) using repo defaults: Linux user jason@titanorbitcp (paths like /home/jason/titanorbit-server).
# Clears TITANORBIT_GCE_* overrides so upload + restart use built-in defaults.
# Run from tools\gce:
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\deploy_identity_repo_default.ps1
#
# Requires on the VM: user "jason" with your google_compute_engine public key in ~jason/.ssh/authorized_keys

$ErrorActionPreference = "Stop"

Remove-Item Env:\TITANORBIT_GCE_SSH_USER -ErrorAction SilentlyContinue
Remove-Item Env:\TITANORBIT_GCE_INSTANCE_TARGET -ErrorAction SilentlyContinue

Write-Host "Cleared TITANORBIT_GCE_SSH_USER and TITANORBIT_GCE_INSTANCE_TARGET (repo defaults: jason@titanorbitcp)."
Write-Host ""

$bat = Join-Path $PSScriptRoot "deploy_server_gce_iap.bat"
& cmd.exe /c "`"$bat`""
exit $LASTEXITCODE
