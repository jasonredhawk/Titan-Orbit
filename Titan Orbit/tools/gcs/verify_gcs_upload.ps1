# Compare local WebGL build artifacts with objects in GCS (and optionally the public URL).
param(
    [string]$SourceDir = "C:\Users\jason\Documents\repo\Titan-Orbit\Titan Orbit\BuildOutput\WebGL\production\TitanOrbitWebGL",
    [string]$Bucket = "titan-orbit-webgl",
    [string]$ProjectId = "titan-orbit",
    [string]$PublicOrigin = "https://titanorbit.io",
    [switch]$CheckPublicUrl
)

$ErrorActionPreference = "Stop"
$buildDir = Join-Path $SourceDir "Build"
if (-not (Test-Path -LiteralPath $buildDir)) {
    Write-Error "Missing Build\ under: $SourceDir"
    exit 1
}

Write-Host "Verifying gs://$Bucket/ against local build..."
Write-Host "Local: $SourceDir"
Write-Host ""

$failed = $false
$artifacts = Get-ChildItem -LiteralPath $buildDir -File | Sort-Object Name

foreach ($file in $artifacts) {
    $rel = "Build/$($file.Name)"
    $gsUri = "gs://$Bucket/$rel"
    $localSize = $file.Length

    $metaJson = gcloud --project $ProjectId storage objects describe $gsUri --format=json 2>$null
    if (-not $metaJson) {
        Write-Host "FAIL  $rel - missing on GCS"
        $failed = $true
        continue
    }

    $meta = $metaJson | ConvertFrom-Json
    $gcsSize = [int64]$meta.size
    $encoding = $meta.content_encoding
    $sizeOk = ($gcsSize -eq $localSize)

    $encodingScript = Join-Path $PSScriptRoot "Get-WebGLEncoding.ps1"
    $expectedEnc = ""
    if (Test-Path -LiteralPath $encodingScript) {
        $expectedEnc = & $encodingScript -FilePath $file.FullName
    }
    $encOk = if ([string]::IsNullOrEmpty($expectedEnc)) {
        [string]::IsNullOrEmpty($encoding)
    } else {
        $encoding -eq $expectedEnc
    }

    $status = if ($sizeOk -and $encOk) { "OK" } else { "FAIL" }
    if ($status -eq "FAIL") { $failed = $true }

    $encLabel = if ([string]::IsNullOrEmpty($encoding)) { "(none)" } else { $encoding }
    $expectedLabel = if ([string]::IsNullOrEmpty($expectedEnc)) { "(none)" } else { $expectedEnc }
    Write-Host ("{0}  {1}" -f $status, $rel)
    Write-Host ("     local={0:N0}  gcs={1:N0}  sizeMatch={2}" -f $localSize, $gcsSize, $sizeOk)
    Write-Host ("     content_encoding={0}  expected={1}  encMatch={2}" -f $encLabel, $expectedLabel, $encOk)

    if ($CheckPublicUrl -and $PublicOrigin) {
        $url = "$PublicOrigin/Build/$($file.Name)"
        try {
            $resp = Invoke-WebRequest -Uri $url -Method Head -TimeoutSec 30 -UseBasicParsing
            $publicLen = $resp.Headers["Content-Length"]
            if ($publicLen -and [int64]$publicLen -ne $localSize) {
                Write-Host "     PUBLIC CACHE MISMATCH: $url reports $publicLen bytes (expected $localSize). Purge Cloudflare cache."
                $failed = $true
            } else {
                Write-Host "     public url size ok (or chunked)"
            }
        } catch {
            Write-Host "     public url check failed: $($_.Exception.Message)"
        }
    }
    Write-Host ""
}

if ($failed) {
    Write-Host "Verification FAILED."
    Write-Host "If GCS sizes match but the site still hangs:"
    Write-Host "  1. Run set_webgl_gcs_metadata.bat (fixes Content-Encoding: br on .unityweb)"
    Write-Host "  2. Purge Cloudflare cache for titanorbit.io (Build/* often stays stale)"
    Write-Host "  3. Clear site data / IndexedDB for titanorbit.io in the browser"
    exit 1
}

Write-Host "Verification OK - GCS objects match local build."
if ($CheckPublicUrl) {
    Write-Host "If the site still hangs, purge Cloudflare cache and clear browser site data."
}
exit 0
