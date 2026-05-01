# Upload Unity Linux headless build using Windows OpenSSH (ssh.exe / scp.exe).
# Use when gcloud compute ssh/scp fails with plink "Remote side unexpectedly closed network connection".
#
# Usage (from tools\gce):
#   powershell -NoProfile -File .\upload_linux_build_to_gce_openssh.ps1
#   (Tries direct SSH to the VM external IP first; if that fails, retries via IAP automatically.)
#   powershell -NoProfile -File .\upload_linux_build_to_gce_openssh.ps1 -UseIap
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
    [switch] $NoIapFallback
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
if (Test-Path $archivePath) {
    Remove-Item $archivePath -Force
}
Push-Location $sourceParent
try {
    & tar.exe -czf $archivePath $sourceBase
    if ($LASTEXITCODE -ne 0) {
        Write-Error ('tar failed (exit ' + $LASTEXITCODE + ')')
        exit 1
    }
}
finally {
    Pop-Location
}

$remotePrepare = "mkdir -p $TargetDir"
# Windows tar often drops Linux +x; systemd 203/EXEC without chmod on the player binary.
$remoteExtract = "mkdir -p $TargetDir; rm -rf $TargetDir/$sourceBase; tar -xzf $bundleRemote -C $TargetDir; rm -f $bundleRemote; chmod +x $TargetDir/$sourceBase/TitanOrbitServer.x86_64 2>/dev/null; chmod +x $TargetDir/$sourceBase/TitanOrbitServer 2>/dev/null; exit 0"
# Avoid `||` inside double quotes (PS 7+ parses it as the pipeline-chain operator).
$remoteVerify = "ls -la $TargetDir; ls -la $TargetDir/$sourceBase " + '|| true'

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
    }
}

$useIapNow = $UseIap.IsPresent
if (-not $useIapNow) {
    $nat = Get-InstanceNatIp
    if (-not $nat) {
        $useIapNow = $true
        Write-Host 'No external NAT IP on this VM; using IAP.'
    }
    else {
        Write-Host ('[3/4] Direct upload to ' + $nat + ' (OpenSSH; no plink). If this times out, script will retry via IAP ...')
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

        if (-not $directOk) {
            if ($NoIapFallback) {
                Write-Error ('Direct upload failed (last exit ' + $LASTEXITCODE + '). Use -UseIap or fix firewall / sshd on :22.')
                exit 1
            }
            Write-Warning 'Direct SSH failed (timeout / port 22 blocked). Retrying once via IAP (if that fails with 4003, run add_iap_ssh_firewall_and_tag.bat).'
            $useIapNow = $true
        }
    }
}

if ($useIapNow) {
    Invoke-UploadViaIapTunnel
}

Write-Host '[4/4] Cleaning local archive'
if (Test-Path $archivePath) {
    Remove-Item $archivePath -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host 'Upload complete. Next: prepare_and_start_server_on_gce.bat (or restart scripts).'
exit 0
