# Creates C:\Users\<you>\.ssh\google_compute_engine (+ .pub) if missing, copies a VM-ready PUBLIC key line to the clipboard,
# and prints where to paste it in Google Cloud Console. Use when "gcloud compute ssh" fails on Windows (PuTTY plink popup).

param(
    [string] $LinuxUser = "jason"
)

$ErrorActionPreference = "Stop"
$sshDir = Join-Path $env:USERPROFILE ".ssh"
$priv = Join-Path $sshDir "google_compute_engine"
$pub = "$priv.pub"

if (-not (Get-Command ssh-keygen.exe -ErrorAction SilentlyContinue)) {
    Write-Error "ssh-keygen.exe not found. Install OpenSSH Client (Windows optional features), then reopen PowerShell."
    exit 1
}

New-Item -ItemType Directory -Path $sshDir -Force | Out-Null

if (Test-Path $priv) {
    Write-Host "Already exists: $priv"
    Write-Host "If the VM still rejects SSH, add or refresh the public key in Cloud Console (see README)."
}
else {
    Write-Host "Creating new key pair (no passphrase): $priv"
    $argList = @(
        "-t", "ed25519",
        "-f", $priv,
        "-q",
        "-N", [string]::Empty,
        "-C", "windows-gce-$($env:USERNAME)"
    )
    & ssh-keygen.exe @argList
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

$pubLine = (Get-Content -LiteralPath $pub -TotalCount 1 | Select-Object -First 1).Trim()
if (-not $pubLine) {
    Write-Error "Could not read public key from $pub"
    exit 1
}

# Google Cloud expects "linux_username:ssh-ed25519 AAAA... comment"
$clipboardLine = if ($pubLine.StartsWith("${LinuxUser}:")) { $pubLine } else { "${LinuxUser}:$pubLine" }
Set-Clipboard -Value $clipboardLine

Write-Host ""
Write-Host "Copied to clipboard (one line). It should start with: ${LinuxUser}:ssh-ed25519"
Write-Host ""
Write-Host "Paste it in Google Cloud Console (pick ONE):"
Write-Host "  A) Whole project: Compute Engine -> Metadata -> SSH Keys -> EDIT -> Add item -> paste -> Save."
Write-Host "  B) One VM only: Compute Engine -> VM instances -> your VM -> EDIT -> SSH Keys -> Add item -> paste -> Save."
Write-Host ""
Write-Host "If your Linux login is not '$LinuxUser', re-run:"
Write-Host "  powershell -NoProfile -File `"$PSScriptRoot\create_local_gce_ssh_key.ps1`" -LinuxUser your_linux_name"
Write-Host ""
Write-Host "Then run from tools\gce: install_enable_server_service_on_gce_iap.bat"
Write-Host ""
Write-Host "Tightening key ACLs for Windows OpenSSH (ssh.exe) ..."
$fixAcl = Join-Path $PSScriptRoot "fix_google_compute_engine_key_acl.ps1"
$psHost = Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"
# Nested -File breaks when repo path has spaces (e.g. Titan Orbit); use -Command with single-quoted paths.
# Use string "'" for Replace so .NET picks Replace(string,string), not Replace(char,char).
$fixLit = "'" + ($fixAcl.Replace("'", "''")) + "'"
$keyLit = "'" + ($priv.Replace("'", "''")) + "'"
$cmd = "& $fixLit -KeyPath $keyLit"
$aclProc = Start-Process -FilePath $psHost -ArgumentList @(
    "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", $cmd
) -Wait -PassThru -NoNewWindow
if ($aclProc.ExitCode -ne 0) {
    exit $aclProc.ExitCode
}
