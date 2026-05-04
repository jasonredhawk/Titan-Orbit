@echo off
setlocal

REM One-step publish: upload via OpenSSH (upload_linux_build_to_gce_openssh.ps1), then restart (restart_server_remote.ps1).
REM Upload does not use gcloud compute scp/ssh (PuTTY plink). Requires ssh.exe, scp.exe, gcloud, tar.
REM
REM Upload Linux headless build to the GCE VM, then restart the dedicated server service.
REM Argument order matches upload_linux_build_to_gce.bat so you can swap paths or project id the same way
REM as tools/gcs/deploy_webgl_gcs.bat does for WebGL.
REM
REM Usage:
REM   deploy_server_gce.bat
REM   deploy_server_gce.bat "C:\path\to\TitanOrbitLinux1"
REM   deploy_server_gce.bat "C:\path\to\TitanOrbitLinux1" your-gcp-project-id
REM   deploy_server_gce.bat "C:\path\to\TitanOrbitLinux1" your-gcp-project-id useIap
REM   deploy_server_gce.bat useIap
REM
REM First-time VM setup: run install_enable_server_service_on_gce.bat once so systemd unit exists.

call "%~dp0upload_linux_build_to_gce.bat" %*
if errorlevel 1 exit /b 1
REM Restart args must match restart_titanorbit_server_on_gce.bat (project, then optional useIap).
if /i "%~1"=="useIap" (
  call "%~dp0restart_titanorbit_server_on_gce.bat" useIap
) else if /i "%~3"=="useIap" (
  call "%~dp0restart_titanorbit_server_on_gce.bat" %~2 useIap
) else if /i "%~2"=="useIap" (
  call "%~dp0restart_titanorbit_server_on_gce.bat" useIap
) else (
  call "%~dp0restart_titanorbit_server_on_gce.bat" %~2 %~3
)
exit /b %errorlevel%
