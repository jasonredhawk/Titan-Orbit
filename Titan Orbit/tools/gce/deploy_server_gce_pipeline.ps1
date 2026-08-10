# End-to-end dedicated server deploy: optional VM disk cleanup, upload (OpenSSH or GCS), install on VM, restart systemd or hard VM reset.
# Replaces a manual split between upload_linux_build_to_gcs.bat (bucket only) and Cloud Shell.
#
# Flags (any position, case-insensitive): freeDisk  useGcs  aggressive  useIap
#   freeDisk   — run vm_free_disk_for_server_upload_gce.ps1 before upload (same as vm_free_disk_for_server_upload_gce.bat).
#   useGcs     — tar + upload_linux_build_to_gcs.bat, then VM pulls from GCS and extracts (needs bucket IAM for the VM SA).
#   aggressive — pass through to free-disk script when freeDisk is set (also removes entire game install; see vm_free_disk_for_server_upload.sh).
#                Implied automatically when both freeDisk and useGcs are set (small GCE boot disks).
#   useIap     — IAP for free-disk / GCS install ssh; after deploy matches legacy deploy_server_gce.bat: hard VM reset instead of systemctl restart.
#
# Remaining arguments follow upload_linux_build_to_gce.bat / upload_linux_build_to_gcs.bat order:
#   [build folder] [project id] [bucket name when -UseGcs]   plus optional useIap mixed in.
#
# Examples (from tools\gce):
#   .\deploy_server_gce_pipeline.ps1
#   .\deploy_server_gce_pipeline.ps1 freeDisk useGcs useIap
#   .\deploy_server_gce_pipeline.ps1 useGcs "D:\build\TitanOrbitLinux1" my-gcp-project my-bucket
#   .\deploy_server_gce_pipeline.ps1 "C:\path\TitanOrbitLinux1" my-project useIap

param()

$ErrorActionPreference = "Stop"

$gceDir = $PSScriptRoot
$repoRoot = (Resolve-Path (Join-Path $gceDir "..\..")).Path

