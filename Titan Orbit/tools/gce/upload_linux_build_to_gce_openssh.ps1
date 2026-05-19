# Upload Unity Linux headless build using Windows OpenSSH (ssh.exe / scp.exe).
# Use when gcloud compute ssh/scp fails with plink "Remote side unexpectedly closed network connection".
#
# Usage (from tools\gce):
#   powershell -NoProfile -File .\upload_linux_build_to_gce_openssh.ps1
#   (Tries direct SSH to the VM external IP first; if that fails, retries via IAP automatically.)
#   powershell -NoProfile -File .\upload_linux_build_to_gce_openssh.ps1 -UseIap   (legacy; upload tries direct SSH first whenever the VM has a public IP, then IAP)
#   powershell -NoProfile -File .\upload_linux_build_to_gce_openssh.ps1 -IapOnly   (skip direct; IAP tunnel only — for testing)
#   powershell -NoProfile -File .\upload_linux_build_to_gce_openssh.ps1 -NoIapFallback
#   powershell -NoProfile -File .\upload_linux_build_to_gce_openssh.ps1 -SourceDir "D:\Builds\TitanOrbitLinux1" -ProjectId titan-orbit

param(
    [string] $ProjectId = "titan-orbit",
    [string] $Zone = "us-central1-f",
    [string] $InstanceName = "titanorbitcp",
    [string] $SshUser = "jason",
    [string] $TargetDir = "/home/jason/titanorbit-server",
    [string] $SourceDir = "",
    [switch] $UseIap,
    [switch] $IapOnly,
    [switch] $NoIapFallback
)

# Linux username must match a user that can log in (metadata/OS Login). Console often creates e.g. jason_redhawk, not jason.
if (-not [string]::IsNullOrWhiteSpace($env:TITANORBIT_GCE_SSH_USER)) {
    $SshUser = $env:TITANORBIT_GCE_SSH_USER.Trim()
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
    Write-Error "gcloud not found in PATH."
    exit 1
}

$sshExe = (Get-Command ssh.exe -ErrorAction SilentlyContinue).Source
$scpExe = (Get-Command scp.exe -ErrorAction SilentlyContinue).Source
if (-not $sshExe -or -not $scpExe) {
    Write-Error 'ssh.exe / scp.exe not found. Install OpenSSH Client (Windows optional features).'
    exit 1
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
if (-not $SourceDir) {
    $SourceDir = Join-Path $repoRoot "BuildOutput\Server\TitanOrbitLinux1"
}
$SourceDir = (Resolve-Path -LiteralPath $SourceDir).Path

$identity = Join-Path $env:USERPROFILE ".ssh\google_compute_engine"
if (-not (Test-Path $identity)) {
    Write-Error "Missing $identity - create or copy your GCE private key there."
    exit 1
}

$sourceBase = Split-Path -Leaf $SourceDir
$sourceParent = Split-Path -Parent $SourceDir
$archivePath = Join-Path ([System.IO.Path]::GetTempPath()) "$sourceBase.tar.gz"
$bundleRemote = "/tmp/$sourceBase.tar.gz"

$sshCommon = @(
    "-i", $identity,
    "-o", "StrictHostKeyChecking=no",
    "-o", "UserKnownHostsFile=NUL",
    "-o", "IdentitiesOnly=yes",
    "-o", "PreferredAuthentications=publickey",
    "-o", "BatchMode=yes",
    "-o", "IPQoS=none",
    "-o", "ServerAliveInterval=15",
    "-o", "ServerAliveCountMax=6",
    "-o", "ConnectTimeout=90"
)

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

function Test-IapTunnelStderrFor4003 {
    param([string] $ErrLogPath)
    if (-not (Test-Path -LiteralPath $ErrLogPath)) {
        return $false
    }
    $t = (Get-Content -LiteralPath $ErrLogPath -Raw -ErrorAction SilentlyContinue)
    if (-not $t) {
        return $false
    }
    return $t -match '4003|failed to connect to backend|Failed to connect to port 22'
}

function Get-IapTunnelLogTail {
    param(
        [string] $OutPath,
        [string] $ErrPath,
        [int] $MaxChars = 2000
    )
    $s = ""
    foreach ($p in @($OutPath, $ErrPath)) {
        if (Test-Path -LiteralPath $p) {
            try {
                $s += ([System.IO.File]::ReadAllText($p) + "`n")
            }
            catch { }
        }
    }
    $s = $s.Trim()
    if ($s.Length -le $MaxChars) {
        return $s
    }
    return $s.Substring($s.Length - $MaxChars)
}

function Get-InstanceNatIp {
    $out = & $gcloudExe @(
        "--quiet",
        "compute", "instances", "describe", $InstanceName,
        "--project=$ProjectId", "--zone=$Zone",
        "--format=get(networkInterfaces[0].accessConfigs[0].natIP)"
    ) 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Error "gcloud instances describe failed: $out"
        exit 1
    }
    return ($out | Out-String).Trim()
}

