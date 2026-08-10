#Requires -Version 5.1
<#
.SYNOPSIS
  Static guard against Titan Orbit Windows late-join Crash!!! regressions (seed-hydrate era).

.DESCRIPTION
  Scans client-facing C# for patterns that historically cause native Crash!!! after Join Team,
  and for regressions that reintroduce full-map asteroid GhostSpawn Instantiates floods.

  Run before every Windows client build:
    powershell -File tools/verify-join-crash-gates.ps1

  Exit codes:
    0 = no high-severity findings
    1 = high-severity findings (do not ship Windows client)
    2 = script / path error

  Paired: Titan Orbit/tools/netcode-patches/JOIN-CRASH-VERIFY.md
#>

[CmdletBinding()]
param(
    [string]$RepoRoot = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = Split-Path -Parent $PSScriptRoot
}
$AssetsScripts = Join-Path $RepoRoot "Titan Orbit\Assets\Scripts"
if (-not (Test-Path -LiteralPath $AssetsScripts)) {
    Write-Error "Assets/Scripts not found at: $AssetsScripts"
    exit 2
}

Write-Host "=== Titan Orbit join-crash gate verifier (seed-hydrate) ==="
Write-Host "Scanning: $AssetsScripts"
Write-Host ""

$high = New-Object System.Collections.Generic.List[string]
$warn = New-Object System.Collections.Generic.List[string]
function Add-High([string]$msg) { [void]$high.Add($msg) }
function Add-Warn([string]$msg) { [void]$warn.Add($msg) }

