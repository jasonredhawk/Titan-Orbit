$files = @(
  'https://titanorbit.io/',
)
$html = (Invoke-WebRequest -Uri 'https://titanorbit.io/' -UseBasicParsing).Content
$code = $null; $data = $null; $fw = $null; $loader = $null; $ver = $null
if ($html -match 'productVersion:\s*"([^"]+)"') { $ver = $Matches[1] }
if ($html -match 'codeUrl: buildUrl \+ "/([^"]+)"') { $code = $Matches[1] }
if ($html -match 'dataUrl: buildUrl \+ "/([^"]+)"') { $data = $Matches[1] }
if ($html -match 'frameworkUrl: buildUrl \+ "/([^"]+)"') { $fw = $Matches[1] }
if ($html -match 'loaderUrl = buildUrl \+ "/([^"]+)"') { $loader = $Matches[1] }
Write-Host "productVersion=$ver"
Write-Host "loader=$loader code=$code data=$data fw=$fw"
Write-Host ""
foreach ($rel in @($loader, $fw, $code, $data)) {
  if (-not $rel) { continue }
  $url = "https://titanorbit.io/Build/$rel"
  Write-Host "=== $rel ==="
  & curl.exe -sI $url | Select-String -Pattern 'HTTP/|content-type|content-encoding|content-length|x-goog-stored|cf-cache|age' -CaseSensitive:$false
  Write-Host ""
}
Write-Host "=== GCS direct wasm ==="
& curl.exe -sI "https://storage.googleapis.com/titan-orbit-webgl/Build/$code" | Select-String -Pattern 'HTTP/|content-type|content-encoding|content-length|x-goog-stored' -CaseSensitive:$false
