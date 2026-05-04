@echo off
setlocal

REM One-step publish: upload via OpenSSH (upload_linux_build_to_gce_openssh.ps1), then either a VM hard reset
REM (gcloud compute instances reset — same as reset_gce_vm.bat) when useIap, or systemd restart (restart_server_remote.ps1).
REM Upload does not use gcloud compute scp/ssh (PuTTY plink). Requires ssh.exe, scp.exe, gcloud, tar.
REM
REM Upload Linux headless build to the GCE VM, then restart the dedicated server (service over SSH, or full VM reset with IAP).
REM Argument order matches upload_linux_build_to_gce.bat so you can swap paths or project id the same way
REM as tools/gcs/deploy_webgl_gcs.bat does for WebGL.
REM
REM Usage:
REM   deploy_server_gce.bat
REM   deploy_server_gce.bat "C:\path\to\TitanOrbitLinux1"
REM   deploy_server_gce.bat "C:\path\to\TitanOrbitLinux1" your-gcp-project-id
REM   deploy_server_gce.bat "C:\path\to\TitanOrbitLinux1" your-gcp-project-id useIap
REM   deploy_server_gce.bat useIap
REM When useIap is set, step 2 is gcloud compute instances reset (reset_gce_vm.bat), not SSH service restart.
REM
REM First-time VM setup: run install_enable_server_service_on_gce.bat once so systemd unit exists.

call "%~dp0upload_linux_build_to_gce.bat" %*
if errorlevel 1 exit /b 1
REM With useIap: hard VM reset (same as reset_gce_vm.bat). Post-upload SSH restart often fails when IAP/plain SSH is flaky.
REM Without useIap: restart systemd unit over SSH (restart_titanorbit_server_on_gce.bat).
if /i "%~1"=="useIap" (
  call "%~dp0reset_gce_vm.bat"
) else if /i "%~3"=="useIap" (
  call "%~dp0reset_gce_vm.bat" titanorbitcp "%~2"
) else if /i "%~2"=="useIap" (
  call "%~dp0reset_gce_vm.bat"
) else (
  call "%~dp0restart_titanorbit_server_on_gce.bat" %~2 %~3
)
exit /b %errorlevel%
