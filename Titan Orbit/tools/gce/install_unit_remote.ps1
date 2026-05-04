# Installs titanorbit-server.service on the VM.
# - With -UseIap (default for IAP .bat): IAP tunnel + ssh.exe (OpenSSH), not plink.
# - Without -UseIap: try direct ssh.exe to VM external IP first; plink only as last resort.
# - With -UsePlinkWithIap: force "gcloud compute ssh" (PuTTY plink).

param(
    [string] $ProjectId = "titan-orbit",
    [string] $Zone = "us-central1-f",
    [string] $InstanceTarget = "jason@titanorbitcp",
    [string] $RemoteDir = "/home/jason/titanorbit-server/TitanOrbitLinux1",
    [string] $ExeName = "",
    [switch] $UseIap,
    [switch] $UsePlinkWithIap
)

if (-not [string]::IsNullOrWhiteSpace($env:TITANORBIT_GCE_INSTANCE_TARGET)) {
    $InstanceTarget = $env:TITANORBIT_GCE_INSTANCE_TARGET.Trim()
}

$ErrorActionPreference = "Stop"

try {
    $gcloudEntry = (Get-Command gcloud -ErrorAction Stop).Source
    $gcloudDir = Split-Path $gcloudEntry
    $gcloudCmd = Join-Path $gcloudDir "gcloud.cmd"
    # Start-Process cannot launch gcloud.ps1; prefer gcloud.cmd next to it (Windows SDK layout).
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

$unitPath = Join-Path $PSScriptRoot "titanorbit-server.service"
if (-not (Test-Path $unitPath)) {
    Write-Error "Missing unit file: $unitPath"
    exit 1
}

if ($InstanceTarget -notmatch '^([^@]+)@(.+)$') {
    Write-Error "InstanceTarget must be like user@INSTANCE_NAME (got: $InstanceTarget)"
    exit 1
}
$sshUser = $Matches[1]
$instanceName = $Matches[2]

$b64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($unitPath))
$servicePath = "/etc/systemd/system/titanorbit-server.service"

$remoteScript = (@"
set -e
mkdir -p "$RemoteDir"
# Prefer Unity Linux .x86_64; fall back to extensionless TitanOrbitServer (some Unity builds).
EXE_NAME=""
if [ -n "$ExeName" ] && [ -f "$RemoteDir/$ExeName" ]; then
  EXE_NAME="$ExeName"
elif [ -f "$RemoteDir/TitanOrbitServer.x86_64" ]; then
  EXE_NAME=TitanOrbitServer.x86_64
elif [ -f "$RemoteDir/TitanOrbitServer" ]; then
  EXE_NAME=TitanOrbitServer
else
  EXE_NAME=TitanOrbitServer.x86_64
  echo "NOTE: No server binary in $RemoteDir yet. Installing unit for `$EXE_NAME; upload your Linux build to this folder, then: sudo systemctl restart titanorbit-server"
fi
if [ -f "$RemoteDir/`$EXE_NAME" ]; then
  chmod 755 "$RemoteDir/`$EXE_NAME"
fi
echo '$b64' | base64 -d | sudo tee $servicePath >/dev/null
# Repo unit uses __TITANORBIT_EXE__; must match Unity output (usually TitanOrbitServer.x86_64) or systemd returns 203/EXEC.
sudo sed -i "s|__TITANORBIT_EXE__|`$EXE_NAME|g" $servicePath
sudo chmod 644 $servicePath
sudo systemctl daemon-reload
if [ -f "$RemoteDir/`$EXE_NAME" ]; then
  sudo systemctl enable --now titanorbit-server.service
  sudo systemctl status titanorbit-server.service --no-pager -l | sed -n '1,80p'
  echo
  echo Recent log:
  if [ -f "$RemoteDir/Player.log" ]; then tail -n 80 "$RemoteDir/Player.log"; else echo "(no Player.log yet - Unity may create it shortly after startup)"; fi
else
  sudo systemctl enable titanorbit-server.service
  echo "Service enabled (not started). After upload: sudo systemctl start titanorbit-server"
fi
"@) -replace "`r`n", "`n"
if (-not $remoteScript.EndsWith("`n")) {
    $remoteScript += "`n"
}

