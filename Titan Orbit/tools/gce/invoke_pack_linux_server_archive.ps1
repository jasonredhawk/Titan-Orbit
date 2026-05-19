param(
    [Parameter(Mandatory = $true)]
    [string] $SourceDir,
    [string] $ArchivePath = "",
    [int] $PackAttempts = 3
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "pack_linux_server_archive.ps1")
$SourceDir = (Resolve-Path -LiteralPath $SourceDir).Path
try {
    $result = New-TitanOrbitLinuxServerArchive -Root $SourceDir -OutPath $ArchivePath -Attempts $PackAttempts
    Write-Host ('Archive OK: ' + $result)
    Write-Output $result
}
catch {
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}