# Invoke a .bat via one cmd /c string so paths with spaces (project folder "Titan Orbit")
# survive PowerShell -> cmd. The old pattern (& cmd.exe @('/c','call "bat"','"path with space"'))
# fails immediately with: The filename, directory name, or volume label syntax is incorrect.
function Invoke-GceBat {
    param(
        [Parameter(Mandatory = $true)][string] $BatPath,
        [string[]] $BatArgs = @()
    )
    if (-not (Test-Path -LiteralPath $BatPath)) {
        throw "Missing bat: $BatPath"
    }
    $parts = New-Object System.Collections.Generic.List[string]
    $parts.Add('"' + $BatPath + '"') | Out-Null
    foreach ($a in $BatArgs) {
        if ($null -eq $a) { continue }
        $s = [string]$a
        if ($s.Length -eq 0) { continue }
        $parts.Add('"' + ($s.Replace('"', '')) + '"') | Out-Null
    }
    # One string after /c. Do NOT use Start-Process -ArgumentList @('/c', $cmdLine):
    # that re-splits on spaces inside "Titan Orbit" and fails before the bat runs.
    $cmdLine = ($parts -join ' ')
    Write-Host "cmd /c $cmdLine"

    # Redirect bat stdout/stderr to a temp log inside cmd, then print the log.
    # Why not `cmd | ForEach-Object { Write-Host }`?
    #   1) Native stdout would become this function's return value (array of log lines + exit),
    #      so callers falsely treat a successful upload as failure.
    #   2) A pipeline can overwrite $LASTEXITCODE with the last pipeline command's code.
    $logFile = Join-Path $env:TEMP ("titanorbit-gce-bat-" + [guid]::NewGuid().ToString("n") + ".log")
    try {
        cmd.exe /c "$cmdLine > `"$logFile`" 2>&1"
        $code = $LASTEXITCODE
        if (Test-Path -LiteralPath $logFile) {
            Get-Content -LiteralPath $logFile | ForEach-Object { Write-Host $_ }
        }
    }
    finally {
        Remove-Item -LiteralPath $logFile -Force -ErrorAction SilentlyContinue
    }

    if ($null -eq $code) { return 1 }
    return [int]$code
}

$flags = @{
    freeDisk   = $false
    useGcs     = $false
    aggressive = $false
}
$positional = [System.Collections.Generic.List[string]]::new()
foreach ($a in $args) {
    $t = [string]$a
    if ($t -match '^(?i)freeDisk$') { $flags.freeDisk = $true; continue }
    if ($t -match '^(?i)useGcs$') { $flags.useGcs = $true; continue }
    if ($t -match '^(?i)aggressive$') { $flags.aggressive = $true; continue }
    $positional.Add($t) | Out-Null
}

$hasUseIap = ($positional | Where-Object { $_ -match '^(?i)useIap$' }).Count -gt 0
$resetVmAfter = $hasUseIap

if ($flags.freeDisk -and $flags.useGcs -and -not $flags.aggressive) {
    $flags.aggressive = $true
}

$withoutIap = @($positional | Where-Object { $_ -notmatch '^(?i)useIap$' })

function Get-DefaultSourceDir {
    Join-Path $repoRoot "BuildOutput\Server\TitanOrbitLinux1"
}

function Get-SourceDirAndLeaf {
    param([object[]] $Tail)
    $defaultDir = Get-DefaultSourceDir
    if ($Tail.Count -eq 0) {
        return @{ SourceDir = $defaultDir; Leaf = "TitanOrbitLinux1" }
    }
    $first = $Tail[0]
    if (Test-Path -LiteralPath $first -PathType Container) {
        $resolved = (Resolve-Path -LiteralPath $first).Path
        return @{ SourceDir = $resolved; Leaf = (Split-Path $resolved -Leaf) }
    }
    return @{ SourceDir = $defaultDir; Leaf = "TitanOrbitLinux1" }
}

$srcInfo = Get-SourceDirAndLeaf $withoutIap
$sourceDir = $srcInfo.SourceDir
$extractLeaf = $srcInfo.Leaf

$instanceDefault = "titanorbitcp"
if (-not [string]::IsNullOrWhiteSpace($env:TITANORBIT_GCE_INSTANCE)) {
    $instanceDefault = $env:TITANORBIT_GCE_INSTANCE.Trim()
}

Write-Host ""
Write-Host "=== Titan Orbit GCE deploy pipeline ==="
Write-Host "Repo root:     $repoRoot"
Write-Host "Source dir:    $sourceDir"
Write-Host "Mode:          $(if ($flags.useGcs) { 'GCS upload + VM pull' } else { 'OpenSSH folder upload' })"
Write-Host "Free disk:     $(if ($flags.freeDisk) { 'yes' } else { 'no' })$(if ($flags.freeDisk -and $flags.aggressive) { ' (aggressive)' } elseif ($flags.freeDisk) { '' })"
Write-Host "Post-deploy:   $(if ($resetVmAfter) { 'VM reset (useIap legacy)' } else { 'systemctl restart titanorbit-server' })"
Write-Host ""

if ($flags.freeDisk) {
    Write-Host "--- [1] Free disk on VM ---"
    $vmPs1 = Join-Path $gceDir "vm_free_disk_for_server_upload_gce.ps1"
    $vmArgs = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $vmPs1)
    if ($hasUseIap) { $vmArgs += "-UseIap" }
    if ($flags.aggressive) { $vmArgs += "-Aggressive" }
    & powershell.exe @vmArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Error "vm_free_disk_for_server_upload_gce.ps1 failed (exit $LASTEXITCODE)."
        exit $LASTEXITCODE
    }
    Write-Host ""
}

$uploadLabel = if ($flags.freeDisk) { "[2]" } else { "[1]" }

if ($flags.useGcs) {
    Write-Host "--- $uploadLabel Upload tarball to GCS ---"
    $gcsBat = Join-Path $gceDir "upload_linux_build_to_gcs.bat"
    $gcsExit = Invoke-GceBat -BatPath $gcsBat -BatArgs @($withoutIap)
    if ($gcsExit -ne 0) {
        Write-Error "upload_linux_build_to_gcs.bat failed (exit $gcsExit)."
        exit $gcsExit
    }
    Write-Host ""

    $installLabel = if ($flags.freeDisk) { "[3]" } else { "[2]" }
    Write-Host "--- $installLabel VM: pull from GCS + extract ---"
    $installPs1 = Join-Path $gceDir "install_linux_build_from_gcs_remote.ps1"
    $bucket = "titan-orbit-dedicated-server"
    $gcsPrefix = "titanorbit-linux-build"
    $projectId = "titan-orbit"
    if ($withoutIap.Count -ge 1 -and (Test-Path -LiteralPath $withoutIap[0] -PathType Container)) {
        if ($withoutIap.Count -ge 2) { $projectId = $withoutIap[1] }
        if ($withoutIap.Count -ge 3) { $bucket = $withoutIap[2] }
    }
    elseif ($withoutIap.Count -ge 1) {
        $projectId = $withoutIap[0]
        if ($withoutIap.Count -ge 2) { $bucket = $withoutIap[1] }
    }

    $installParams = @{
        ProjectId  = $projectId
        Bucket     = $bucket
        GcsPrefix  = $gcsPrefix
        ExtractDir = $extractLeaf
    }
    if ($hasUseIap) {
        $installParams.UseIap = $true
    }
    & $installPs1 @installParams
    if ($LASTEXITCODE -ne 0) {
        Write-Error "install_linux_build_from_gcs_remote.ps1 failed (exit $LASTEXITCODE)."
        exit $LASTEXITCODE
    }
    Write-Host ""
}
else {
    Write-Host "--- $uploadLabel Upload build folder (OpenSSH) ---"
    $upBat = Join-Path $gceDir "upload_linux_build_to_gce.bat"
    $upExit = Invoke-GceBat -BatPath $upBat -BatArgs @($positional)
    if ($upExit -ne 0) {
        Write-Error "upload_linux_build_to_gce.bat failed (exit $upExit)."
        exit $upExit
    }
    Write-Host ""
}

$finBase = if ($flags.useGcs) { if ($flags.freeDisk) { 4 } else { 3 } } else { if ($flags.freeDisk) { 3 } else { 2 } }
Write-Host "--- [$finBase] Post-deploy: $(if ($resetVmAfter) { 'reset VM' } else { 'restart service' }) ---"

$resetBat = Join-Path $gceDir "reset_gce_vm.bat"
$restartBat = Join-Path $gceDir "restart_titanorbit_server_on_gce.bat"

if ($resetVmAfter) {
    $resetArgs = @()
    if ($withoutIap.Count -ge 2 -and (Test-Path -LiteralPath $withoutIap[0] -PathType Container)) {
        $resetArgs = @($instanceDefault, $withoutIap[1])
    }
    $resetExit = Invoke-GceBat -BatPath $resetBat -BatArgs $resetArgs
    if ($resetExit -ne 0) {
        Write-Error "reset_gce_vm.bat failed (exit $resetExit)."
        exit $resetExit
    }
}
else {
    # restart_titanorbit_server_on_gce.bat expects [project-id] [useIap|plainFirst...] - never pass GCS bucket as an arg.
    $restartProject = $null
    if ($withoutIap.Count -ge 1 -and (Test-Path -LiteralPath $withoutIap[0] -PathType Container)) {
        if ($withoutIap.Count -ge 2) { $restartProject = $withoutIap[1] }
    }
    elseif ($withoutIap.Count -ge 1) {
        $restartProject = $withoutIap[0]
    }
    $restartArgs = @()
    if ($null -ne $restartProject -and $restartProject -ne "") {
        $restartArgs = @($restartProject)
    }
    $restartExit = Invoke-GceBat -BatPath $restartBat -BatArgs $restartArgs
    if ($restartExit -ne 0) {
        Write-Error "restart_titanorbit_server_on_gce.bat failed (exit $restartExit)."
        exit $restartExit
    }
}

Write-Host ""
Write-Host "=== Pipeline finished OK === $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ==="
exit 0