function Invoke-GcloudComputeSsh {
    # Do NOT pass "bash -s" after "--": on Windows, gcloud uses PuTTY plink, and plink's "-s" means "subsystem" (popup: Unknown option -s).
    # Do NOT pipe the install script on stdin: gcloud/plink often read stdin for (Y/n) first; the first line consumed can make remote bash see "y" as line 1.
    $installPackB64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($remoteScript))
    if ($installPackB64.Length -gt 6000) {
        Write-Error "Encoded install script is too long for gcloud --command on Windows (~8k limit). Shorten titanorbit-server.service or use -UseIap."
        exit 1
    }
    # bash -lc '...' keeps the pipe on the VM; PS invokes gcloud with argv (no cmd.exe), so local Windows base64 is never run.
    $remoteCmd = "bash -lc 'echo $installPackB64 | base64 -d | bash -s'"
    $gcloudArgs = @(
        "--quiet",
        "compute", "ssh", $InstanceTarget,
        "--project=$ProjectId",
        "--zone=$Zone",
        "--strict-host-key-checking=no"
    )
    if ($UseIap) {
        $gcloudArgs += "--tunnel-through-iap"
    }
    $gcloudArgs += "--command=$remoteCmd"

    Write-Host "Running: gcloud --quiet compute ssh ... --command=bash -lc 'echo <b64> | base64 -d | bash -s' (stdin not used for script)"
    $prevPrompts = $env:CLOUDSDK_CORE_DISABLE_PROMPTS
    $env:CLOUDSDK_CORE_DISABLE_PROMPTS = "1"
    try {
        & $gcloudExe @gcloudArgs
        exit $LASTEXITCODE
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

function Invoke-DirectOpenSshInstall {
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
    $installPackB64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($remoteScript))
    if ($installPackB64.Length -gt 6000) {
        return 1
    }
    $iapSshRemoteArg = "bash -lc `"echo $installPackB64 | base64 -d | bash -s`""
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
    Write-Host "Running: ssh.exe -> $target (direct install; no IAP) ..."
    & $sshCmd.Source @sshArgs $iapSshRemoteArg
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
        Write-Error "ssh.exe not found (install OpenSSH Client optional Windows feature). Falling back is disabled for IAP; use -UsePlinkWithIap or install OpenSSH."
        exit 1
    }

    $identity = Join-Path $env:USERPROFILE ".ssh\google_compute_engine"
    if (-not (Test-Path $identity)) {
        Write-Error "Missing SSH key for GCE: $identity`nRun once (any path that completes SSH):`n  gcloud --project $ProjectId compute ssh $InstanceTarget --zone $Zone --tunnel-through-iap"
        exit 1
    }

    # Same delivery as non-IAP gcloud path: embed script in bash -lc 'echo b64 | base64 -d | bash -s'.
    # Piping $remoteScript to 'ssh ... bash -s' can truncate on Windows (remote bash: "unexpected end of file" at last line).
    $iapInstallB64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($remoteScript))
    if ($iapInstallB64.Length -gt 26000) {
        Write-Error "Install script too long for ssh.exe argv (base64 length $($iapInstallB64.Length)). Shorten titanorbit-server.service or use Cloud Shell install script."
        exit 1
    }
    $iapSshRemoteArg = "bash -lc `"echo $iapInstallB64 | base64 -d | bash -s`""
    if ($iapSshRemoteArg.Length -gt 31000) {
        Write-Error "ssh remote argument too long ($($iapSshRemoteArg.Length) chars)."
        exit 1
    }

    $port = Get-FreeLocalPort
    # Without this flag, gcloud probes port 22 through IAP before binding; that probe often returns 4003 briefly
    # (or falsely) even when the VPC rule + tag are correct. Disabling the check still requires a working sshd + firewall for ssh.exe to succeed.
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
    # Redirect stdout too: gcloud may block on a full stdout pipe; without it the tunnel can accept TCP before SSH is forwarded ("banner exchange" timeout).
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
                $iap4003Hint = ""
                if ($stderrTail -match '4003|Failed to connect to port 22|failed to connect to backend') {
                    $iap4003Hint = @"

IAP could not reach TCP 22 on the VM (common). Add a VPC firewall rule that allows SSH from IAP's TCP forwarding range, and tag your VM (or use a matching target filter).

Easiest: re-run tools\gce\add_iap_ssh_firewall_and_tag.bat (it detects the VM VPC and creates iap-allow-ssh-<vpc>).

Manual example on the correct VPC network name:

  gcloud compute firewall-rules create allow-ssh-ingress-from-iap --project=$ProjectId --network=YOUR_VPC_NAME --direction=INGRESS --action=ALLOW --rules=tcp:22 --source-ranges=35.235.240.0/20 --target-tags=allow-iap-ssh

Then: VM -> Edit -> Network tags -> add: allow-iap-ssh -> Save. Wait a minute and retry.

See: https://cloud.google.com/iap/docs/using-tcp-forwarding#create-firewall-rule

If you already ran add_iap_ssh_firewall_and_tag.bat successfully: on the VM confirm sshd listens on all interfaces (e.g. ListenAddress 0.0.0.0 or unset in /etc/ssh/sshd_config), and grant your Google user roles/iap.tunnelResourceAccessor on this project.
"@
                }
                Write-Error @"
IAP tunnel process exited early (exit $exitCode). Check IAP is enabled, roles/iap.tunnelResourceAccessor for your account, and sshd on the VM.
gcloud stderr (if any):
$stderrTail
$iap4003Hint
"@
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
            catch {
                # gcloud may still be writing the log
            }
            $log = "$outText`n$errText"
            # gcloud prints e.g. "Listening on port [127.0.0.1:29212]." when the tunnel is actually forwarding.
            if ($log -match 'istening') {
                $ready = $true
                break
            }
            if (Test-NetConnection -ComputerName 127.0.0.1 -Port $port -WarningAction SilentlyContinue -InformationLevel Quiet -ErrorAction SilentlyContinue) {
                if (-not $tncSince) {
                    $tncSince = Get-Date
                }
                # Port accepts TCP but no "Listening" yet — wait longer: TCP can open before IAP forwards SSH ("banner exchange" timeout).
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
            Write-Error "Timed out waiting for local IAP tunnel on 127.0.0.1:$port (no Listening line and port not ready). Last tunnel stderr:`n$(if (Test-Path $tunnelErrLog) { [IO.File]::ReadAllText($tunnelErrLog) } else { '(none)' })"
            exit 1
        }

        # Brief settle time after "Listening" before first SSH packet (reduces banner-exchange timeouts).
        Start-Sleep -Seconds 3
        Write-Host "Waiting for SSH banner on 127.0.0.1:$port (avoids banner-exchange timeout) ..."
        if (-not (Test-LocalPortSshBanner -Port $port -TimeoutSec 50)) {
            Write-Warning "Did not read an SSH- banner on the local tunnel (continuing anyway). Tunnel stderr tail:`n$((Get-Content $tunnelErrLog -Raw -ErrorAction SilentlyContinue).Trim())"
        }

        Write-Host "Running ssh.exe -> bash -lc 'echo <b64> | base64 -d | bash -s' (same as gcloud path; avoids Windows stdin pipe truncation)"
        # -T: no TTY (fine for bash -lc remote command).
        # IPQoS=none: avoids some Windows / router paths that reset SSH through tunnels.
        # IdentitiesOnly=yes: only use -i key (Windows ssh-agent keys can confuse GCE).
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
            & $sshExe @sshBase $iapSshRemoteArg
            $lastCode = $LASTEXITCODE
            if ($lastCode -eq 0) {
                break
            }
            if ($a -lt $maxAttempts) {
                Write-Host "SSH exited $lastCode (e.g. kex reset / banner). Retrying in 3s ..."
                Start-Sleep -Seconds 3
            }
        }

        if ($lastCode -ne 0) {
            Write-Host ""
            Write-Host 'IAP tunnel is up but ssh.exe still failed. Common causes:'
            Write-Host '  0) Windows key ACL: if you saw UNPROTECTED PRIVATE KEY / OWNER RIGHTS on google_compute_engine, run: fix_google_compute_engine_key_acl.bat'
            Write-Host '  1) OS Login enabled for the project/instance: metadata SSH keys are ignored. Fix: IAM grant roles/compute.osLogin and roles/compute.osAdminLogin if needed; or set VM metadata enable-oslogin=FALSE for testing; or use Cloud Console SSH only.'
            Write-Host '  2) Antivirus may reset localhost tunnel traffic: exclude OpenSSH and gcloud, or use Cloud Shell and README Simple deploy GCS plus Cloud Shell.'
            Write-Host '  3) Manual unit install: copy tools/gce/titanorbit-server.service via browser SSH; sudo tee /etc/systemd/system/titanorbit-server.service'
            Write-Host '  4) Bypass Windows ssh.exe: run write_cloudshell_install_unit_script.bat then run the generated cloudshell_install_titanorbit_unit.sh in Google Cloud Shell.'
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

if ($UseIap -and -not $UsePlinkWithIap) {
    Invoke-IapTunnelPlusOpenSsh
}
elseif (-not $UsePlinkWithIap) {
    $dc = Invoke-DirectOpenSshInstall
    if ($dc -eq 0) {
        exit 0
    }
    Write-Warning "Direct OpenSSH install failed ($dc). Last resort: gcloud compute ssh (plink)."
    Invoke-GcloudComputeSsh
}
else {
    Invoke-GcloudComputeSsh
}
