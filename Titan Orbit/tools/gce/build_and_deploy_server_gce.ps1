# One-shot Linux dedicated-server build + GCE deploy for Titan Orbit.
#
# Separate from deploy_server_gce.bat (deploy-only). This file owns build+deploy.
#
# What this does (in order):
#   1) Find the Unity Editor matching ProjectVersion.txt (or TITANORBIT_UNITY_EDITOR).
#   2) Run a headless IL2CPP Linux Dedicated Server build via
#      TitanOrbit.Editor.Build.TitanOrbitBuildAutomation.BuildHeadlessServerLinuxBatchMode
#      -> output: BuildOutput/Server/TitanOrbitLinux1
#   3) Call deploy_server_gce_pipeline.ps1 (same flags as deploy_server_gce.bat).
#
# Why a .bat / PowerShell script (not an Editor menu item)?
#   Upload must run with the Unity Editor closed so IL2CPP files are not truncated while tar'ing.
#   Batchmode builds the binary, exits Unity, then deploy uploads - one command from a terminal.
#
# Usage (from tools\gce):
#   .\build_and_deploy_server_gce.bat
#   .\build_and_deploy_server_gce.bat freeDisk useGcs useIap
#   .\build_and_deploy_server_gce.bat buildOnly
#   .\build_and_deploy_server_gce.bat deployOnly freeDisk useGcs
#
# Flags (any order, case-insensitive):
#   buildOnly   - Unity build only (skip deploy)
#   deployOnly  - skip Unity build; deploy existing TitanOrbitLinux1 folder
#   freeDisk / useGcs / aggressive / useIap - passed through to deploy (defaults: freeDisk useGcs)
#   Plus optional deploy positionals: [build folder] [project id] [bucket]

param()

$ErrorActionPreference = "Stop"

$gceDir = $PSScriptRoot
$repoRoot = (Resolve-Path (Join-Path $gceDir "..\..")).Path
$projectVersionPath = Join-Path $repoRoot "ProjectSettings\ProjectVersion.txt"
$defaultSourceDir = Join-Path $repoRoot "BuildOutput\Server\TitanOrbitLinux1"
$logDir = Join-Path $repoRoot "BuildOutput\Logs"
$executeMethod = "TitanOrbit.Editor.Build.TitanOrbitBuildAutomation.BuildHeadlessServerLinuxBatchMode"

# --- Parse flags ---
$flags = @{
    buildOnly  = $false
    deployOnly = $false
    freeDisk   = $false
    useGcs     = $false
    aggressive = $false
    useIap     = $false
}
$positional = [System.Collections.Generic.List[string]]::new()
foreach ($a in $args) {
    $t = [string]$a
    if ($t -match '^(?i)buildOnly$') { $flags.buildOnly = $true; continue }
    if ($t -match '^(?i)deployOnly$') { $flags.deployOnly = $true; continue }
    if ($t -match '^(?i)freeDisk$') { $flags.freeDisk = $true; continue }
    if ($t -match '^(?i)useGcs$') { $flags.useGcs = $true; continue }
    if ($t -match '^(?i)aggressive$') { $flags.aggressive = $true; continue }
    if ($t -match '^(?i)useIap$') { $flags.useIap = $true; continue }
    $positional.Add($t) | Out-Null
}

if ($flags.buildOnly -and $flags.deployOnly) {
    Write-Error "Choose only one of buildOnly or deployOnly (or neither for build+deploy)."
    exit 1
}

# Day-to-day default when deploying: free disk on VM + GCS upload path.
$deploying = -not $flags.buildOnly
if ($deploying -and -not $flags.freeDisk -and -not $flags.useGcs -and -not $flags.aggressive -and -not $flags.useIap -and $positional.Count -eq 0) {
    $flags.freeDisk = $true
    $flags.useGcs = $true
}

function Get-UnityEditorVersion {
    param([string] $VersionFile)
    if (-not (Test-Path -LiteralPath $VersionFile)) {
        throw "ProjectVersion.txt not found: $VersionFile"
    }
    $line = Get-Content -LiteralPath $VersionFile | Where-Object { $_ -match '^\s*m_EditorVersion:' } | Select-Object -First 1
    if (-not $line -or $line -notmatch 'm_EditorVersion:\s*(\S+)') {
        throw "Could not parse m_EditorVersion from $VersionFile"
    }
    return $Matches[1]
}

