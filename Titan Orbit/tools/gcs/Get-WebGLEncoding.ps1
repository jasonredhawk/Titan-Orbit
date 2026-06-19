# Determines whether a Unity WebGL artifact is pre-compressed (Brotli) on disk.
# Output: "br" or "" (empty = no Content-Encoding header on GCS)
param(
    [Parameter(Mandatory = $true)]
    [string]$FilePath
)

if (-not (Test-Path -LiteralPath $FilePath)) {
    Write-Error "File not found: $FilePath"
    exit 2
}

$name = [IO.Path]::GetFileName($FilePath)
$ext = [IO.Path]::GetExtension($FilePath).ToLowerInvariant()

# Plain loader / template assets are never pre-compressed.
if ($ext -eq ".js" -and $name -notlike "*.unityweb") {
    Write-Output ""
    exit 0
}
if ($ext -in ".html", ".css", ".ico", ".png", ".json") {
    Write-Output ""
    exit 0
}

# Explicit brotli sidecar from older Unity builds.
if ($ext -eq ".br" -or $name.EndsWith(".br")) {
    Write-Output "br"
    exit 0
}

$bytes = New-Object byte[] 16
$fs = [IO.File]::OpenRead($FilePath)
try {
    $read = $fs.Read($bytes, 0, $bytes.Length)
}
finally {
    $fs.Close()
}

if ($read -lt 4) {
    Write-Output ""
    exit 0
}

# Uncompressed WASM magic \0asm
if ($bytes[0] -eq 0 -and $bytes[1] -eq 0x61 -and $bytes[2] -eq 0x73 -and $bytes[3] -eq 0x6d) {
    Write-Output ""
    exit 0
}

# Uncompressed JavaScript / JSON text at file start
$textLen = [Math]::Min($read, 16)
$text = [Text.Encoding]::UTF8.GetString($bytes, 0, $textLen)
if ($text -match '^(var\s|\(function|/\*|//|import\s|export\s|\{)') {
    Write-Output ""
    exit 0
}

# UnityWebData marker (uncompressed .data)
if ($text -match '^UnityWeb') {
    Write-Output ""
    exit 0
}

# Gzip magic — do not label as br
if ($bytes[0] -eq 0x1f -and $bytes[1] -eq 0x8b) {
    Write-Output "gzip"
    exit 0
}

# .unityweb and other binary build blobs from Brotli builds are pre-compressed.
Write-Output "br"
exit 0