function Get-Rel([string]$full) {
    return $full.Replace($RepoRoot, "").TrimStart("\", "/")
}

function Test-IsLikelyServerOnly([string]$text) {
    $hasServer = $text -match 'WorldSystemFilterFlags\.ServerSimulation'
    $hasClient = $text -match 'WorldSystemFilterFlags\.ClientSimulation|WorldSystemFilterFlags\.ClientPresentation|IsClient\('
    return ($hasServer -and -not $hasClient)
}

function Test-HasShipGate([string]$text) {
    return $text -match 'ShouldSkipShipEntityQueries|GhostSpawnBacklog|ArmPostTeamChoiceHold|TransformQuarantine'
}
function Test-HasMapGate([string]$text) {
    return $text -match 'ShouldSkipMapBodyQueries|TransformQuarantine|ClientSeedHydratedMapBody'
}

$clientRoots = @(
    (Join-Path $AssetsScripts "Game"),
    (Join-Path $AssetsScripts "UI"),
    (Join-Path $AssetsScripts "Camera"),
    (Join-Path $AssetsScripts "ECS")
) | Where-Object { Test-Path -LiteralPath $_ }

$csFiles = foreach ($root in $clientRoots) {
    Get-ChildItem -LiteralPath $root -Recurse -Filter *.cs -File
}

# --- Pass 1: FORBIDDEN mid-play Transform RE-ENABLE paths that fight seed-hydrate comments ---
# Seed-hydrate intentionally enables TransformSystemGroup. Flag only "RE-ENABLE" crash comments
# paired with Enabled=true outside the join gate, or explicit quarantine re-enable anti-patterns.
foreach ($f in $csFiles) {
    $text = [System.IO.File]::ReadAllText($f.FullName)
    $rel = Get-Rel $f.FullName
    if ($rel -match 'TitanOrbitClientJoinTransformGateSystem') { continue }
    if ($text -match 'RE-ENABLE' -and $text -match 'TransformSystemGroup' -and $text -match '\.Enabled\s*=\s*true') {
        Add-High ("Suspicious TransformSystemGroup RE-ENABLE outside join gate: {0}" -f $rel)
    }
}

# --- Pass 2: Settling-only early-outs ---
foreach ($f in $csFiles) {
    $rel = Get-Rel $f.FullName
    $text = [System.IO.File]::ReadAllText($f.FullName)
    $compact = [regex]::Replace($text, '\s+', ' ')

    $settlingOnly = [regex]::IsMatch(
        $compact,
        'if\s*\(\s*(?:state\.World\.IsClient\(\)\s*&&\s*)?ClientJoinSettleCache\.Settling\s*\)\s*return')
    if ($settlingOnly) {
        if (Test-HasShipGate $text -or Test-HasMapGate $text) {
            Add-Warn ("Settling-only early-out present; prefer ShouldSkip* helpers for new code: {0}" -f $rel)
        }
        else {
            Add-High ("Settling-only early-out with no backlog/quarantine gate: {0}" -f $rel)
        }
    }
}

# --- Pass 3: client gather APIs without any recognized gate ---
$gatherPattern = 'ToEntityArray|WithEntityAccess|ToComponentDataArray'

$exemptBaseNames = @(
    'AsteroidGhostAuthoring',
    'PlanetGhostAuthoring',
    'MapBodyHybridVisualRequestSystem',
    'MapBodyHybridVisualInstantiateHook',
    'GemClientEntityRegistry',
    'PlanetClientEntityRegistry',
    'ShipMoonDockAttachLogic',
    'PlanetConnectionComponents',
    'TitanOrbitClientJoinTransformGateSystem',
    'TitanOrbitGhostSpawnBacklogRefreshSystem',
    'TeamChoiceResultClientSystem',
    'RejoinShipResultClientSystem',
    'MoonOrbitRpcClientSystem',
    'PeopleTransportSpawnRpcClientSystem',
    'PeopleTransportPoseRpcClientSystem',
    'BulletHitRpcClientSystem',
    'BulletSpawnRpcClientSystem',
    'AsteroidRespawnRpcClientSystem',
    'ClientLocalMapBodySpawn',
    'ClientMapHydrateSystem',
    'MapLayoutBlueprint',
    'ShipHullColliderLogic',
    'ShipAttributeUpgradeLogic',
    'ShipHomeSpawnLogic',
    'PlanetMotorSnapshot',
    'PlanetOwnershipNetNotify',
    'PeopleTransportSystem'
)

foreach ($f in $csFiles) {
    $rel = Get-Rel $f.FullName
    $base = $f.BaseName
    if ($exemptBaseNames -contains $base) { continue }

    $text = [System.IO.File]::ReadAllText($f.FullName)
    if (Test-IsLikelyServerOnly $text) { continue }
    if ($text -notmatch $gatherPattern) { continue }

    $looksShip = $text -match 'ShipTag|ShipState|EnsureShipProxies|LocalPlayerShip'
    $looksMap = $text -match 'AsteroidTag|AsteroidState|PlanetTag|PlanetState|GemTag|GemState|MapBodyHybrid'

    $hasShipGate = Test-HasShipGate $text
    $hasMapGate = Test-HasMapGate $text

    if ($looksShip -and -not $hasShipGate) {
        Add-High ("Ship-oriented gather with no ship gate (need ShouldSkipShipEntityQueries or GhostSpawnBacklog): {0}" -f $rel)
    }
    if ($looksMap -and -not $hasMapGate) {
        if ($text -match 'CreateEntityQuery\([^\)]*(Asteroid|Planet|Gem|Moon)' -or
            $text -match 'WithAll<\s*(Asteroid|Planet|Gem)' -or
            $text -match 'Query<[^>]*(AsteroidState|PlanetState|GemState)') {
            Add-High ("Map-body gather with no map gate (need ShouldSkipMapBodyQueries or TransformQuarantine): {0}" -f $rel)
        }
        else {
            Add-Warn ("Map types + gather API; confirm quarantine gate: {0}" -f $rel)
        }
    }
    elseif (-not $hasShipGate -and -not $hasMapGate) {
        Add-Warn ("Gather API with no ShouldSkip*/quarantine/backlog gate (review if client-hot): {0}" -f $rel)
    }
}

# --- Pass 4: prefer helper APIs over hand-rolled (warn only) ---
foreach ($f in $csFiles) {
    $rel = Get-Rel $f.FullName
    $text = [System.IO.File]::ReadAllText($f.FullName)
    if ($text -match 'GhostSpawnBacklog' -and $text -notmatch 'ShouldSkipShipEntityQueries') {
        if ($text -match 'ToEntityArray|WithEntityAccess|ToComponentDataArray') {
            Add-Warn ("Hand-rolled GhostSpawnBacklog gate - prefer ShouldSkipShipEntityQueries (folds TeamChoice hold): {0}" -f $rel)
        }
    }
}

# --- Pass 5: GhostSpawn patch + asteroid relevancy regression ---
$ghostSpawnCandidates = @(
    (Join-Path $RepoRoot "Titan Orbit\Packages\com.unity.netcode\Runtime\Snapshot\GhostSpawnSystem.cs"),
    (Join-Path $RepoRoot "Packages\com.unity.netcode\Runtime\Snapshot\GhostSpawnSystem.cs"),
    (Join-Path $RepoRoot "tools\netcode-patches\GhostSpawnSystem.cs"),
    (Join-Path $RepoRoot "Titan Orbit\tools\netcode-patches\GhostSpawnSystem.cs")
) | Where-Object { Test-Path -LiteralPath $_ }

foreach ($gs in $ghostSpawnCandidates) {
    $text = [System.IO.File]::ReadAllText($gs)
    $rel = Get-Rel $gs
    if ($text -notmatch 'TO_GhostSpawn_v1[3-9]|TO_GhostSpawn_v[2-9]\d') {
        Add-Warn ("GhostSpawn patch id may be older than v13: {0}" -f $rel)
    }
}

$relevancyPath = Join-Path $RepoRoot "Titan Orbit\Assets\Scripts\NetCode\TitanOrbitDynamicGhostRelevancySystem.cs"
if (-not (Test-Path -LiteralPath $relevancyPath)) {
    Add-High "Missing TitanOrbitDynamicGhostRelevancySystem.cs (asteroids would stream again)."
}
else {
    $relText = [System.IO.File]::ReadAllText($relevancyPath)
    if ($relText -notmatch 'SetIsRelevant') {
        Add-High "Ghost relevancy system must use SetIsRelevant so asteroids are not streamed."
    }
    if ($relText -match 'AsteroidTag') {
        Add-High "Do not include AsteroidTag in DefaultRelevancyQuery (reintroduces Instantiates flood)."
    }
    if ($relText -notmatch 'ClientMapHydrate') {
        Add-Warn "Relevancy system should mention ClientMapHydrate in comments for agents."
    }
}

$hydratePath = Join-Path $AssetsScripts "ECS\Systems\ClientMapHydrateSystem.cs"
if (-not (Test-Path -LiteralPath $hydratePath)) {
    Add-High "Missing ClientMapHydrateSystem.cs (seed-hydrate join required)."
}

# --- Report ---
Write-Host "--- Warnings ($($warn.Count)) ---"
if ($warn.Count -eq 0) { Write-Host "(none)" }
else { $warn | ForEach-Object { Write-Host "  WARN  $_" } }

Write-Host ""
Write-Host "--- High severity ($($high.Count)) ---"
if ($high.Count -eq 0) { Write-Host "(none)" }
else { $high | ForEach-Object { Write-Host "  HIGH  $_" } }

Write-Host ""
if ($high.Count -gt 0) {
    Write-Host "FAIL: Fix high findings before building a Windows client."
    Write-Host "Seed-hydrate join: asteroids local from recipe; ships still use ShouldSkipShipEntityQueries."
    exit 1
}

Write-Host "PASS: No high-severity join-crash gate regressions detected."
Write-Host "Still required: Windows Relay late-join smoke test (Join Team -> no Crash!!!)."
exit 0
