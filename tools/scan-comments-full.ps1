Set-Location 'c:\Users\jason\Documents\repo\Titan-Orbit'

# In-scope: all gameplay C# under Assets/Scripts + Assets/Editor, excluding generated/meta
$excludePatterns = @(
  '*Tests*',
  '*Test.cs'
)

$allFiles = @()
$allFiles += Get-ChildItem -Path 'Titan Orbit/Assets/Scripts' -Filter '*.cs' -Recurse -ErrorAction SilentlyContinue
$allFiles += Get-ChildItem -Path 'Titan Orbit/Assets/Editor' -Filter '*.cs' -Recurse -ErrorAction SilentlyContinue

$missingSummary = @()
$missingSections = @()
$good = 0
$total = 0

foreach ($f in $allFiles) {
  $skip = $false
  foreach ($pat in $excludePatterns) {
    if ($f.FullName -like $pat) { $skip = $true; break }
  }
  if ($skip) { continue }

  $total++
  $content = Get-Content $f.FullName -Raw -ErrorAction SilentlyContinue
  if (-not $content) { continue }
  $hasSummary = $content -match '(?m)^\s*///\s*<summary>'
  $hasSection = $content -match '// ---'
  $rel = $f.FullName.Replace((Get-Location).Path + '\', '').Replace('\', '/')
  if (-not $hasSummary) { $missingSummary += $rel }
  elseif (-not $hasSection) { $missingSections += $rel }
  else { $good++ }
}

Write-Host "TOTAL IN-SCOPE: $total"
Write-Host "COMPLETE (summary+sections): $good"
Write-Host "PCT: $([math]::Round(100.0 * $good / [math]::Max(1,$total), 1))%"
Write-Host ""
Write-Host "MISSING SUMMARY ($($missingSummary.Count)):"
$missingSummary | ForEach-Object { Write-Host "  $_" }
Write-Host ""
Write-Host "MISSING SECTIONS ($($missingSections.Count)):"
$missingSections | ForEach-Object { Write-Host "  $_" }
