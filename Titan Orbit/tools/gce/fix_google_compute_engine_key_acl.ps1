# Tightens ACLs on %USERPROFILE%\.ssh\google_compute_engine so OpenSSH (ssh.exe) will load it on Windows.
# Fixes: "UNPROTECTED PRIVATE KEY", "OWNER RIGHTS (S-1-3-4)", "Permissions ... are too open".

param(
    [string] $KeyPath = (Join-Path $env:USERPROFILE ".ssh\google_compute_engine")
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $KeyPath)) {
    Write-Error "Key file not found: $KeyPath`nCreate it with create_local_gce_ssh_key.bat or gcloud compute ssh once."
    exit 1
}

# Use full account (e.g. AzureAD\name or DOMAIN\name); plain USERNAME is wrong on some PCs.
$who = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
if ([string]::IsNullOrWhiteSpace($who)) {
    Write-Error "Could not resolve current Windows identity."
    exit 1
}

Write-Host "Fixing ACLs on: $KeyPath"
Write-Host "(inheritance off, remove OWNER RIGHTS / broad grants, then grant only you Read)"
Write-Host "Identity for /grant: $who"
Write-Host ""

# OpenSSH requires a minimal DACL; inherited / OWNER RIGHTS ACEs cause rejection.
& icacls.exe $KeyPath /inheritance:r
if ($LASTEXITCODE -ne 0) {
    Write-Error "icacls /inheritance:r failed (exit $LASTEXITCODE). Run PowerShell as your normal user (not elevated) or fix path."
    exit $LASTEXITCODE
}

# OWNER RIGHTS (S-1-3-4) — remove before and after /grant (order matters on some builds).
foreach ($sid in @('*S-1-3-4', 'OWNER RIGHTS')) {
    & icacls.exe $KeyPath /remove $sid 2>$null | Out-Null
}

# Drop common inherited groups OpenSSH still treats as "others" (ignore errors if absent).
foreach ($entry in @('Everyone', 'Users', 'Authenticated Users')) {
    & icacls.exe $KeyPath /remove:g $entry 2>$null | Out-Null
}

& icacls.exe $KeyPath /grant:r "${who}:(R)"
if ($LASTEXITCODE -ne 0) {
    Write-Error "icacls /grant:r failed (exit $LASTEXITCODE)."
    exit $LASTEXITCODE
}

foreach ($sid in @('*S-1-3-4', 'OWNER RIGHTS')) {
    & icacls.exe $KeyPath /remove $sid 2>$null | Out-Null
}

Write-Host ""
Write-Host "Done. Current ACL:"
& icacls.exe $KeyPath
Write-Host ""
Write-Host "Retry: restart_titanorbit_server_on_gce_iap.bat or install_enable_server_service_on_gce_iap.bat"
