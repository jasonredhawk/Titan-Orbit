Set-Location 'c:\Users\jason\Documents\repo\Titan-Orbit'
$dirs = @(
  'Titan Orbit/Assets/Scripts/Game',
  'Titan Orbit/Assets/Scripts/Data',
  'Titan Orbit/Assets/Scripts/UI',
  'Titan Orbit/Assets/Scripts/Core',
  'Titan Orbit/Assets/Scripts/Editor',
  'Titan Orbit/Assets/Scripts/ECS/Editor',
  'Titan Orbit/Assets/Scripts/NetCode',
  'Titan Orbit/Assets/Scripts/Simulation',
  'Titan Orbit/Assets/Editor'
)
$missingSummary = @()
$missingSections = @()
$total = 0
$good = 0
foreach ($dir in $dirs) {
  if (-not (Test-Path $dir)) { continue }
  Get-ChildItem -Path $dir -Filter '*.cs' -Recurse | ForEach-Object {
    $total++
    $content = Get-Content $_.FullName -Raw -ErrorAction SilentlyContinue
    if (-not $content) { return }
    $hasSummary = $content -match '(?m)^\s*///\s*<summary>'
    $hasSection = $content -match '// ---'
    $rel = $_.FullName.Replace((Get-Location).Path + '\', '').Replace('\', '/')
    if (-not $hasSummary) { $missingSummary += $rel }
    elseif (-not $hasSection) { $missingSections += $rel }
    else { $good++ }
  }
}
Write-Host "TOTAL: $total"
Write-Host "GOOD: $good"
Write-Host "MISSING SUMMARY ($($missingSummary.Count)):"
$missingSummary | ForEach-Object { Write-Host "  $_" }
Write-Host "MISSING SECTIONS ($($missingSections.Count)):"
$missingSections | ForEach-Object { Write-Host "  $_" }
