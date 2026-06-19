# Pull latest tarball from GCS onto the VM and extract (same layout as README Cloud Shell flow).
# Uses gcloud compute ssh + base64 inline script (same transport as vm_free_disk_for_server_upload_gce.ps1).
# The VM uses its service account token (metadata) + curl — no gsutil on the guest required.
#
# Usage:
#   powershell -NoProfile -File .\install_linux_build_from_gcs_remote.ps1
#   powershell -NoProfile -File .\install_linux_build_from_gcs_remote.ps1 -UseIap
#   powershell -NoProfile -File .\install_linux_build_from_gcs_remote.ps1 -Bucket "my-bucket" -GcsPrefix "titanorbit-linux-build" -ExtractDir "TitanOrbitLinux1"

param(
    [string] $ProjectId = "titan-orbit",
    [string] $Zone = "us-central1-f",
    [string] $InstanceName = "titanorbitcp",
    [string] $SshUser = "jason",
    [string] $Bucket = "titan-orbit-dedicated-server",
    [string] $GcsPrefix = "titanorbit-linux-build",
    [string] $ExtractDir = "TitanOrbitLinux1",
    [switch] $UseIap
)

$ErrorActionPreference = "Stop"

foreach ($a in $args) {
    if ($a -ieq "useIap") { $UseIap = $true }
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

function BashSingleQuote([string] $s) {
    if ($null -eq $s) { return "''" }
    return "'" + ($s.Replace("'", "'\''")) + "'"
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

$scriptPath = Join-Path $PSScriptRoot "install_linux_build_from_gcs_on_vm.sh"
if (-not (Test-Path -LiteralPath $scriptPath)) {
    Write-Error "Missing script: $scriptPath"
    exit 1
}

$gcsObject = "${GcsPrefix}/${ExtractDir}-latest.tar.gz"
$installRoot = "/home/${SshUser}/titanorbit-server"

$body = [System.IO.File]::ReadAllText($scriptPath)
$body = $body -replace "`r`n", "`n"
if (-not $body.EndsWith("`n")) {
    $body += "`n"
}

$head = @"
export TITANORBIT_GCS_BUCKET=$(BashSingleQuote $Bucket)
export TITANORBIT_GCS_OBJECT=$(BashSingleQuote $gcsObject)
export TITANORBIT_INSTALL_ROOT=$(BashSingleQuote $installRoot)
export TITANORBIT_EXTRACT_DIR=$(BashSingleQuote $ExtractDir)
"@
$head = $head -replace "`r`n", "`n"
if (-not $head.EndsWith("`n")) {
    $head += "`n"
}

$full = $head + $body
$packB64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($full))
if ($packB64.Length -gt 6500) {
    Write-Error "install payload is too large for gcloud --command (base64 length $($packB64.Length)). Shorten scripts or run from Cloud Shell."
    exit 1
}

$remoteCmd = "bash -lc 'echo $packB64 | base64 -d | bash -s'"
$instanceTarget = "${SshUser}@${InstanceName}"

Write-Host "Project:       $ProjectId"
Write-Host "Zone:          $Zone"
Write-Host "Target:        $instanceTarget"
Write-Host "GCS object:    gs://${Bucket}/${gcsObject}"
Write-Host "Install root:  ${installRoot}"
if ($UseIap.IsPresent) { Write-Host "IAP:           enabled" }
Write-Host ""
Write-Host "Running remote GCS pull + extract via gcloud compute ssh ..."

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