function Invoke-WithIapTunnel {
    param(
        [Parameter(Mandatory = $true)]
        [scriptblock] $Action
    )

    $port = Get-FreeLocalPort
    $tunnelArgs = @(
        "compute", "start-iap-tunnel", $InstanceName, "22",
        "--project=$ProjectId",
        "--zone=$Zone",
        "--local-host-port=127.0.0.1:$port",
        "--iap-tunnel-disable-connection-check"
    )

    Write-Host "Starting IAP tunnel on 127.0.0.1:$port ..."
    $tunnelErrLog = Join-Path ([System.IO.Path]::GetTempPath()) "titanorbit-upload-iap-$port.err.log"
    $tunnelOutLog = Join-Path ([System.IO.Path]::GetTempPath()) "titanorbit-upload-iap-$port-out.log"
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
                Write-Error ("IAP tunnel exited early. stderr:`n" + $stderrTail + "`n" + 'See tools/gce/README.md (IAP 4003 / firewall).')
                exit 1
            }
            $log = ""
            try {
                if (Test-Path $tunnelOutLog) {
                    $log += [System.IO.File]::ReadAllText($tunnelOutLog)
                }
                if (Test-Path $tunnelErrLog) {
                    $log += [System.IO.File]::ReadAllText($tunnelErrLog)
                }
            }
            catch { }
            if ($log -match 'istening') {
                $ready = $true
                break
            }
            if (Test-NetConnection -ComputerName 127.0.0.1 -Port $port -WarningAction SilentlyContinue -InformationLevel Quiet -ErrorAction SilentlyContinue) {
                if (-not $tncSince) {
                    $tncSince = Get-Date
                }
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
            Write-Error "Timed out waiting for IAP tunnel. stderr:`n$((Get-Content $tunnelErrLog -Raw -ErrorAction SilentlyContinue))"
            exit 1
        }

        if ($proc.HasExited) {
            $tail = Get-IapTunnelLogTail -OutPath $tunnelOutLog -ErrPath $tunnelErrLog
            Write-Error ('IAP tunnel process exited before ssh (exit ' + $proc.ExitCode + ").`n" + $tail)
            exit 1
        }

        # Single settle; do not probe the port (probes can spam IAP errors into the log).
        Write-Host 'IAP tunnel is up; waiting 18s before ssh/scp ...'
        Start-Sleep -Seconds 18
        if (Test-IapTunnelStderrFor4003 -ErrLogPath $tunnelErrLog) {
            $msg4003 = @(
                'IAP error 4003: Google can reach your project but not TCP port 22 on this VM (nothing answered on the guest).',
                '',
                'If add_iap_ssh_firewall_and_tag already reports the rule+tag on this VPC, 4003 is usually guest OS or a stricter network policy:',
                '  - Guest: sshd down, ListenAddress not 0.0.0.0, ufw/nftables, or broken route to metadata (169.254.169.254) — see README serial console.',
                '  - VPC: DENY before ALLOW, org/folder firewall policy, or Shared VPC (IAP rule on host network project).',
                '',
                'Next: (1) Cloud Shell: bash diagnose_iap_ssh_cloudshell.sh from tools/gce',
                '      (2) Console serial / browser SSH to fix sshd',
                '      (3) Upload without PC SSH: upload_linux_build_to_gcs.bat + README GCS + Cloud Shell',
                'README: tools/gce/README.md — IAP 4003 sections.'
            ) -join [Environment]::NewLine
            Write-Error $msg4003
            exit 1
        }

        try {
            & $Action $port
        }
        catch {
            $tail = Get-IapTunnelLogTail -OutPath $tunnelOutLog -ErrPath $tunnelErrLog
            if ($tail.Length -gt 0) {
                Write-Host ''
                Write-Host '--- IAP tunnel log (tail; for 4003 / sshd see README) ---'
                Write-Host $tail
                Write-Host '---'
            }
            throw $_
        }
    }
    finally {
        if (-not $proc.HasExited) {
            Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        }
        Remove-Item $tunnelErrLog, $tunnelOutLog -Force -ErrorAction SilentlyContinue
    }
}

Write-Host ('[1/4] Verifying source folder: ' + $SourceDir)
if (-not (Test-Path -LiteralPath $SourceDir)) {
    Write-Error "Source folder not found: $SourceDir"
    exit 1
}

