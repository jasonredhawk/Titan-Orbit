# Deploy (upload + restart) using the Linux user that matches Google Console SSH keys (often jason_redhawk, not jason).
# Run from tools\gce:
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\deploy_identity_google_user.ps1
# Optional: -LinuxUser "other_user" -InstanceName "titanorbitcp"

param(
    [string] $LinuxUser = "jason_redhawk",
    [string] $InstanceName = "titanorbitcp"
)

$ErrorActionPreference = "Stop"

$env:TITANORBIT_GCE_SSH_USER = $LinuxUser.Trim()
$env:TITANORBIT_GCE_INSTANCE_TARGET = "$LinuxUser@$InstanceName".Trim()

Write-Host "Using Linux identity for this session:"
Write-Host "  TITANORBIT_GCE_SSH_USER        = $($env:TITANORBIT_GCE_SSH_USER)"
Write-Host "  TITANORBIT_GCE_INSTANCE_TARGET = $($env:TITANORBIT_GCE_INSTANCE_TARGET)"
Write-Host ""
Write-Host "If your server files live under /home/jason/... on the VM, create user jason or use deploy_identity_repo_default.ps1 instead."
Write-Host ""

$bat = Join-Path $PSScriptRoot "deploy_server_gce_iap.bat"
& cmd.exe /c "`"$bat`""
exit $LASTEXITCODE
