# Preflight check before upload: required Build files and Brotli vs plain on disk.
param(
    [string]$SourceDir = "C:\Users\jason\Documents\repo\Titan-Orbit\Titan Orbit\BuildOutput\WebGL\production\TitanOrbitWebGL"
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$encodingScript = Join-Path $scriptDir "Get-WebGLEncoding.ps1"

if (-not (Test-Path -LiteralPath (Join-Path $SourceDir "index.html"))) {
    Write-Error "Missing index.html under: $SourceDir"
    exit 1
}

$buildDir = Join-Path $SourceDir "Build"
if (-not (Test-Path -LiteralPath $buildDir)) {
    Write-Error "Missing Build\ folder under: $SourceDir"
    exit 1
}

Write-Host "WebGL build folder: $SourceDir"
Write-Host ""

$artifacts = Get-ChildItem -LiteralPath $buildDir -File | Sort-Object Name
if ($artifacts.Count -eq 0) {
    Write-Error "Build\ is empty."
    exit 1
}

$loader = $artifacts | Where-Object { $_.Name -like "*.loader.js" } | Select-Object -First 1
$framework = $artifacts | Where-Object { $_.Name -like "*.framework.js*" } | Select-Object -First 1
$wasm = $artifacts | Where-Object { $_.Name -like "*.wasm*" -and $_.Name -notlike "*.symbols*" } | Select-Object -First 1
$data = $artifacts | Where-Object { $_.Name -like "*.data*" } | Select-Object -First 1

foreach ($pair in @(
        @{ Label = "loader"; File = $loader },
        @{ Label = "framework"; File = $framework },
        @{ Label = "wasm"; File = $wasm },
        @{ Label = "data"; File = $data }
    )) {
    if (-not $pair.File) {
        Write-Warning "Missing expected $($pair.Label) artifact in Build\"
        continue
    }
    $enc = & $encodingScript -FilePath $pair.File.FullName
    $encLabel = if ([string]::IsNullOrEmpty($enc)) { "(none - uncompressed on disk)" } else { $enc }
    $sizeMb = [Math]::Round($pair.File.Length / 1MB, 2)
    Write-Host ("{0,-10} {1,-42} {2,8} MB  encoding={3}" -f $pair.Label, $pair.File.Name, $sizeMb, $encLabel)
}

Write-Host ""
Write-Host "All Build\ files:"
foreach ($f in $artifacts) {
    $enc = & $encodingScript -FilePath $f.FullName
    $encLabel = if ([string]::IsNullOrEmpty($enc)) { "-" } else { $enc }
    Write-Host ("  {0,-44} {1,8} MB  {2}" -f $f.Name, [Math]::Round($f.Length / 1MB, 2), $encLabel)
}

Write-Host ""
Write-Host "After upload, run set_webgl_gcs_metadata.bat (or deploy_webgl_gcs.bat) so GCS Content-Encoding matches the encoding column."
Write-Host "Then purge Cloudflare cache (if used) and clear site data for titanorbit.io in the browser."