Write-Host ('[2/4] Creating archive: ' + $archivePath)
. (Join-Path $PSScriptRoot 'pack_linux_server_archive.ps1')
try {
    $null = New-TitanOrbitLinuxServerArchive -Root $SourceDir -OutPath $archivePath -Attempts 3
}
catch {
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host 'Quit Unity Editor, then re-run upload_linux_build_to_gce.bat' -ForegroundColor Yellow
    if (Test-Path $archivePath) { Remove-Item $archivePath -Force -ErrorAction SilentlyContinue }
    exit 1
}

$wrapperPath = Join-Path $PSScriptRoot "run_titanorbit_server.sh"
if (-not (Test-Path $wrapperPath)) {
    Write-Error "Missing systemd wrapper: $wrapperPath"
    exit 1
}
$wrapperB64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($wrapperPath))

$remotePrepare = "mkdir -p $TargetDir"
# Windows tar + tight umask: files may be mode 700 owned by the SSH user. chmod +x then only adds u+x → still 700;
# systemd runs as User=jason → 203/EXEC "Permission denied". Use 755 on entry ELFs and open the tree for o+rx / a+r.
# IL2CPP: small TitanOrbitServer ELF + GameAssembly.so / UnityPlayer.so must be readable by jason.
# sudo chown fixes ownership when NOPASSWD sudo exists.
$remoteExtract = "mkdir -p $TargetDir; rm -rf $TargetDir/$sourceBase; tar -xzf $bundleRemote -C $TargetDir; rm -f $bundleRemote; chmod -R a+rX $TargetDir/$sourceBase 2>/dev/null; chmod 755 $TargetDir/$sourceBase/TitanOrbitServer 2>/dev/null; chmod 755 $TargetDir/$sourceBase/TitanOrbitServer.x86_64 2>/dev/null; chmod a+r $TargetDir/$sourceBase/GameAssembly.so $TargetDir/$sourceBase/UnityPlayer.so 2>/dev/null; echo '$wrapperB64' | base64 -d > $TargetDir/$sourceBase/run_titanorbit_server.sh; chmod 755 $TargetDir/$sourceBase/run_titanorbit_server.sh; sudo -n chown -R jason:jason $TargetDir/$sourceBase 2>/dev/null || true; exit 0"
# Avoid `||` inside double quotes (PS 7+ parses it as the pipeline-chain operator).
$remoteVerify = "ls -la $TargetDir; ls -la $TargetDir/$sourceBase " + '|| true'
# After extract, fail loudly if any IL2CPP runtime file is empty on the VM. This is the symptom of
# the "no game rooms in lobby" bug: server crashes with "Failed to initialize IL2CPP" because
# global-metadata.dat is 0 bytes, so it never publishes a lobby.
$remoteIntegrity = @"
set -e
B=$TargetDir/$sourceBase
fail=0
check() {
  local p="`$1"; local min=`$2; local label="`$3"
  if [ ! -s "`$p" ]; then
    sz=`$(stat -c%s "`$p" 2>/dev/null || echo MISSING)
    echo "[VM] BAD: `$label is `$sz bytes at `$p"
    fail=1
  else
    sz=`$(stat -c%s "`$p")
    if [ "`$sz" -lt "`$min" ]; then
      echo "[VM] BAD: `$label is `$sz bytes (< `$min) at `$p"
      fail=1
    else
      echo "[VM] OK:  `$label `$sz bytes"
    fi
  fi
}
check "`$B/TitanOrbitServer"                                                       1024     TitanOrbitServer
check "`$B/GameAssembly.so"                                                        10000000 GameAssembly.so
check "`$B/UnityPlayer.so"                                                         5000000  UnityPlayer.so
check "`$B/TitanOrbitServer_Data/il2cpp_data/Metadata/global-metadata.dat"         1000000  global-metadata.dat
if [ "`$fail" -ne 0 ]; then
  echo "[VM] Integrity check failed - the extracted server will not boot. Aborting upload."
  exit 42
fi
echo "[VM] All IL2CPP runtime files look good."
"@

