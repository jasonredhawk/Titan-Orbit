# Registers your local %USERPROFILE%\.ssh\google_compute_engine.pub on GCP via gcloud (no Console paste).
# Merges into existing ssh-keys: uses INSTANCE metadata if the VM already has ssh-keys; otherwise PROJECT metadata.
#
# Requires: gcloud auth, roles that can read/write instance or project metadata (e.g. compute.instanceAdmin.v1 / Editor).
#
# Usage (from tools\gce):
#   powershell -NoProfile -File .\register_local_ssh_key_on_gce.ps1
#   powershell -NoProfile -File .\register_local_ssh_key_on_gce.ps1 -WhatIf
#   powershell -NoProfile -File .\register_local_ssh_key_on_gce.ps1 -LinuxUser jason -ProjectId titan-orbit

param(
    [string] $ProjectId = "titan-orbit",
    [string] $Zone = "us-central1-f",
    [string] $InstanceName = "titanorbitcp",
    [string] $LinuxUser = "jason",
    [string] $PrivateKeyPath = "",
    [switch] $WhatIf
)

$ErrorActionPreference = "Stop"

if (-not $PrivateKeyPath) {
    $PrivateKeyPath = Join-Path $env:USERPROFILE ".ssh\google_compute_engine"
}
$pubPath = "$PrivateKeyPath.pub"
if (-not (Test-Path -LiteralPath $pubPath)) {
    Write-Error "Missing public key: $pubPath`nRun create_local_gce_ssh_key.bat first (or create a key pair named google_compute_engine)."
    exit 1
}
if (-not (Test-Path -LiteralPath $PrivateKeyPath)) {
    Write-Error "Missing private key: $PrivateKeyPath"
    exit 1
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

function Invoke-FixGoogleComputeEngineKeyAcl {
    param([Parameter(Mandatory = $true)][string] $KeyPath)
    $fixAcl = Join-Path $PSScriptRoot "fix_google_compute_engine_key_acl.ps1"
    if (-not (Test-Path -LiteralPath $fixAcl)) {
        return
    }
    Write-Host "Tightening private key ACLs (OpenSSH on Windows) ..."
    $psHost = Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"
    # Nested -File breaks when repo path has spaces (e.g. Titan Orbit); use -Command with single-quoted paths.
    # Use string "'" for Replace so .NET picks Replace(string,string), not Replace(char,char).
    $fixLit = "'" + ($fixAcl.Replace("'", "''")) + "'"
    $keyLit = "'" + ($KeyPath.Replace("'", "''")) + "'"
    $cmd = "& $fixLit -KeyPath $keyLit"
    $aclProc = Start-Process -FilePath $psHost -ArgumentList @(
        "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", $cmd
    ) -Wait -PassThru -NoNewWindow
    if ($aclProc.ExitCode -ne 0) {
        exit $aclProc.ExitCode
    }
}

Invoke-FixGoogleComputeEngineKeyAcl -KeyPath $PrivateKeyPath

$pubLine = (Get-Content -LiteralPath $pubPath -TotalCount 1 | Select-Object -First 1).Trim()
if (-not $pubLine) {
    Write-Error "Empty or unreadable: $pubPath"
    exit 1
}
$entry = if ($pubLine.StartsWith("${LinuxUser}:")) { $pubLine } else { "${LinuxUser}:$pubLine" }
$n = [Math]::Min(72, $entry.Length)
Write-Host ('Key line to register (starts with ' + $LinuxUser + '): ' + $entry.Substring(0, $n) + '...')

function Invoke-GcloudJson {
    # Do not name this parameter $Args — it clashes with PowerShell's automatic $Args and gcloud gets no subcommand.
    param([string[]] $GcloudArgumentList)
    $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("titanorbit-gcemeta-" + [Guid]::NewGuid().ToString() + ".json")
    $errTmp = Join-Path ([System.IO.Path]::GetTempPath()) ("titanorbit-gcemeta-" + [Guid]::NewGuid().ToString() + ".err.txt")
    $fullArgs = @("--quiet") + $GcloudArgumentList
    try {
        $proc = Start-Process -FilePath $gcloudExe -ArgumentList $fullArgs -Wait -PassThru -NoNewWindow `
            -RedirectStandardOutput $tmp -RedirectStandardError $errTmp
        if ($proc.ExitCode -ne 0) {
            $stderr = ""
            if (Test-Path $errTmp) {
                $stderr = (Get-Content -LiteralPath $errTmp -Raw -ErrorAction SilentlyContinue).Trim()
            }
            Write-Error ("gcloud failed (exit " + $proc.ExitCode + "): " + $stderr)
            exit $proc.ExitCode
        }
        return (Get-Content -LiteralPath $tmp -Raw | ConvertFrom-Json)
    }
    finally {
        Remove-Item $tmp, $errTmp -Force -ErrorAction SilentlyContinue
    }
}

function Get-SshKeysFromInstanceMetadata($instanceJson) {
    $items = $instanceJson.metadata.items
    if (-not $items) {
        return $null
    }
    foreach ($it in $items) {
        if ($it.key -eq "ssh-keys") {
            return [string]$it.value
        }
    }
    return $null
}

function Get-SshKeysFromProjectMetadata($projectJson) {
    $items = $projectJson.commonInstanceMetadata.items
    if (-not $items) {
        return $null
    }
    foreach ($it in $items) {
        if ($it.key -eq "ssh-keys") {
            return [string]$it.value
        }
    }
    return $null
}

function Merge-SshKeys([string] $Existing, [string] $NewLine) {
    $lines = New-Object System.Collections.Generic.List[string]
    if ($Existing) {
        foreach ($raw in $Existing -split "`r?`n") {
            $t = $raw.Trim()
            if ($t) {
                $lines.Add($t)
            }
        }
    }
    $normNew = $NewLine.Trim()
    foreach ($x in $lines) {
        if ($x -eq $normNew) {
            return @{ Text = $Existing; Added = $false }
        }
    }
    $lines.Add($normNew)
    $text = ($lines -join "`n") + "`n"
    return @{ Text = $text; Added = $true }
}

$inst = Invoke-GcloudJson -GcloudArgumentList @(
    "compute", "instances", "describe", $InstanceName,
    "--project=$ProjectId", "--zone=$Zone", "--format=json"
)

$oslogin = $false
foreach ($it in $inst.metadata.items) {
    if ($it.key -eq "enable-oslogin" -and ($it.value -eq "TRUE" -or $it.value -eq "true")) {
        $oslogin = $true
    }
}
if ($oslogin) {
    Write-Warning "This instance has enable-oslogin=TRUE. Project/instance ssh-keys metadata may be ignored. Use OS Login IAM roles or disable OS Login for metadata keys."
}

$instanceSshKeys = Get-SshKeysFromInstanceMetadata $inst
$target = if ($null -ne $instanceSshKeys -and $instanceSshKeys.Trim().Length -gt 0) { "instance" } else { "project" }

if ($target -eq "instance") {
    Write-Host "Merging into INSTANCE metadata (ssh-keys) on $InstanceName ..."
    $m = Merge-SshKeys $instanceSshKeys $entry
}
else {
    Write-Host "No ssh-keys on instance; merging into PROJECT metadata (ssh-keys) ..."
    $proj = Invoke-GcloudJson -GcloudArgumentList @("compute", "project-info", "describe", "--project=$ProjectId", "--format=json")
    $projKeys = Get-SshKeysFromProjectMetadata $proj
    $m = Merge-SshKeys $projKeys $entry
}

if (-not $m.Added) {
    Write-Host "Already registered (same line present). Nothing to do."
    exit 0
}

if ($WhatIf) {
    Write-Host ('[WhatIf] Would write ssh-keys to ' + $target + ' metadata (' + $m.Text.Length + ' chars).')
    Write-Host $m.Text
    exit 0
}

$outFile = Join-Path ([System.IO.Path]::GetTempPath()) ("titanorbit-ssh-keys-" + [Guid]::NewGuid().ToString() + ".txt")
try {
    # GCP expects newline-separated keys; UTF-8 without BOM is safest for gcloud.
    $utf8NoBom = New-Object System.Text.UTF8Encoding $false
    [System.IO.File]::WriteAllText($outFile, $m.Text, $utf8NoBom)

    if ($target -eq "instance") {
        Write-Host "Running: gcloud compute instances add-metadata ... --metadata-from-file ssh-keys=..."
        & $gcloudExe @(
            "compute", "instances", "add-metadata", $InstanceName,
            "--project=$ProjectId", "--zone=$Zone",
            "--metadata-from-file", "ssh-keys=$outFile"
        )
    }
    else {
        Write-Host "Running: gcloud compute project-info add-metadata ... --metadata-from-file ssh-keys=..."
        & $gcloudExe @(
            "compute", "project-info", "add-metadata",
            "--project=$ProjectId",
            "--metadata-from-file", "ssh-keys=$outFile"
        )
    }
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}
finally {
    Remove-Item $outFile -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "Done. Wait ~30-60s for metadata to propagate, then retry SSH or upload_linux_build_to_gce_openssh.bat (useIap if needed)."
exit 0
