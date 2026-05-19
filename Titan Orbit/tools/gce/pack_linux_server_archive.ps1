# Shared pack/verify helpers for Linux headless server deploy.
# Dot-source from upload scripts, or invoke:
#   powershell -NoProfile -File .\invoke_pack_linux_server_archive.ps1 -SourceDir "..." -ArchivePath "%TEMP%\TitanOrbitLinux1.tar.gz"

$ErrorActionPreference = "Stop"

function Get-Il2CppMetadataPath {
    param([string] $Root)
    Join-Path $Root 'TitanOrbitServer_Data\il2cpp_data\Metadata\global-metadata.dat'
}

function Test-LocalIl2CppSourceIntegrity {
    param([string] $Root)
    $checks = @(
        @{ Path = (Join-Path $Root 'TitanOrbitServer'); Min = 1024; Label = 'TitanOrbitServer' },
        @{ Path = (Join-Path $Root 'GameAssembly.so'); Min = 10MB; Label = 'GameAssembly.so' },
        @{ Path = (Join-Path $Root 'UnityPlayer.so'); Min = 5MB; Label = 'UnityPlayer.so' },
        @{ Path = (Get-Il2CppMetadataPath -Root $Root); Min = 1MB; Label = 'global-metadata.dat' }
    )
    $missing = @()
    $bad = @()
    foreach ($c in $checks) {
        if (-not (Test-Path -LiteralPath $c.Path)) {
            $missing += $c.Label
            continue
        }
        $len = (Get-Item -LiteralPath $c.Path).Length
        if ($len -lt $c.Min) {
            $bad += ('{0} ({1:N0} bytes, need >= {2:N0})' -f $c.Label, $len, [long]$c.Min)
        }
    }
    if ($missing.Count -gt 0 -or $bad.Count -gt 0) {
        Write-Host '*** Local build is bad before packing. ***' -ForegroundColor Red
        if ($missing.Count -gt 0) { Write-Host ('  Missing: ' + ($missing -join ', ')) -ForegroundColor Red }
        if ($bad.Count -gt 0) { Write-Host ('  Too small: ' + ($bad -join '; ')) -ForegroundColor Red }
        Write-Host 'Rebuild Linux server in Unity, quit the Editor, then retry.' -ForegroundColor Yellow
        return $false
    }
    return $true
}

function Force-MaterializeFile {
    param([Parameter(Mandatory = $true)][string] $Path)
    $fi = Get-Item -LiteralPath $Path
    $expected = $fi.Length
    if ($expected -lt 1) { throw "Refusing to materialize empty file: $Path" }
    $tmp = "$Path.materialize.$PID.tmp"
    if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Force }
    $inStream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    try {
        $outStream = [System.IO.File]::Open($tmp, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
        try {
            $buf = New-Object byte[] 1048576
            while (($read = $inStream.Read($buf, 0, $buf.Length)) -gt 0) {
                $outStream.Write($buf, 0, $read)
            }
            $outStream.Flush($true)
        }
        finally { $outStream.Dispose() }
    }
    finally { $inStream.Dispose() }
    if ((Get-Item -LiteralPath $tmp).Length -ne $expected) {
        Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
        throw "Materialize size mismatch for $Path"
    }
    Move-Item -LiteralPath $tmp -Destination $Path -Force
    Write-Host ('  materialized {0} ({1:N0} bytes)' -f (Split-Path -Leaf $Path), $expected)
}

function Force-MaterializeIl2CppMetadata {
    param([string] $Root)
    $meta = Get-Il2CppMetadataPath -Root $Root
    if (-not (Test-Path -LiteralPath $meta)) { throw "Missing $meta" }
    Write-Host 'Force-reading global-metadata.dat (avoids Windows tar packing a 0-byte stub)...'
    Force-MaterializeFile -Path $meta
}

