# Run from Windows PowerShell (not cmd.exe) to avoid "Terminate batch job (Y/N)?" when gcloud uses plink.
# Usage (from this folder):
#   .\restart_titanorbit_server.ps1
#   .\restart_titanorbit_server.ps1 -UseIap
#   .\restart_titanorbit_server.ps1 -UseIap -PlainSshFirst   (same order as restart_titanorbit_server_on_gce_iap.bat)
param(
    [string] $ProjectId = "titan-orbit",
    [string] $Zone = "us-central1-f",
    [string] $InstanceTarget = "jason@titanorbitcp",
    [string] $ServiceName = "titanorbit-server",
    [switch] $UseIap,
    [switch] $PlainSshFirst
)

$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$ps1 = Join-Path $here "restart_server_remote.ps1"
if (-not (Test-Path $ps1)) {
    Write-Error "Missing: $ps1"
    exit 1
}

# Child process: restart_server_remote.ps1 uses `exit` and would close your interactive session if dot-sourced here.
$argList = @(
    "-NoProfile",
    "-ExecutionPolicy", "Bypass",
    "-File", $ps1,
    "-ProjectId", $ProjectId,
    "-Zone", $Zone,
    "-InstanceTarget", $InstanceTarget,
    "-ServiceName", $ServiceName
)
if ($UseIap) {
    $argList += "-UseIap"
}
if ($PlainSshFirst) {
    $argList += "-PlainSshFirst"
}
$p = Start-Process -FilePath "powershell.exe" -ArgumentList $argList -Wait -PassThru -NoNewWindow
exit $(if ($null -ne $p.ExitCode) { $p.ExitCode } else { 1 })
