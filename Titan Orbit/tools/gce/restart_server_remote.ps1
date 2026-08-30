# Restarts titanorbit-server on the VM using Windows OpenSSH (ssh.exe) first — not PuTTY plink.
# - Default (no -UseIap): try direct ssh.exe to the VM external IP if present; else gcloud compute ssh (plink last resort).
# - With -UseIap: gcloud start-iap-tunnel + ssh.exe (primary). Plink only as explicit last resort after OpenSSH attempts fail.
# - With -UseIap -PlainSshFirst: direct ssh.exe to external IP first, then IAP tunnel + ssh.exe.
# - With -UsePlinkWithIap: force legacy gcloud compute ssh --tunnel-through-iap (plink) — not recommended on Windows.

param(
    [string] $ProjectId = "titan-orbit",
    [string] $Zone = "us-central1-f",
    [string] $InstanceTarget = "jason@titanorbitcp",
    [string] $ServiceName = "titanorbit-server",
    [string] $RemoteLog = "/home/jason/titanorbit-server/TitanOrbitLinux1/Player.log",
    [switch] $UseIap,
    [switch] $PlainSshFirst,
    [switch] $UsePlinkWithIap
)

# Full "user@instanceName" — must match VM login user (see metadata SSH keys / OS Login), e.g. jason_redhawk@titanorbitcp
if (-not [string]::IsNullOrWhiteSpace($env:TITANORBIT_GCE_INSTANCE_TARGET)) {
    $InstanceTarget = $env:TITANORBIT_GCE_INSTANCE_TARGET.Trim()
}

$ErrorActionPreference = "Stop"

try {
    $gcloudEntry = (Get-Command gcloud -ErrorAction Stop).Source
    $gcloudDir = Split-Path $gcloudEntry
    $gcloudCmd = Join-Path $gcloudDir "gcloud.cmd"
    if (Test-Path $gcloudCmd) {
        $gcloudExe = $gcloudCmd
    }
    else {
        $gcloudExe = $gcloudEntry
    }
}
catch {
    Write-Error "gcloud not found in PATH. Install Google Cloud SDK and reopen your terminal."
    exit 1
}

if ($InstanceTarget -notmatch '^([^@]+)@(.+)$') {
    Write-Error "InstanceTarget must be like user@INSTANCE_NAME (got: $InstanceTarget)"
    exit 1
}
$sshUser = $Matches[1]
$instanceName = $Matches[2]

