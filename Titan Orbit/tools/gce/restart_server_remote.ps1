# Restarts titanorbit-server on the VM without hanging Windows plink on long gcloud --command lines.
# - Default: gcloud --quiet compute ssh + bash -lc 'echo <b64> | base64 -d | bash -s' (same idea as install_unit_remote.ps1).
# - With -UseIap: gcloud compute start-iap-tunnel + ssh.exe + stdin to bash -s (avoids gcloud compute ssh / plink for IAP).
# - With -UseIap -PlainSshFirst: try plain gcloud compute ssh first, then IAP tunnel if that fails (avoids hangs when direct SSH works).
# - With -UsePlinkWithIap: legacy gcloud compute ssh --tunnel-through-iap (plink).

param(
    [string] $ProjectId = "titan-orbit",
    [string] $Zone = "us-central1-a",
    [string] $InstanceTarget = "jason@titan-orbit-compute-engine",
    [string] $ServiceName = "titanorbit-server",
    [string] $RemoteLog = "/home/jason/titanorbit-server/TitanOrbitLinux1/Player.log",
    [switch] $UseIap,
    [switch] $PlainSshFirst,
    [switch] $UsePlinkWithIap
)

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
sudo systemctl restart __SN__
set +e
i=0
stable=0
while [ $i -lt 120 ]; do
  ast=$(sudo systemctl show -p ActiveState --value __SN__ 2>/dev/null || true)
  sub=$(sudo systemctl show -p SubState --value __SN__ 2>/dev/null || true)
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
    echo "HINT: status=203/EXEC means systemd could not execute ExecStart. Unity Linux server builds"
    echo "      usually ship TitanOrbitServer.x86_64 (not TitanOrbitServer). Re-install the unit so"
    echo "      ExecStart matches the real binary: install_enable_server_service_on_gce.bat (or _iap.bat)"
    echo "      (or Cloud Shell: bash cloudshell_install_titanorbit_unit.sh)."
  fi
  exit 1
fi
echo '--- Player.log tail ---'
if [ -f "__LOG__" ]; then tail -n 30 "__LOG__"; else echo 'No Player.log yet.'; fi
'@.Replace('__SN__', $ServiceName).Replace('__LOG__', $RemoteLog) -replace "`r`n", "`n"

function Invoke-RestartViaGcloudSsh {
    param(
        [Parameter(Mandatory = $true)]
        [bool] $IncludeIapTunnel
    )
    $packB64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($restartScript))
    if ($packB64.Length -gt 6000) {
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
        & $gcloudExe @gcloudArgs
        return $LASTEXITCODE
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
    $code = Invoke-RestartViaGcloudSsh -IncludeIapTunnel ([bool]$UseIap)
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

        Write-Host "Running ssh.exe -> bash -s (restart script on stdin; no plink)"
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
            $restartScript | & $sshExe @sshBase "bash -s"
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
            Write-Host "IAP tunnel + ssh.exe failed (exit $lastCode). Retrying with plain gcloud compute ssh (no local IAP tunnel) ..."
            Write-Host "This matches install_enable_server_service_on_gce.bat when you do NOT pass useIap."
            Write-Host ""
            $plainCode = Invoke-RestartViaGcloudSsh -IncludeIapTunnel $false
            if ($plainCode -eq 0) {
                Write-Host "Restart succeeded via plain gcloud compute ssh."
                exit 0
            }
            Write-Host "Plain gcloud retry exited $plainCode."
            Write-Host ""
            Write-Host 'IAP + plain SSH both failed. Common causes:'
            Write-Host '  0) Windows key ACL: if you saw UNPROTECTED PRIVATE KEY / OWNER RIGHTS, run: fix_google_compute_engine_key_acl.bat'
            Write-Host '  1) OS Login / IAM: metadata SSH keys ignored; roles/compute.osLogin, or use Console SSH.'
            Write-Host '  2) Antivirus / VPN: IAP WebSocket errors (4010, 10053) — exclude gcloud.exe and OpenSSH; or use Cloud Shell.'
            Write-Host '  3) VM has no public SSH: you need a working IAP path or Console SSH.'
            Write-Host '  4) Cloud Shell: gcloud compute ssh ... --tunnel-through-iap --command "sudo systemctl restart titanorbit-server"'
            exit $plainCode
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
    Write-Host "Trying plain gcloud compute ssh first (-PlainSshFirst; skips IAP when direct SSH works)."
    $plainFirstCode = Invoke-RestartViaGcloudSsh -IncludeIapTunnel $false
    if ($plainFirstCode -eq 0) {
        exit 0
    }
    Write-Host "Plain gcloud compute ssh exited $plainFirstCode; trying IAP tunnel + ssh.exe ..."
    Invoke-IapTunnelPlusOpenSsh
}
elseif ($UseIap -and -not $UsePlinkWithIap) {
    Invoke-IapTunnelPlusOpenSsh
}
else {
    Invoke-GcloudComputeSsh
}
