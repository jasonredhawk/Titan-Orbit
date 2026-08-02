# Determines whether a Unity WebGL artifact is pre-compressed (Brotli) on disk.
# Output: "br", "gzip", or "" (empty = no Content-Encoding header on GCS).
# Always ends with a single pipeline string and NO trailing CR — batch for /f used to
# capture "br`r" from Write-Output CRLF, fail `if ENC==br`, and clear Content-Encoding.
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

function Emit([string]$value) {
    # --- Pipeline-only, no CR ---
    # [STANDARD] Write-Output can be CRLF on Windows; consumers must Trim().
    # We still Write-Output so `& script` / Out-String capture the value (Console.Out
    # would print "br" but leave the pipeline empty — that cleared GCS encoding).
    Write-Output $value
    exit 0
}

# Plain loader / template assets are never pre-compressed.
if ($ext -eq ".js" -and $name -notlike "*.unityweb") {
    Emit ""
}
if ($ext -in ".html", ".css", ".ico", ".png", ".json") {
    Emit ""
}

# Explicit brotli sidecar from older Unity builds.
if ($ext -eq ".br" -or $name.EndsWith(".br")) {
    Emit "br"
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
    Emit ""
}

# Uncompressed WASM magic \0asm
if ($bytes[0] -eq 0 -and $bytes[1] -eq 0x61 -and $bytes[2] -eq 0x73 -and $bytes[3] -eq 0x6d) {
    Emit ""
}

# Uncompressed JavaScript / JSON text at file start
$textLen = [Math]::Min($read, 16)
$text = [Text.Encoding]::UTF8.GetString($bytes, 0, $textLen)
if ($text -match '^(var\s|\(function|/\*|//|import\s|export\s|\{)') {
    Emit ""
}

# UnityWebData marker (uncompressed .data)
if ($text -match '^UnityWeb') {
    Emit ""
}

# Gzip magic — do not label as br
if ($bytes[0] -eq 0x1f -and $bytes[1] -eq 0x8b) {
    Emit "gzip"
}

# .unityweb and other binary build blobs from Brotli builds are pre-compressed.
Emit "br"
