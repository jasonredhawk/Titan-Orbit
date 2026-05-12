# Run vm_free_disk_for_server_upload.sh on a GCE VM without gcloud compute scp (Windows uses PuTTY
# pscp for scp — same "error while writing: failure" as large uploads when /tmp is full or flaky).
# Same delivery as install_unit_remote.ps1: gcloud compute ssh --command="bash -lc 'echo <b64> | base64 -d | bash -s'".
#
# Usage (from tools\gce):
#   powershell -NoProfile -File .\vm_free_disk_for_server_upload_gce.ps1
#   powershell -NoProfile -File .\vm_free_disk_for_server_upload_gce.ps1 -UseIap -Aggressive
#   powershell -NoProfile -File .\vm_free_disk_for_server_upload_gce.ps1 useIap aggressive   # same as .bat passthrough

param(
    [string] $ProjectId = "titan-orbit",
    [string] $Zone = "us-central1-f",
    [string] $InstanceName = "titanorbitcp",
    [string] $SshUser = "jason",
    [switch] $UseIap,
    [switch] $Aggressive
)

$ErrorActionPreference = "Stop"

foreach ($a in $args) {
    if ($a -ieq "useIap") { $UseIap = $true }
    if ($a -ieq "aggressive") { $Aggressive = $true }
}

if (-not [string]::IsNullOrWhiteSpace($env:TITANORBIT_GCE_PROJECT)) {
    $ProjectId = $env:TITANORBIT_GCE_PROJECT.Trim()
}
if (-not [string]::IsNullOrWhiteSpace($env:TITANORBIT_GCE_ZONE)) {
    $Zone = $env:TITANORBIT_GCE_ZONE.Trim()
}
if (-not [string]::IsNullOrWhiteSpace($env:TITANORBIT_GCE_INSTANCE)) {
    $InstanceName = $env:TITANORBIT_GCE_INSTANCE.Trim()
}
if (-not [string]::IsNullOrWhiteSpace($env:TITANORBIT_GCE_SSH_USER)) {
    $SshUser = $env:TITANORBIT_GCE_SSH_USER.Trim()
}

try {
    $gcloudEntry = (Get-Command gcloud -ErrorAction Stop).Source
    $gcloudDir = Split-Path $gcloudEntry
    $gcloudCmd = Join-Path $gcloudDir "gcloud.cmd"
    if (Test-Path $gcloudCmd) {
        $gcloudExe = $gcloudCmd
    }
    else {
        $gcloudExe = $gcloudEntry
    }
}
catch {
    Write-Error "gcloud not found in PATH."
    exit 1
}

$scriptPath = Join-Path $PSScriptRoot "vm_free_disk_for_server_upload.sh"
if (-not (Test-Path -LiteralPath $scriptPath)) {
    Write-Error "Missing script: $scriptPath"
    exit 1
}

$text = [System.IO.File]::ReadAllText($scriptPath)
$text = $text -replace "`r`n", "`n"
if (-not $text.EndsWith("`n")) {
    $text += "`n"
}

$packB64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($text))
# Windows CreateProcess command-line limit (~8191); keep headroom for gcloud flags + bash wrapper.
if ($packB64.Length -gt 6500) {
    Write-Error "vm_free_disk_for_server_upload.sh is too large to embed in gcloud --command on Windows (base64 length $($packB64.Length)). Run the .sh from browser SSH or Cloud Shell instead."
    exit 1
}

$tail = if ($Aggressive.IsPresent) { " -- --aggressive" } else { "" }
$remoteCmd = "bash -lc 'echo $packB64 | base64 -d | bash -s$tail'"

$instanceTarget = "${SshUser}@${InstanceName}"

Write-Host "Project:  $ProjectId"
Write-Host "Zone:     $Zone"
Write-Host "Target:   $instanceTarget"
if ($UseIap.IsPresent) { Write-Host "IAP:      enabled" }
if ($Aggressive.IsPresent) { Write-Host "Mode:     aggressive" }
Write-Host ""
Write-Host "Running: gcloud compute ssh ... --command=bash -lc 'echo <b64> | base64 -d | bash -s' (no pscp; avoids /tmp write on VM)"

$gcloudArgs = @(
    "--quiet",
    "compute", "ssh", $instanceTarget,
    "--project=$ProjectId",
    "--zone=$Zone",
    "--strict-host-key-checking=no"
)
if ($UseIap.IsPresent) {
    $gcloudArgs += "--tunnel-through-iap"
}
$gcloudArgs += "--command=$remoteCmd"

$prevPrompts = $env:CLOUDSDK_CORE_DISABLE_PROMPTS
$env:CLOUDSDK_CORE_DISABLE_PROMPTS = "1"
try {
    & $gcloudExe @gcloudArgs
    exit $LASTEXITCODE
}
finally {
    if ($null -eq $prevPrompts) {
        Remove-Item Env:\CLOUDSDK_CORE_DISABLE_PROMPTS -ErrorAction SilentlyContinue
    }
    else {
        $env:CLOUDSDK_CORE_DISABLE_PROMPTS = $prevPrompts
    }
}