function Resolve-UnityEditorPath {
    param([string] $EditorVersion)

    # Env overrides (full path to Unity.exe).
    foreach ($envName in @("TITANORBIT_UNITY_EDITOR", "UNITY_EDITOR_PATH")) {
        $fromEnv = [Environment]::GetEnvironmentVariable($envName)
        if (-not [string]::IsNullOrWhiteSpace($fromEnv) -and (Test-Path -LiteralPath $fromEnv)) {
            return (Resolve-Path -LiteralPath $fromEnv).Path
        }
    }

    $candidates = @(
        (Join-Path ${env:ProgramFiles} "Unity\Hub\Editor\$EditorVersion\Editor\Unity.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "Unity\Hub\Editor\$EditorVersion\Editor\Unity.exe")
    )
    foreach ($c in $candidates) {
        if (Test-Path -LiteralPath $c) {
            return (Resolve-Path -LiteralPath $c).Path
        }
    }

    throw @"
Unity Editor $EditorVersion not found.
Install it via Unity Hub, or set TITANORBIT_UNITY_EDITOR to the full path of Unity.exe.
Tried:
  $($candidates -join "`n  ")
"@
}

function Get-UnityProcessesHoldingProject {
    param([string] $ProjectRoot)

    $normalized = (Resolve-Path -LiteralPath $ProjectRoot).Path.TrimEnd('\')
    $holders = [System.Collections.Generic.List[object]]::new()

    # Prefer WMI command line so we only flag Unity instances on THIS project.
    $procs = Get-CimInstance Win32_Process -Filter "Name = 'Unity.exe'" -ErrorAction SilentlyContinue
    foreach ($p in $procs) {
        $cmd = [string]$p.CommandLine
        if ([string]::IsNullOrWhiteSpace($cmd)) { continue }
        # Skip Unity Hub helper workers naming is fine; any -projectPath match blocks batchmode.
        if ($cmd -like "*$normalized*" -or $cmd -like "*$($normalized.Replace('\', '/'))*") {
            $holders.Add([pscustomobject]@{ Pid = $p.ProcessId; CommandLine = $cmd }) | Out-Null
        }
    }

    return $holders
}

function Assert-LinuxBuildLooksHealthy {
    param([string] $SourceDir)

    if (-not (Test-Path -LiteralPath $SourceDir -PathType Container)) {
        throw "Build output folder missing: $SourceDir"
    }

    $meta = Join-Path $SourceDir "TitanOrbitServer_Data\il2cpp_data\Metadata\global-metadata.dat"
    $gasm = Join-Path $SourceDir "GameAssembly.so"
    if (-not (Test-Path -LiteralPath $meta)) {
        throw "Missing IL2CPP metadata after build: $meta"
    }
    if (-not (Test-Path -LiteralPath $gasm)) {
        throw "Missing GameAssembly.so after build: $gasm"
    }

    $metaSize = (Get-Item -LiteralPath $meta).Length
    $gasmSize = (Get-Item -LiteralPath $gasm).Length
    if ($metaSize -lt 1000000) {
        throw "global-metadata.dat is only $metaSize bytes (need >= 1000000). Rebuild with Unity closed."
    }
    if ($gasmSize -lt 10000000) {
        throw "GameAssembly.so is only $gasmSize bytes (need >= 10000000). Rebuild with Unity closed."
    }
}

Write-Host ""
Write-Host "=== Titan Orbit: build + deploy Linux server (GCE) ==="
Write-Host "Repo root:  $repoRoot"
Write-Host "Mode:       $(if ($flags.buildOnly) { 'buildOnly' } elseif ($flags.deployOnly) { 'deployOnly' } else { 'build + deploy' })"
if ($deploying) {
    Write-Host "Deploy:     freeDisk=$($flags.freeDisk) useGcs=$($flags.useGcs) aggressive=$($flags.aggressive) useIap=$($flags.useIap)"
}
Write-Host ""

# --- [1] Unity batchmode Linux server build ---
if (-not $flags.deployOnly) {
    $unityHolders = @(Get-UnityProcessesHoldingProject -ProjectRoot $repoRoot)
    $lockFile = Join-Path $repoRoot "Temp\UnityLockfile"
    $hasLockFile = Test-Path -LiteralPath $lockFile

    if ($unityHolders.Count -gt 0 -or $hasLockFile) {
        Write-Host "ERROR: Unity still has this project open. Close the Editor, then re-run."
        Write-Host "Batchmode cannot build while the GUI holds the project, and upload must"
        Write-Host "run with Unity closed so IL2CPP files are not truncated."
        Write-Host ""
        if ($unityHolders.Count -gt 0) {
            Write-Host "Running Unity.exe process(es) for this project:"
            foreach ($h in $unityHolders) {
                Write-Host ("  PID {0}" -f $h.Pid)
            }
            Write-Host ""
            Write-Host "Close the Titan Orbit Editor window (File -> Exit), or in Task Manager end those PIDs."
        }
        elseif ($hasLockFile) {
            Write-Host "No Unity.exe found, but Temp\UnityLockfile still exists (stale lock after a crash)."
            Write-Host "If the Editor is truly closed, delete that lockfile and re-run:"
            Write-Host "  $lockFile"
        }
        exit 1
    }

    $editorVersion = Get-UnityEditorVersion -VersionFile $projectVersionPath
    $unityExe = Resolve-UnityEditorPath -EditorVersion $editorVersion
    New-Item -ItemType Directory -Force -Path $logDir | Out-Null
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $logFile = Join-Path $logDir "linux-server-build-$stamp.log"

    Write-Host "--- [1] Unity batchmode Linux Dedicated Server build ---"
    Write-Host "Editor:     $unityExe"
    Write-Host "Version:    $editorVersion"
    Write-Host "Method:     $executeMethod"
    Write-Host "Log:        $logFile"
    Write-Host "Output:     $defaultSourceDir"
    Write-Host ""
    Write-Host "This can take several minutes (IL2CPP). Do not open the project in the Editor until it finishes."
    Write-Host ""

    # Important: do NOT pass -quit. BuildHeadlessServerLinuxBatchMode may switch to Linux
    # Dedicated Server (domain reload) then resume BuildPlayer, and exits via EditorApplication.Exit.
    $unityArgs = @(
        "-batchmode",
        "-nographics",
        "-projectPath", $repoRoot,
        "-executeMethod", $executeMethod,
        "-logFile", $logFile
    )

    $proc = Start-Process -FilePath $unityExe -ArgumentList $unityArgs -Wait -PassThru -NoNewWindow
    $unityExit = $proc.ExitCode
    if ($null -eq $unityExit) { $unityExit = 1 }

    if ($unityExit -ne 0) {
        Write-Host ""
        Write-Host "Unity batchmode exited with code $unityExit. Tail of log:"
        if (Test-Path -LiteralPath $logFile) {
            Get-Content -LiteralPath $logFile -Tail 80
        }
        Write-Error "Linux server build failed (exit $unityExit). Full log: $logFile"
        exit $unityExit
    }

    try {
        Assert-LinuxBuildLooksHealthy -SourceDir $defaultSourceDir
    }
    catch {
        Write-Host ""
        Write-Host "Build reported success but output failed health checks. Tail of log:"
        if (Test-Path -LiteralPath $logFile) {
            Get-Content -LiteralPath $logFile -Tail 40
        }
        throw
    }

    Write-Host ""
    Write-Host "Linux server build OK."
    Write-Host ""
}
else {
    Write-Host "--- Skipping Unity build (deployOnly) ---"
    Assert-LinuxBuildLooksHealthy -SourceDir $defaultSourceDir
    Write-Host ""
}

# --- [2] Deploy to GCE ---
if ($flags.buildOnly) {
    Write-Host "buildOnly set - skipping deploy."
    Write-Host "When ready: .\deploy_server_gce.bat freeDisk useGcs"
    Write-Host ""
    exit 0
}

Write-Host "--- [2] Deploy to GCE ---"
$deployArgs = [System.Collections.Generic.List[string]]::new()
if ($flags.freeDisk) { $deployArgs.Add("freeDisk") | Out-Null }
if ($flags.useGcs) { $deployArgs.Add("useGcs") | Out-Null }
if ($flags.aggressive) { $deployArgs.Add("aggressive") | Out-Null }
if ($flags.useIap) { $deployArgs.Add("useIap") | Out-Null }
foreach ($p in $positional) { $deployArgs.Add($p) | Out-Null }

$pipeline = Join-Path $gceDir "deploy_server_gce_pipeline.ps1"
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $pipeline @deployArgs
$deployExit = $LASTEXITCODE
if ($null -eq $deployExit) { $deployExit = 0 }
if ($deployExit -ne 0) {
    Write-Error "Deploy pipeline failed (exit $deployExit)."
    exit $deployExit
}

Write-Host ""
Write-Host "=== Build + deploy finished successfully ==="
Write-Host ""
exit 0
