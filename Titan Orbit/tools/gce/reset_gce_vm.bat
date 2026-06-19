@echo off
setlocal

REM Hard reboot the dedicated GCE VM (same defaults as deploy/restart scripts).
REM Often fixes guest networking, metadata (169.254.169.254), sshd/IAP issues without changing disks.
REM Optional: attach guest_network_recovery_startup.sh as startup-script once (see that file), then reset.
REM After recovery, remove startup-script metadata so it does not run every boot (README).
REM
REM Usage:
REM   reset_gce_vm.bat
REM   reset_gce_vm.bat my-instance
REM   reset_gce_vm.bat my-instance my-gcp-project
REM   reset_gce_vm.bat my-instance my-gcp-project us-central1-a

set "INSTANCE=titanorbitcp"
set "PROJECT_ID=titan-orbit"
set "ZONE=us-central1-f"

if not "%~1"=="" set "INSTANCE=%~1"
if not "%~2"=="" set "PROJECT_ID=%~2"
if not "%~3"=="" set "ZONE=%~3"

where gcloud >nul 2>&1
if errorlevel 1 (
  echo ERROR: gcloud was not found in PATH.
  exit /b 1
)

echo.
echo Resetting VM:  %INSTANCE%
echo Project:       %PROJECT_ID%
echo Zone:          %ZONE%
echo This is a hard reboot ^(brief outage^).
echo.

call gcloud compute instances reset "%INSTANCE%" --project="%PROJECT_ID%" --zone="%ZONE%"
exit /b %errorlevel%