# Note: `systemctl is-active` exits 3 while the unit is "activating"; with `set -e` the remote bash would
# abort immediately and ssh would return 3 even though `restart` succeeded. Poll ActiveState instead.
$restartScript = @'
set -e
# Windows tar / umask 077: chmod +x on 700 leaves rwx------ → User=jason cannot exec. Force 755 on entry ELFs.
shopt -s nullglob
for f in /home/jason/titanorbit-server/TitanOrbitLinux1/*.x86_64; do
  if [ -f "$f" ]; then chmod 755 "$f" || true; fi
done
if [ -f /home/jason/titanorbit-server/TitanOrbitLinux1/TitanOrbitServer ]; then
  chmod 755 /home/jason/titanorbit-server/TitanOrbitLinux1/TitanOrbitServer || true
fi
chmod a+r /home/jason/titanorbit-server/TitanOrbitLinux1/GameAssembly.so /home/jason/titanorbit-server/TitanOrbitLinux1/UnityPlayer.so 2>/dev/null || true
chmod -R a+rX /home/jason/titanorbit-server/TitanOrbitLinux1 2>/dev/null || true
# Linux Dedicated Server builds may ship TitanOrbitServer (no .x86_64). Unit may still say .x86_64 from install-before-upload → 203/EXEC.
BASE=/home/jason/titanorbit-server/TitanOrbitLinux1
UNIT=/etc/systemd/system/titanorbit-server.service
# Windows-packed wrapper shebang is bash\r → systemd ExecStart exit 127.
if [ -f "$BASE/run_titanorbit_server.sh" ]; then
  sed -i 's/\r$//' "$BASE/run_titanorbit_server.sh"
  chmod 755 "$BASE/run_titanorbit_server.sh"
fi
if [ -f "$BASE/TitanOrbitServer" ] && ! grep -q $'\x7fELF' "$BASE/TitanOrbitServer" 2>/dev/null; then
  sed -i 's/\r$//' "$BASE/TitanOrbitServer" || true
  chmod 755 "$BASE/TitanOrbitServer" || true
fi
EXE=""
if [ -f "$BASE/TitanOrbitServer.x86_64" ]; then
  EXE=TitanOrbitServer.x86_64
elif [ -f "$BASE/TitanOrbitServer" ]; then
  EXE=TitanOrbitServer
else
  EXE=TitanOrbitServer.x86_64
fi
# Keep ExecStart on the wrapper. Pointing at the raw player drops TITANORBIT_PUBLIC_ADDRESS
# so the lobby never publishes after Relay removal.
if [ -f "$UNIT" ] && [ -x "$BASE/run_titanorbit_server.sh" ]; then
  if ! grep -q 'run_titanorbit_server.sh' "$UNIT"; then
    sudo sed -i -E "s|(ExecStart=/home/jason/titanorbit-server/TitanOrbitLinux1/)[^[:space:]]+|\1run_titanorbit_server.sh|" "$UNIT" || true
    sudo systemctl daemon-reload || true
  fi
fi
sudo systemctl restart __SN__
set +e
echo "Polling __SN__ until active+running (up to ~120s; progress every 10s)..."
i=0
stable=0
while [ $i -lt 120 ]; do
  ast=$(sudo systemctl show -p ActiveState --value __SN__ 2>/dev/null || true)
  sub=$(sudo systemctl show -p SubState --value __SN__ 2>/dev/null || true)
  if [ "$i" -ge 10 ] && [ $((i % 10)) -eq 0 ]; then
    echo "... elapsed_s=$i ActiveState=$ast SubState=$sub stable_streak=$stable"
  fi
  if [ "$ast" = "active" ] && [ "$sub" = "running" ]; then
    stable=$((stable+1))
    if [ "$stable" -ge 3 ]; then
      echo "active (SubState=running, stable ${stable}s)"
      break
    fi
  else
    stable=0
  fi
  if [ "$ast" = "failed" ]; then
    sudo systemctl status __SN__ --no-pager -l
    echo '--- journalctl (last 100) ---'
    sudo journalctl -u __SN__ -n 100 --no-pager || true
    echo '--- IL2CPP file sizes ---'
    B=$(dirname "__LOG__")
    stat -c '%n %s bytes' "$B/GameAssembly.so" "$B/UnityPlayer.so" "$B/TitanOrbitServer_Data/il2cpp_data/Metadata/global-metadata.dat" 2>/dev/null || true
    echo '--- Player.log tail ---'
    if [ -f "__LOG__" ]; then tail -n 100 "__LOG__"; else echo 'No Player.log.'; fi
    exit 1
  fi
  i=$((i+1))
  sleep 1
done
set -e
ast=$(sudo systemctl show -p ActiveState --value __SN__ 2>/dev/null || true)
sub=$(sudo systemctl show -p SubState --value __SN__ 2>/dev/null || true)
if [ "$ast" != "active" ] || [ "$sub" != "running" ]; then
  echo "Timed out or service not healthy (ActiveState=$ast SubState=$sub)"
  sudo systemctl status __SN__ --no-pager -l
  if sudo systemctl status __SN__ --no-pager -l 2>/dev/null | grep -q "203/EXEC"; then
    echo ""
    echo "HINT: status=203/EXEC means systemd could not execute ExecStart. Common causes:"
    echo "      (1) Missing +x on the Linux player after upload from Windows tar — redeploy with latest"
    echo "          tools/gce/upload script (chmod step) or: chmod 755 /home/jason/titanorbit-server/TitanOrbitLinux1/TitanOrbitServer /home/jason/titanorbit-server/TitanOrbitLinux1/*.x86_64"
    echo "      (2) Missing run_titanorbit_server.sh — reinstall unit (install_unit_remote.ps1) or redeploy with upload_linux_build_to_gce_openssh.ps1"
  fi
  if sudo systemctl status __SN__ --no-pager -l 2>/dev/null | grep -qE 'status=1/FAILURE|exited, status=1'; then
    echo ""
    echo "HINT: status=1 after [UnityMemory] lines = Unity crashed during startup (often IL2CPP)."
    echo "      journalctl -u __SN__ -n 100  and  stat GameAssembly.so UnityPlayer.so global-metadata.dat"
    echo "      Redeploy: quit Unity Editor, rebuild Linux server, upload_linux_build_to_gce_openssh.ps1"
  fi
  echo '--- journalctl (last 100) ---'
  sudo journalctl -u __SN__ -n 100 --no-pager || true
  echo '--- IL2CPP file sizes ---'
  B=$(dirname "__LOG__")
  stat -c '%n %s bytes' "$B/GameAssembly.so" "$B/UnityPlayer.so" "$B/TitanOrbitServer_Data/il2cpp_data/Metadata/global-metadata.dat" 2>/dev/null || true
  echo '--- Player.log tail ---'
  if [ -f "__LOG__" ]; then tail -n 100 "__LOG__"; else echo 'No Player.log yet.'; fi
  exit 1
fi
echo '--- Player.log tail ---'
if [ -f "__LOG__" ]; then tail -n 30 "__LOG__"; else echo 'No Player.log yet.'; fi
'@.Replace('__SN__', $ServiceName).Replace('__LOG__', $RemoteLog) -replace "`r`n", "`n"
if (-not $restartScript.EndsWith("`n")) {
    $restartScript += "`n"
}

function Invoke-RestartViaGcloudSsh {
    param(
        [Parameter(Mandatory = $true)]
        [bool] $IncludeIapTunnel
    )
    $packB64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($restartScript))
    if ($packB64.Length -gt 32000) {
        Write-Error "Encoded restart script unexpectedly long ($($packB64.Length) chars)."
        return 1
    }
    $remoteCmd = "bash -lc 'echo $packB64 | base64 -d | bash -s'"
    $gcloudArgs = @(
        "--quiet",
        "compute", "ssh", $InstanceTarget,
        "--project=$ProjectId",
        "--zone=$Zone",
        "--strict-host-key-checking=no"
    )
    if ($IncludeIapTunnel) {
        $gcloudArgs += "--tunnel-through-iap"
    }
    $gcloudArgs += "--command=$remoteCmd"

    $label = if ($IncludeIapTunnel) {
        "gcloud --quiet compute ssh ... --tunnel-through-iap --command=bash -lc (base64)"
    }
    else {
        "gcloud --quiet compute ssh ... (no --tunnel-through-iap; same as install_enable_server_service_on_gce.bat)"
    }
    Write-Host "Running: $label"
    $prevPrompts = $env:CLOUDSDK_CORE_DISABLE_PROMPTS
    $env:CLOUDSDK_CORE_DISABLE_PROMPTS = "1"
    try {
        # Capture native exit code immediately; do not let stdout flow to the pipeline or
        # `$x = Invoke-RestartViaGcloudSsh` will absorb all lines and `-eq 0` will never match.
        $gcloudOut = & $gcloudExe @gcloudArgs 2>&1
        $ec = $LASTEXITCODE
        $gcloudOut | ForEach-Object { Write-Host $_ }
        return $ec
    }
    finally {
        if ($null -eq $prevPrompts) {
            Remove-Item Env:\CLOUDSDK_CORE_DISABLE_PROMPTS -ErrorAction SilentlyContinue
        }
        else {
            $env:CLOUDSDK_CORE_DISABLE_PROMPTS = $prevPrompts
        }
    }
}

function Invoke-GcloudComputeSsh {
    $code = [int](Invoke-RestartViaGcloudSsh -IncludeIapTunnel ([bool]$UseIap))
    exit $code
}

function Get-FreeLocalPort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    try {
        $listener.Start()
        return $listener.LocalEndpoint.Port
    }
    finally {
        $listener.Stop()
    }
}

function Get-InstanceNatIp {
    $out = & $gcloudExe @(
        "--quiet",
        "compute", "instances", "describe", $instanceName,
        "--project=$ProjectId", "--zone=$Zone",
        "--format=get(networkInterfaces[0].accessConfigs[0].natIP)"
    ) 2>&1
    if ($LASTEXITCODE -ne 0) {
        return ""
    }
    return ($out | Out-String).Trim()
}

function Invoke-DirectOpenSshRestart {
    $sshCmd = Get-Command ssh.exe -ErrorAction SilentlyContinue
    if (-not $sshCmd) {
        return 99
    }
    $identity = Join-Path $env:USERPROFILE ".ssh\google_compute_engine"
    if (-not (Test-Path $identity)) {
        return 99
    }
    $nat = Get-InstanceNatIp
    if ([string]::IsNullOrWhiteSpace($nat)) {
        return 99
    }
    $packB64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($restartScript))
    if ($packB64.Length -gt 32000) {
        Write-Error "Encoded restart script unexpectedly long ($($packB64.Length) chars)."
        return 1
    }
    $remoteCmd = "bash -lc `"echo $packB64 | base64 -d | bash -s`""
    $target = "${sshUser}@${nat}"
    $sshArgs = @(
        "-4", "-T",
        "-i", $identity,
        "-o", "StrictHostKeyChecking=no",
        "-o", "UserKnownHostsFile=NUL",
        "-o", "IdentitiesOnly=yes",
        "-o", "PreferredAuthentications=publickey",
        "-o", "BatchMode=yes",
        "-o", "IPQoS=none",
        "-o", "ServerAliveInterval=15",
        "-o", "ServerAliveCountMax=6",
        "-o", "ConnectTimeout=90",
        $target
    )
    Write-Host "Running: ssh.exe -> $target (direct; no IAP tunnel) ..."
    & $sshCmd.Source @sshArgs $remoteCmd
    return $LASTEXITCODE
}

# IAP can accept TCP before sshd traffic is forwarded; ssh.exe then fails with "banner exchange" timeout.
function Test-LocalPortSshBanner {
    param([int] $Port, [int] $TimeoutSec = 45)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        $c = $null
        try {
            $c = New-Object System.Net.Sockets.TcpClient
            $iar = $c.BeginConnect([IPAddress]::Loopback, $Port, $null, $null)
            if (-not $iar.AsyncWaitHandle.WaitOne(2500)) {
                continue
            }
            $c.EndConnect($iar)
            $ns = $c.GetStream()
            $ns.ReadTimeout = 10000
            $buf = New-Object byte[] 512
            $n = $ns.Read($buf, 0, 512)
            if ($n -ge 4) {
                $head = [Text.Encoding]::ASCII.GetString($buf, 0, [Math]::Min(64, $n))
                if ($head.StartsWith("SSH-")) {
                    return $true
                }
            }
        }
        catch {
            # retry
        }
        finally {
            if ($null -ne $c) {
                try { $c.Close() } catch { }
            }
        }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

function Invoke-IapTunnelPlusOpenSsh {
    $sshCmd = Get-Command ssh.exe -ErrorAction SilentlyContinue
    $sshExe = if ($sshCmd) { $sshCmd.Source } else { $null }
    if (-not $sshExe) {
        Write-Error "ssh.exe not found (install OpenSSH Client optional Windows feature). Use -UsePlinkWithIap or install OpenSSH."
        exit 1
    }

    $identity = Join-Path $env:USERPROFILE ".ssh\google_compute_engine"
    if (-not (Test-Path $identity)) {
        Write-Error "Missing SSH key for GCE: $identity`nRun once: gcloud --project $ProjectId compute ssh $InstanceTarget --zone $Zone --tunnel-through-iap"
        exit 1
    }

    $iapRestartPackB64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($restartScript))
    if ($iapRestartPackB64.Length -gt 26000) {
        Write-Error "Restart script too long for ssh.exe argv (base64 length $($iapRestartPackB64.Length))."
        exit 1
    }
    $iapRestartRemoteArg = "bash -lc `"echo $iapRestartPackB64 | base64 -d | bash -s`""
    if ($iapRestartRemoteArg.Length -gt 31000) {
        Write-Error "ssh argv too long ($($iapRestartRemoteArg.Length) chars)."
        exit 1
    }

    $port = Get-FreeLocalPort
    $tunnelArgs = @(
        "compute", "start-iap-tunnel", $instanceName, "22",
        "--project=$ProjectId",
        "--zone=$Zone",
        "--local-host-port=127.0.0.1:$port",
        "--iap-tunnel-disable-connection-check"
    )

    Write-Host "Starting IAP tunnel on 127.0.0.1:$port (instance $instanceName) ..."
    $tunnelErrLog = Join-Path ([System.IO.Path]::GetTempPath()) "titanorbit-iap-tunnel-$port.err.log"
    $tunnelOutLog = Join-Path ([System.IO.Path]::GetTempPath()) "titanorbit-iap-tunnel-$port-out.log"
    foreach ($p in @($tunnelErrLog, $tunnelOutLog)) {
        if (Test-Path $p) {
            Remove-Item $p -Force -ErrorAction SilentlyContinue
        }
    }
    $proc = Start-Process -FilePath $gcloudExe -ArgumentList $tunnelArgs -PassThru -WindowStyle Hidden `
        -RedirectStandardError $tunnelErrLog -RedirectStandardOutput $tunnelOutLog
    if (-not $proc) {
        Write-Error "Failed to start gcloud compute start-iap-tunnel"
        exit 1
    }

    try {
        $deadline = (Get-Date).AddSeconds(90)
        $ready = $false
        $tncSince = $null
        while ((Get-Date) -lt $deadline) {
            if ($proc.HasExited) {
                $stderrTail = ""
                if (Test-Path $tunnelErrLog) {
                    $stderrTail = (Get-Content $tunnelErrLog -Raw -ErrorAction SilentlyContinue).Trim()
                }
                $exitCode = if ($null -ne $proc.ExitCode) { [string]$proc.ExitCode } else { "unknown" }
                Write-Error "IAP tunnel process exited early (exit $exitCode). stderr:`n$stderrTail"
                exit 1
            }
            $outText = ""
            $errText = ""
            try {
                if (Test-Path $tunnelOutLog) {
                    $outText = [System.IO.File]::ReadAllText($tunnelOutLog)
                }
                if (Test-Path $tunnelErrLog) {
                    $errText = [System.IO.File]::ReadAllText($tunnelErrLog)
                }
            }
            catch { }
            $log = "$outText`n$errText"
            if ($log -match 'istening') {
                $ready = $true
                break
            }
            if (Test-NetConnection -ComputerName 127.0.0.1 -Port $port -WarningAction SilentlyContinue -InformationLevel Quiet -ErrorAction SilentlyContinue) {
                if (-not $tncSince) {
                    $tncSince = Get-Date
                }
                # Longer wait without "Listening" line: port can accept TCP before IAP forwards SSH ("banner exchange" timeout).
                if (((Get-Date) - $tncSince).TotalSeconds -ge 45) {
                    $ready = $true
                    break
                }
            }
            else {
                $tncSince = $null
            }
            Start-Sleep -Milliseconds 500
        }
        if (-not $ready) {
            Write-Error "Timed out waiting for IAP tunnel on 127.0.0.1:$port. stderr:`n$(if (Test-Path $tunnelErrLog) { [IO.File]::ReadAllText($tunnelErrLog) } else { '(none)' })"
            exit 1
        }

        Start-Sleep -Seconds 10
        Write-Host "Waiting for SSH banner on 127.0.0.1:$port (avoids banner-exchange timeout) ..."
        if (-not (Test-LocalPortSshBanner -Port $port -TimeoutSec 50)) {
            Write-Warning "Did not read an SSH- banner on the local tunnel (continuing anyway). Tunnel stderr tail:`n$((Get-Content $tunnelErrLog -Raw -ErrorAction SilentlyContinue).Trim())"
        }

        Write-Host "Running ssh.exe -> bash -lc 'echo <b64> | base64 -d | bash -s' (avoids Windows stdin pipe truncation)"
        $sshBase = @(
            "-4", "-T",
            "-p", "$port",
            "-i", $identity,
            "-o", "StrictHostKeyChecking=no",
            "-o", "UserKnownHostsFile=NUL",
            "-o", "IdentitiesOnly=yes",
            "-o", "PreferredAuthentications=publickey",
            "-o", "BatchMode=yes",
            "-o", "IPQoS=none",
            "-o", "ServerAliveInterval=15",
            "-o", "ServerAliveCountMax=6",
            "-o", "ConnectTimeout=90",
            "${sshUser}@127.0.0.1"
        )

        $maxAttempts = 4
        $lastCode = 1
        for ($a = 1; $a -le $maxAttempts; $a++) {
            Write-Host "SSH attempt $a / $maxAttempts ..."
            & $sshExe @sshBase $iapRestartRemoteArg
            $lastCode = $LASTEXITCODE
            if ($lastCode -eq 0) {
                break
            }
            if ($a -lt $maxAttempts) {
                Write-Host "SSH exited $lastCode. Retrying in 3s ..."
                Start-Sleep -Seconds 3
            }
        }

        if ($lastCode -ne 0) {
            Write-Host ""
            Write-Host "IAP tunnel + ssh.exe failed (exit $lastCode). Trying direct OpenSSH to VM external IP (if any) ..."
            $directCode = Invoke-DirectOpenSshRestart
            if ($directCode -eq 0) {
                Write-Host "Restart succeeded via direct ssh.exe to external IP."
                exit 0
            }
            Write-Warning "OpenSSH paths failed. Last resort: gcloud compute ssh --tunnel-through-iap (PuTTY plink)."
            $plinkCode = Invoke-RestartViaGcloudSsh -IncludeIapTunnel $true
            if ($plinkCode -eq 0) {
                Write-Host "Restart succeeded via gcloud IAP (plink fallback only)."
                exit 0
            }
            Write-Host ""
            Write-Host 'All restart paths failed. Common causes:'
            Write-Host '  0) Windows key ACL: fix_google_compute_engine_key_acl.bat'
            Write-Host '  1) OS Login / IAM: metadata SSH keys ignored.'
            Write-Host '  2) Antivirus / VPN blocking IAP or ssh.exe.'
            Write-Host '  3) Cloud Shell: gcloud compute ssh ... --tunnel-through-iap --command "sudo systemctl restart titanorbit-server"'
            exit $plinkCode
        }

        exit $lastCode
    }
    finally {
        if ($proc -and -not $proc.HasExited) {
            Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        }
        foreach ($p in @($tunnelErrLog, $tunnelOutLog)) {
            if ($p -and (Test-Path $p)) {
                Remove-Item $p -Force -ErrorAction SilentlyContinue
            }
        }
    }
}

if ($UseIap -and $PlainSshFirst -and -not $UsePlinkWithIap) {
    Write-Host "Trying direct ssh.exe to external IP first (-PlainSshFirst) ..."
    $df = Invoke-DirectOpenSshRestart
    if ($df -eq 0) {
        exit 0
    }
    Write-Host "Direct ssh exited $df; using IAP tunnel + ssh.exe ..."
    Invoke-IapTunnelPlusOpenSsh
}
elseif ($UseIap -and -not $UsePlinkWithIap) {
    Write-Host "Using IAP tunnel + ssh.exe (OpenSSH; stable on Windows)."
    Invoke-IapTunnelPlusOpenSsh
}
elseif (-not $UsePlinkWithIap) {
    Write-Host "Trying direct ssh.exe to external IP (no -UseIap) ..."
    $df2 = Invoke-DirectOpenSshRestart
    if ($df2 -eq 0) {
        exit 0
    }
    Write-Warning "Direct OpenSSH failed ($df2). Last resort: gcloud compute ssh (plink)."
    Invoke-GcloudComputeSsh
}
else {
    Invoke-GcloudComputeSsh
}
