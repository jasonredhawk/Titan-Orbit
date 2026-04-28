# Creates an IAP-friendly SSH firewall rule on the SAME VPC the VM uses, and adds network tag allow-iap-ssh.
# Fixes IAP error 4003 when an older rule lived on "default" but the VM is on another VPC (per-network rule name).

param(
    [Parameter(Position = 0)]
    [string] $ProjectId = "titan-orbit",
    [Parameter(Position = 1)]
    [string] $NetworkOverride = "",
    [string] $Zone = "us-central1-a",
    [string] $Instance = "titan-orbit-compute-engine",
    [string] $Tag = "allow-iap-ssh"
)

$ErrorActionPreference = "Stop"
if ($null -ne (Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue)) {
    $PSNativeCommandUseErrorActionPreference = $false
}
$iapRange = "35.235.240.0/20"

# Prefer gcloud.cmd (see install_unit_remote.ps1).
$gcloudEntry = (Get-Command gcloud -ErrorAction Stop).Source
$gcloudDir = Split-Path $gcloudEntry
$gcloudCmdPath = Join-Path $gcloudDir "gcloud.cmd"
if (Test-Path $gcloudCmdPath) {
    $gcloudExe = $gcloudCmdPath
}
else {
    $gcloudExe = $gcloudEntry
}

# Run gcloud without piping stderr through PowerShell (PS 7+ treats gcloud ERROR lines as terminating even with 2>$null).
function Invoke-GcloudCapture {
    param(
        [Parameter(Mandatory = $true)]
        [string[]] $GcloudArgumentList
    )
    $outFile = Join-Path $env:TEMP ("titanorbit-gcloud-" + [Guid]::NewGuid().ToString() + ".out.txt")
    $errFile = Join-Path $env:TEMP ("titanorbit-gcloud-" + [Guid]::NewGuid().ToString() + ".err.txt")
    try {
        $proc = Start-Process -FilePath $gcloudExe `
            -ArgumentList $GcloudArgumentList `
            -Wait -PassThru -NoNewWindow `
            -RedirectStandardOutput $outFile `
            -RedirectStandardError $errFile
        $exit = $proc.ExitCode
        $stdout = ""
        $stderr = ""
        if (Test-Path $outFile) {
            $stdout = [System.IO.File]::ReadAllText($outFile)
        }
        if (Test-Path $errFile) {
            $stderr = [System.IO.File]::ReadAllText($errFile)
        }
        return [pscustomobject]@{
            ExitCode = $exit
            Stdout   = $stdout
            Stderr   = $stderr
        }
    }
    finally {
        Remove-Item $outFile, $errFile -Force -ErrorAction SilentlyContinue
    }
}

function Invoke-Gcloud {
    # Do not name this parameter $Args — it clashes with PowerShell's automatic $Args and splatting becomes an empty array.
    param([string[]] $GcloudArgs)
    $r = Invoke-GcloudCapture -GcloudArgumentList $GcloudArgs
    if ($r.ExitCode -ne 0) {
        $err = ($r.Stderr).Trim()
        if (-not $err) { $err = "(no stderr)" }
        throw "gcloud failed (exit $($r.ExitCode)): gcloud $($GcloudArgs -join ' ')`n$err"
    }
    $out = ($r.Stdout).Trim()
    if ($out) {
        Write-Host $out
    }
}

Write-Host "Project: $ProjectId  Zone: $Zone  Instance: $Instance"
Write-Host ""

$instResult = Invoke-GcloudCapture -GcloudArgumentList @(
    "compute", "instances", "describe", $Instance, "--zone=$Zone", "--project=$ProjectId", "--format=json"
)
if ($instResult.ExitCode -ne 0 -or -not ($instResult.Stdout).Trim()) {
    $e = ($instResult.Stderr).Trim()
    if (-not $e) { $e = "(no stderr)" }
    throw "Could not read instance (exit $($instResult.ExitCode)): $e"
}
$instJson = ($instResult.Stdout).Trim() | ConvertFrom-Json
if (-not $instJson) {
    throw "Could not parse instance JSON."
}
$netUrl = $instJson.networkInterfaces[0].network
if (-not $netUrl) {
    throw "Instance has no networkInterfaces[0].network (unexpected API shape)."
}
$network = ($netUrl.ToString().Trim() -split "/")[-1]
if ($NetworkOverride) {
    $network = $NetworkOverride
}
Write-Host "VM VPC network: $network"
Write-Host ""

$sanitized = ($network.ToLower() -replace "[^a-z0-9-]", "-").Trim("-")
if (-not $sanitized) { $sanitized = "net" }
$rule = "iap-allow-ssh-$sanitized"
if ($rule.Length -gt 63) {
    $rule = $rule.Substring(0, 63).TrimEnd("-")
}

Write-Host "[1/2] Firewall rule: $rule (allow tcp:22 from $iapRange to instances with tag $Tag)"
$probe = Invoke-GcloudCapture -GcloudArgumentList @("compute", "firewall-rules", "describe", $rule, "--project=$ProjectId")
if ($probe.ExitCode -eq 0) {
    $netProbe = Invoke-GcloudCapture -GcloudArgumentList @(
        "compute", "firewall-rules", "describe", $rule, "--project=$ProjectId", "--format=value(network)"
    )
    $ruleNetUrl = ($netProbe.Stdout).Trim()
    $ruleNet = ($ruleNetUrl -split "/")[-1]
    Write-Host "Rule already exists, attached to VPC network: $ruleNet"
    if ($ruleNet -ne $network) {
        Write-Warning "Rule '$rule' is on network '$ruleNet' but this VM uses '$network'. Delete or rename the old rule in Console, then re-run this script."
    }
}
else {
    Write-Host "Creating firewall rule on network '$network'..."
    Invoke-Gcloud @(
        "compute", "firewall-rules", "create", $rule,
        "--project=$ProjectId",
        "--network=$network",
        "--direction=INGRESS",
        "--action=ALLOW",
        "--rules=tcp:22",
        "--source-ranges=$iapRange",
        "--target-tags=$Tag",
        # No spaces in --description: Start-Process ArgumentList is not reliably quoted for gcloud on Windows.
        "--description=TitanOrbit-IAP-TCP22-from-35.235.240.0-slash-20"
    )
    Write-Host "Created."
}

Write-Host ""
Write-Host "[2/2] Network tag '$Tag' on instance '$Instance'..."
$existing = @()
if ($instJson.tags -and $instJson.tags.items) {
    $existing = @($instJson.tags.items)
}
if ($existing -contains $Tag) {
    Write-Host "Tag already present. Tags: $($existing -join ', ')"
}
else {
    Write-Host "Adding tag (existing tags are kept)..."
    Invoke-Gcloud @("compute", "instances", "add-tags", $Instance, "--zone=$Zone", "--project=$ProjectId", "--tags=$Tag")
    Write-Host "Added tag '$Tag'."
}

Write-Host ""
Write-Host "Done. Wait about 60 seconds, then run:  install_enable_server_service_on_gce.bat useIap"
Write-Host "Direct SSH without IAP can still time out from home networks; use useIap when that happens."