function Get-TarListLineUncompressedSizeBytes {
    param([string] $Line)
    if ([string]::IsNullOrWhiteSpace($Line)) { return 0L }
    $rx = [regex]'^[^\s]+\s+(?:(?:\d+/\d+)|(?:\d+\s+\d+)|(?:[^/\s]+/[^/\s]+))\s+(\d+)\s+(?:(?:\d{4}-\d{2}-\d{2})|(?:\w{3}\s))'
    $m = $rx.Match($Line)
    if ($m.Success) { return [long]$m.Groups[1].Value }
    $m2 = [regex]::Match($Line, '\s(\d+)\s+\d{4}-\d{2}-\d{2}\b')
    if ($m2.Success) { return [long]$m2.Groups[1].Value }
    $m3 = [regex]::Match($Line, '\s(\d+)\s+(Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)\s')
    if ($m3.Success) { return [long]$m3.Groups[1].Value }
    return 0L
}

function Test-LinuxServerArchiveIntegrity {
    param([string] $Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $false }
    $out = & tar.exe -tvzf $Path 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host ('tar -tvzf failed (exit ' + $LASTEXITCODE + ')') -ForegroundColor Red
        return $false
    }
    $required = @(
        @{ Pattern = 'il2cpp_data/Metadata/global-metadata\.dat$'; MinBytes = 1MB; Label = 'global-metadata.dat' },
        @{ Pattern = '/GameAssembly\.so$'; MinBytes = 10MB; Label = 'GameAssembly.so' },
        @{ Pattern = '/UnityPlayer\.so$'; MinBytes = 5MB; Label = 'UnityPlayer.so' },
        @{ Pattern = '/TitanOrbitServer$'; MinBytes = 1024; Label = 'TitanOrbitServer' }
    )
    $allOk = $true
    foreach ($req in $required) {
        $line = $out | Where-Object { $_ -match $req.Pattern } | Select-Object -First 1
        if (-not $line) {
            Write-Host ('  [archive] MISSING ' + $req.Label) -ForegroundColor Red
            $allOk = $false
            continue
        }
        $bytes = Get-TarListLineUncompressedSizeBytes -Line $line
        if ($bytes -lt $req.MinBytes) {
            Write-Host ('  [archive] {0} is only {1} bytes (need >= {2}). Raw: {3}' -f $req.Label, $bytes, [long]$req.MinBytes, $line) -ForegroundColor Red
            $allOk = $false
        }
        else {
            Write-Host ('  [archive] OK {0} ({1:N0} bytes)' -f $req.Label, $bytes)
        }
    }
    return $allOk
}

function New-TitanOrbitLinuxServerArchive {
    param(
        [string] $Root,
        [string] $OutPath,
        [int] $Attempts = 3
    )
    $Root = (Resolve-Path -LiteralPath $Root).Path
    $sourceBase = Split-Path -Leaf $Root
    $sourceParent = Split-Path -Parent $Root
    if (-not $OutPath) {
        $OutPath = Join-Path ([System.IO.Path]::GetTempPath()) "$sourceBase.tar.gz"
    }
    $tarExcludes = @(
        "--exclude=${sourceBase}/TitanOrbitServer_BackUpThisFolder_ButDontShipItWithYourGame",
        "--exclude=${sourceBase}/Titan Orbit_BurstDebugInformation_DoNotShip"
    )
    if (-not (Test-LocalIl2CppSourceIntegrity -Root $Root)) {
        throw 'Local IL2CPP source integrity check failed'
    }
    Force-MaterializeIl2CppMetadata -Root $Root
    for ($i = 1; $i -le $Attempts; $i++) {
        if (Test-Path -LiteralPath $OutPath) {
            Remove-Item -LiteralPath $OutPath -Force -ErrorAction SilentlyContinue
        }
        Write-Host ('  pack attempt {0}/{1} ...' -f $i, $Attempts)
        Push-Location $sourceParent
        try {
            & tar.exe @(@('-czf', $OutPath) + $tarExcludes + @($sourceBase))
            if ($LASTEXITCODE -ne 0) {
                Write-Host ('  tar failed (exit ' + $LASTEXITCODE + ')') -ForegroundColor Yellow
            }
        }
        finally { Pop-Location }
        if (Test-LinuxServerArchiveIntegrity -Path $OutPath) {
            return $OutPath
        }
        if ($i -lt $Attempts) {
            Write-Host '  Archive missing IL2CPP bytes; waiting 8s then retrying...' -ForegroundColor Yellow
            Start-Sleep -Seconds 8
            Force-MaterializeIl2CppMetadata -Root $Root
        }
    }
    throw "Could not produce a valid archive after $Attempts attempts"
}
