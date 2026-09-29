# Serves the Fast Iterate WebGL folder and opens the default browser. No Python required.
$ErrorActionPreference = "Continue"
$port = 4173
$root = (Resolve-Path (Join-Path $PSScriptRoot "..\Titan Orbit\BuildOutput\WebGL\production\TitanOrbitWebGL")).Path
$index = Join-Path $root "index.html"
$stamp = Join-Path $root "ITERATE_STAMP.txt"

if (-not (Test-Path $index)) {
    Write-Host "Missing $index"
    Write-Host "In Unity: TitanOrbit -> Build -> WebGL Fast Iterate, then run this again."
    exit 1
}
if (-not (Test-Path $stamp)) {
    Write-Host "Missing ITERATE_STAMP.txt. That folder is not a Fast Iterate output."
    exit 1
}

$listener = New-Object System.Net.HttpListener
$prefix = "http://127.0.0.1:$port/"
$listener.Prefixes.Add($prefix)
try {
    $listener.Start()
} catch {
    Write-Host "Port $port is busy. Close the other serve window and try again."
    Write-Host $_.Exception.Message
    exit 1
}

Write-Host ""
Write-Host "Fast Iterate is running."
Write-Host "  Folder: $root"
Write-Host "  URL:    $prefix"
Write-Host "A browser tab should open. Look for a green bar: FAST ITERATE h16"
Write-Host "Leave this window open. Close it when you are done."
Write-Host ""

Start-Process $prefix

$serveOne = {
    param($listener, $root)
    $mime = @{
        ".html" = "text/html; charset=utf-8"
        ".js"   = "application/javascript"
        ".wasm" = "application/wasm"
        ".data" = "application/octet-stream"
        ".json" = "application/json"
        ".css"  = "text/css"
        ".png"  = "image/png"
        ".jpg"  = "image/jpeg"
        ".ico"  = "image/x-icon"
        ".txt"  = "text/plain"
        ".svg"  = "image/svg+xml"
    }
    while ($listener.IsListening) {
        try {
            $ctx = $listener.GetContext()
            $rel = [Uri]::UnescapeDataString($ctx.Request.Url.AbsolutePath.TrimStart("/"))
            if ([string]::IsNullOrWhiteSpace($rel)) { $rel = "index.html" }
            $full = [System.IO.Path]::GetFullPath((Join-Path $root $rel))
            $inside = $full.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)
            if (-not $inside -or -not (Test-Path -LiteralPath $full -PathType Leaf)) {
                $ctx.Response.StatusCode = 404
                $ctx.Response.Close()
                continue
            }
            $ext = [System.IO.Path]::GetExtension($full).ToLowerInvariant()
            if ($mime.ContainsKey($ext)) {
                $ctx.Response.ContentType = $mime[$ext]
            } else {
                $ctx.Response.ContentType = "application/octet-stream"
            }
            $ctx.Response.AddHeader("Cache-Control", "no-store")
            $fs = [System.IO.File]::OpenRead($full)
            try {
                $ctx.Response.ContentLength64 = $fs.Length
                $fs.CopyTo($ctx.Response.OutputStream)
            } finally {
                $fs.Dispose()
                try { $ctx.Response.Close() } catch {}
            }
        } catch {
            Start-Sleep -Milliseconds 50
        }
    }
}

$workers = @()
1..4 | ForEach-Object {
    $ps = [PowerShell]::Create()
    [void]$ps.AddScript($serveOne).AddArgument($listener).AddArgument($root)
    $workers += @{ ps = $ps; handle = $ps.BeginInvoke() }
}

try {
    while ($listener.IsListening) {
        Start-Sleep -Seconds 1
    }
} finally {
    foreach ($w in $workers) {
        try { $w.ps.Stop() } catch {}
        try { $w.ps.Dispose() } catch {}
    }
    try { $listener.Stop() } catch {}
}