function Invoke-UploadViaIapTunnel {
    Write-Host '[3/4] Upload via IAP tunnel + OpenSSH scp (no plink) ...'
    Invoke-WithIapTunnel -Action {
        param([int] $TunnelPort)
        # Do not use ssh -4 to localhost/IAP; some Windows stacks behave better without forcing IPv4.
        $sshPrep = @("-T") + $sshCommon + @("-p", "$TunnelPort", "${SshUser}@127.0.0.1", "bash -lc '$remotePrepare'")
        & $sshExe @sshPrep
        if ($LASTEXITCODE -ne 0) {
            throw ('ssh mkdir failed (exit ' + $LASTEXITCODE + ')')
        }

        $scpArgs = $sshCommon + @("-P", "$TunnelPort", "$archivePath", "${SshUser}@127.0.0.1:$bundleRemote")
        & $scpExe @scpArgs
        if ($LASTEXITCODE -ne 0) {
            throw ('scp failed (exit ' + $LASTEXITCODE + ')')
        }

        $sshArgs = @("-T") + $sshCommon + @("-p", "$TunnelPort", "${SshUser}@127.0.0.1", "bash -lc '$remoteExtract'")
        & $sshExe @sshArgs
        if ($LASTEXITCODE -ne 0) {
            throw ('ssh extract failed (exit ' + $LASTEXITCODE + ')')
        }

        $sshArgs2 = @("-T") + $sshCommon + @("-p", "$TunnelPort", "${SshUser}@127.0.0.1", "bash -lc '$remoteVerify'")
        & $sshExe @sshArgs2
        if ($LASTEXITCODE -ne 0) {
            throw ('ssh verify failed (exit ' + $LASTEXITCODE + ')')
        }

        $sshArgs3 = @("-T") + $sshCommon + @("-p", "$TunnelPort", "${SshUser}@127.0.0.1", "bash -s")
        $remoteIntegrity | & $sshExe @sshArgs3
        if ($LASTEXITCODE -ne 0) {
            throw ('VM integrity check failed (exit ' + $LASTEXITCODE + '). The uploaded build is missing IL2CPP runtime bytes; the server will not boot.')
        }
    }
}

# Always try direct ssh/scp to the VM external IP first when one exists (even if -UseIap was passed).
# deploy_server_gce_iap.bat historically set -UseIap, which incorrectly skipped direct and hit IAP 4003 when guest :22 was not reachable via IAP but direct SSH worked.
$nat = Get-InstanceNatIp
$directOk = $false
if (-not $IapOnly.IsPresent -and -not [string]::IsNullOrWhiteSpace($nat)) {
    Write-Host ('[3/4] Direct upload to ' + $nat + ' (OpenSSH to external IP; avoids IAP 4003 when firewall allows your IP on :22).')
    $target = "${SshUser}@${nat}"
    $directOk = $true

    $sshPrep = @("-4", "-T") + $sshCommon + @($target, "bash -lc '$remotePrepare'")
    & $sshExe @sshPrep
    if ($LASTEXITCODE -ne 0) {
        $directOk = $false
    }
    if ($directOk) {
        $scpArgs = @("-4") + $sshCommon + @("$archivePath", "${target}:$bundleRemote")
        & $scpExe @scpArgs
        if ($LASTEXITCODE -ne 0) {
            $directOk = $false
        }
    }
    if ($directOk) {
        $sshArgs = @("-4", "-T") + $sshCommon + @($target, "bash -lc '$remoteExtract'")
        & $sshExe @sshArgs
        if ($LASTEXITCODE -ne 0) {
            $directOk = $false
        }
    }
    if ($directOk) {
        $sshArgs2 = @("-4", "-T") + $sshCommon + @($target, "bash -lc '$remoteVerify'")
        & $sshExe @sshArgs2
        if ($LASTEXITCODE -ne 0) {
            $directOk = $false
        }
    }
    if ($directOk) {
        $sshArgs3 = @("-4", "-T") + $sshCommon + @($target, "bash -s")
        $remoteIntegrity | & $sshExe @sshArgs3
        if ($LASTEXITCODE -ne 0) {
            Write-Error 'VM integrity check failed after extraction. Server will not boot. Aborting.'
            exit 1
        }
    }

    if ($directOk) {
        Write-Host '[4/4] Cleaning local archive'
        if (Test-Path $archivePath) {
            Remove-Item $archivePath -Force -ErrorAction SilentlyContinue
        }
        Write-Host ""
        Write-Host 'Upload complete (direct SSH). Next: prepare_and_start_server_on_gce.bat (or restart scripts).'
        exit 0
    }

    if ($NoIapFallback) {
        Write-Error ('Direct upload failed (last exit ' + $LASTEXITCODE + '). Fix firewall/sshd on :22 or omit -NoIapFallback to try IAP.')
        exit 1
    }
    Write-Warning 'Direct SSH failed. Retrying via IAP tunnel (if you see 4003, fix IAP firewall tag + guest sshd — see tools/gce/README.md).'
}

if ([string]::IsNullOrWhiteSpace($nat)) {
    Write-Host 'No external NAT IP on this VM; using IAP tunnel only.'
}
elseif ($IapOnly.IsPresent) {
    Write-Host '-IapOnly: skipping direct upload; using IAP tunnel.'
}

Invoke-UploadViaIapTunnel

Write-Host '[4/4] Cleaning local archive'
if (Test-Path $archivePath) {
    Remove-Item $archivePath -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host 'Upload complete. Next: prepare_and_start_server_on_gce.bat (or restart scripts).'
exit 0
