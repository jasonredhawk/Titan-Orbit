# Sets Content-Type / Content-Encoding on GCS objects to match the local WebGL build tree.
# Prefer this over set_webgl_gcs_metadata.bat — batch for /f often leaves a trailing CR on
# encoding detection ("br`r"), so the == br check fails and --clear-content-encoding runs.
# That ships Brotli bytes without Content-Encoding → WASM "memory access out of bounds" at _main.
param(
    [string]$SourceDir = "C:\Users\jason\Documents\repo\Titan-Orbit\Titan Orbit\BuildOutput\WebGL\production\TitanOrbitWebGL",
    [string]$Bucket = "titan-orbit-webgl",
    [string]$ProjectId = "titan-orbit"
)

$ErrorActionPreference = "Stop"
$encodingScript = Join-Path $PSScriptRoot "Get-WebGLEncoding.ps1"
if (-not (Test-Path -LiteralPath $encodingScript)) {
    throw "Missing $encodingScript"
}
if (-not (Test-Path -LiteralPath (Join-Path $SourceDir "index.html"))) {
    throw "Source folder invalid or missing index.html: $SourceDir"
}

$srcBase = (Resolve-Path -LiteralPath $SourceDir).Path.TrimEnd('\')
Write-Host "Setting metadata for gs://$Bucket/ (project: $ProjectId)"
Write-Host "Local tree: $srcBase"
Write-Host ""

& (Join-Path $PSScriptRoot "verify_webgl_build.ps1") $srcBase
Write-Host ""

$failed = $false
$files = Get-ChildItem -LiteralPath $srcBase -Recurse -File

foreach ($file in $files) {
    $rel = $file.FullName.Substring($srcBase.Length).TrimStart('\').Replace('\', '/')
    $gsUri = "gs://$Bucket/$rel"
    $name = $file.Name
    $ext = $file.Extension.ToLowerInvariant()

    $contentType = $null
    switch ($ext) {
        ".html" { $contentType = "text/html" }
        ".css"  { $contentType = "text/css" }
        ".ico"  { $contentType = "image/x-icon" }
        ".png"  { $contentType = "image/png" }
        ".json" { $contentType = "application/json" }
        ".js"   { $contentType = "application/javascript" }
        ".wasm" { $contentType = "application/wasm" }
        ".data" { $contentType = "application/octet-stream" }
        ".unityweb" { $contentType = "application/octet-stream" }
        ".br"   { $contentType = "application/octet-stream" }
    }

    if ($name -like "*.wasm.unityweb" -or $name -like "*.wasm.br") { $contentType = "application/wasm" }
    if ($name -like "*.json.unityweb" -or $name -like "*.json.br") { $contentType = "application/json" }
    if ($name -like "*.js.unityweb" -or $name -like "*.framework.js*" -or $name -like "*.js.br") {
        $contentType = "application/javascript"
    }
    if ($name -like "*.data.unityweb" -or $name -like "*.data.br") {
        $contentType = "application/octet-stream"
    }

    if ([string]::IsNullOrEmpty($contentType)) {
        continue
    }

    # --- Detect encoding (trim CR/LF — batch for /f used to leave `br` + CR) ---
    $encoding = ((& $encodingScript -FilePath $file.FullName) | Out-String).Trim()
    if ($encoding -match '^(br|gzip)$') {
        $encoding = $Matches[1]
    }
    else {
        $encoding = ""
    }

    $args = @(
        "--project", $ProjectId,
        "storage", "objects", "update", $gsUri,
        "--content-type=$contentType",
        "--cache-control=no-cache"
    )
    if ($encoding -eq "br" -or $encoding -eq "gzip") {
        $args += "--content-encoding=$encoding"
    }
    else {
        $args += "--clear-content-encoding"
    }
    $encLabel = if ($encoding) { $encoding } else { "(none)" }

    Write-Host ("UPDATE {0,-55} type={1,-24} enc={2}" -f $rel, $contentType, $encLabel)
    & gcloud @args
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  FAIL gcloud exit $LASTEXITCODE"
        $failed = $true
        continue
    }

    # --- Hard fail if a Brotli file still lacks contentEncoding on GCS ---
    if ($encoding -eq "br" -or $encoding -eq "gzip") {
        $metaJson = gcloud --project $ProjectId storage objects describe $gsUri --format=json 2>$null
        if (-not $metaJson) {
            Write-Host "  FAIL describe missing after update"
            $failed = $true
            continue
        }
        $meta = $metaJson | ConvertFrom-Json
        # gcloud storage describe JSON uses snake_case (content_encoding), not contentEncoding.
        $got = [string]($meta.content_encoding)
        if ([string]::IsNullOrEmpty($got)) { $got = [string]($meta.contentEncoding) }
        if ($got -ne $encoding) {
            Write-Host "  FAIL content_encoding on GCS is '$got' (expected '$encoding')"
            $failed = $true
        }
    }
}

if ($failed) {
    Write-Host ""
    Write-Host "Completed with one or more errors."
    exit 1
}

Write-Host ""
Write-Host "Metadata pass complete."
exit 0
