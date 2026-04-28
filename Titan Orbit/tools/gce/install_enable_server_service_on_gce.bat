@echo off
setlocal

REM Installs and enables a systemd service for Titan Orbit headless server on GCE.
REM Usage:
REM   install_enable_server_service_on_gce.bat
REM   install_enable_server_service_on_gce.bat useIap
REM   install_enable_server_service_on_gce.bat your-gcp-project-id
REM   install_enable_server_service_on_gce.bat your-gcp-project-id useIap
REM If plink times out on Windows, use useIap or install_enable_server_service_on_gce_iap.bat

set "INSTANCE=titan-orbit-compute-engine"
set "ZONE=us-central1-a"
set "PROJECT_ID=titan-orbit"
set "REMOTE_USER=jason"
set "USE_IAP="
set "INSTANCE_TARGET=%REMOTE_USER%@%INSTANCE%"

set "SERVICE_NAME=titanorbit-server.service"
set "REMOTE_DIR=/home/jason/titanorbit-server/TitanOrbitLinux1"
REM ExecStart / binary name live in titanorbit-server.service ^(TitanOrbitServer or .x86_64 per Unity build^).

if /i "%~1"=="useIap" (
  set "USE_IAP=--tunnel-through-iap"
  if not "%~2"=="" set "PROJECT_ID=%~2"
) else (
  if not "%~1"=="" set "PROJECT_ID=%~1"
)
if /i "%~2"=="useIap" (
  set "USE_IAP=--tunnel-through-iap"
)

where gcloud >nul 2>&1
if errorlevel 1 (
  echo ERROR: gcloud was not found in PATH.
  echo Install Google Cloud CLI and run: gcloud init
  exit /b 1
)

echo Installing and enabling %SERVICE_NAME%...
echo Using project: %PROJECT_ID%
if not "%USE_IAP%"=="" echo Using IAP tunnel for SSH.

set "LOCAL_UNIT=%~dp0titanorbit-server.service"
if not exist "%LOCAL_UNIT%" (
  echo ERROR: Missing unit file next to this script:
  echo   %LOCAL_UNIT%
  exit /b 1
)

where powershell >nul 2>&1
if errorlevel 1 (
  echo ERROR: PowerShell not found in PATH.
  exit /b 1
)

echo Installing unit via PowerShell ^(no gcloud scp / pscp - remote script via base64 in gcloud --command^) ...
set "PS_EXTRA="
if not "%USE_IAP%"=="" set "PS_EXTRA=-UseIap"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install_unit_remote.ps1" -ProjectId "%PROJECT_ID%" -Zone "%ZONE%" -InstanceTarget "%INSTANCE_TARGET%" -RemoteDir "%REMOTE_DIR%" %PS_EXTRA%
if errorlevel 1 (
  echo.
  echo ERROR: Failed to install or start systemd service.
  call :InstallSshHints
  exit /b 1
)

echo.
echo Service installed and enabled.
echo Useful commands:
echo   gcloud --project %PROJECT_ID% compute ssh %INSTANCE_TARGET% %USE_IAP% --zone %ZONE% --strict-host-key-checking=no --command "bash -lc 'sudo systemctl status %SERVICE_NAME% --no-pager -l'"
echo   gcloud --project %PROJECT_ID% compute ssh %INSTANCE_TARGET% %USE_IAP% --zone %ZONE% --strict-host-key-checking=no --command "bash -lc 'tail -n 120 /home/jason/titanorbit-server/TitanOrbitLinux1/Player.log'"
echo   gcloud --project %PROJECT_ID% compute ssh %INSTANCE_TARGET% %USE_IAP% --zone %ZONE% --strict-host-key-checking=no --command "bash -lc 'sudo journalctl -u %SERVICE_NAME% -n 120 --no-pager'"

exit /b 0

:InstallSshHints
echo.
echo If ^"Connection timed out^" or ^"remote closed^": try IAP:  install_enable_server_service_on_gce_iap.bat
echo With useIap: read **Windows first-time setup** in tools\gce\README.md ^(OpenSSH Client + first gcloud ssh to create the key^).
echo Force old plink IAP:  powershell -NoProfile -File ^"%~dp0install_unit_remote.ps1^" -ProjectId ... -UseIap -UsePlinkWithIap
echo Or Cloud Console -^> VM -^> SSH and install tools/gce/titanorbit-server.service manually.
echo IAP ^"remote closed^" often means IAM ^(iap.tunnelResourceAccessor^) or OS Login / username mismatch.
echo IAP error 4003 / failed to connect port 22: run add_iap_ssh_firewall_and_tag.bat ^(or README: IAP tunnel error 4003^).
echo If IAP tunnel works but ssh resets ^(kex_exchange_identification^): run write_cloudshell_install_unit_script.bat then run the .sh in Cloud Shell.
goto :eof

