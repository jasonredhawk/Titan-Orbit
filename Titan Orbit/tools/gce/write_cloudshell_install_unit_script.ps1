# Writes cloudshell_install_titanorbit_unit.sh next to this script.
# Run that file IN GOOGLE CLOUD SHELL (browser) so gcloud uses Linux OpenSSH — avoids Windows IAP + ssh.exe kex resets.

param(
    [string] $ProjectId = "titan-orbit",
    [string] $Zone = "us-central1-f",
    [string] $InstanceTarget = "jason@titanorbitcp",
    [string] $RemoteDir = "/home/jason/titanorbit-server/TitanOrbitLinux1",
    [string] $ExeName = ""
)

$ErrorActionPreference = "Stop"
$unitPath = Join-Path $PSScriptRoot "titanorbit-server.service"
if (-not (Test-Path $unitPath)) {
    Write-Error "Missing unit file: $unitPath"
    exit 1
}

$b64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($unitPath))
$servicePath = "/etc/systemd/system/titanorbit-server.service"

$remoteScript = (@"
set -e
EXE_NAME=""
if [ -n "$ExeName" ] && [ -f "$RemoteDir/$ExeName" ]; then
  EXE_NAME="$ExeName"
elif [ -f "$RemoteDir/TitanOrbitServer.x86_64" ]; then
  EXE_NAME=TitanOrbitServer.x86_64
elif [ -f "$RemoteDir/TitanOrbitServer" ]; then
  EXE_NAME=TitanOrbitServer
else
  echo "No TitanOrbitServer or TitanOrbitServer.x86_64 in $RemoteDir"; ls -la "$RemoteDir" || true; exit 1
fi
chmod +x "$RemoteDir/`$EXE_NAME"
echo '$b64' | base64 -d | sudo tee $servicePath >/dev/null
sudo sed -i "s|__TITANORBIT_EXE__|`$EXE_NAME|g" $servicePath
sudo chmod 644 $servicePath
sudo systemctl daemon-reload
sudo systemctl enable --now titanorbit-server.service
sudo systemctl status titanorbit-server.service --no-pager -l | sed -n '1,80p'
echo
echo Recent log:
if [ -f "$RemoteDir/Player.log" ]; then tail -n 80 "$RemoteDir/Player.log"; else echo "(no Player.log yet - Unity may create it shortly after startup)"; fi
"@) -replace "`r`n", "`n"

$scriptB64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($remoteScript))
$outPath = Join-Path $PSScriptRoot "cloudshell_install_titanorbit_unit.sh"

# Bash expands ${SCRIPT_B64} on Cloud Shell before gcloud runs; last line is single-quoted in PS so ${} is literal.
$lines = @(
    '#!/usr/bin/env bash',
    '# Auto-generated — run in Google Cloud Shell (Console terminal), not on Windows.',
    '# If gcloud fails with 4003 / failed to connect to port 22: IAP cannot reach sshd yet.',
    '# Fix: bash add_iap_ssh_firewall_and_tag_cloudshell.sh  (then wait ~60s). Still broken? bash diagnose_iap_ssh_cloudshell.sh',
    '# Shared VPC: IAP rule may belong in the HOST project.',
    '# Escapes: TITANORBIT_IAP_SSH_ALL_VMS=1 and/or TITANORBIT_IAP_SSH_PRIORITY0=1 bash add_iap_ssh_firewall_and_tag_cloudshell.sh',
    'set -euo pipefail',
    "PROJECT_ID=`"$ProjectId`"",
    "ZONE=`"$Zone`"",
    "INSTANCE_SSH=`"$InstanceTarget`"",
    "SCRIPT_B64='$scriptB64'",
    '',
    'gcloud config set project "$PROJECT_ID"',
    'gcloud compute ssh "$INSTANCE_SSH" \',
    '  --project="$PROJECT_ID" \',
    '  --zone="$ZONE" \',
    '  --tunnel-through-iap \',
    '  --strict-host-key-checking=no \',
    '  --quiet \',
    '  --command="echo ${SCRIPT_B64} | base64 -d | bash -s"'
)
[System.IO.File]::WriteAllText($outPath, ($lines -join "`n") + "`n", [System.Text.UTF8Encoding]::new($false))

Write-Host "Wrote: $outPath"
Write-Host "Next: Cloud Shell -> Upload this file -> bash cloudshell_install_titanorbit_unit.sh"
