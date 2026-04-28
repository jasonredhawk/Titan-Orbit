@echo off

setlocal



REM Restarts the systemd dedicated server on the GCE VM (pick up a newly uploaded Linux build).

REM Uses restart_server_remote.ps1 (same SSH strategy as install_unit_remote.ps1) so Windows plink does not hang.

REM Requires install_enable_server_service_on_gce.bat to have been run once on that VM.

REM

REM Usage:

REM   restart_titanorbit_server_on_gce.bat

REM   restart_titanorbit_server_on_gce.bat your-gcp-project-id

REM   restart_titanorbit_server_on_gce.bat useIap

REM   restart_titanorbit_server_on_gce.bat useIap plainFirst   (plain SSH first, then IAP)

REM   restart_titanorbit_server_on_gce.bat your-gcp-project-id useIap

REM   restart_titanorbit_server_on_gce.bat your-gcp-project-id useIap plainFirst

REM IAP path uses start-iap-tunnel + ssh.exe (not gcloud compute ssh --tunnel-through-iap / plink).

REM Or: restart_titanorbit_server_on_gce_iap.bat  (useIap + plainFirst)



set "INSTANCE=titan-orbit-compute-engine"

set "ZONE=us-central1-a"

set "REMOTE_USER=jason"

set "SERVICE_NAME=titanorbit-server"

set "PROJECT_ID=titan-orbit"

set "PS_EXTRA="

set "INSTANCE_TARGET="



if /i "%~1"=="useIap" (

  set "PS_EXTRA=-UseIap"

  if /i "%~2"=="plainFirst" (

    set "PS_EXTRA=-UseIap -PlainSshFirst"

    if not "%~3"=="" set "PROJECT_ID=%~3"

  ) else (

    if not "%~2"=="" set "PROJECT_ID=%~2"

  )

) else (

  if not "%~1"=="" set "PROJECT_ID=%~1"

)

if /i "%~2"=="useIap" (

  set "PS_EXTRA=-UseIap"

  if /i "%~3"=="plainFirst" (

    set "PS_EXTRA=-UseIap -PlainSshFirst"

  )

)

set "INSTANCE_TARGET=%REMOTE_USER%@%INSTANCE%"



where gcloud >nul 2>&1

if errorlevel 1 (

  echo ERROR: gcloud was not found in PATH.

  echo Install Google Cloud CLI and run: gcloud init

  exit /b 1

)

where powershell >nul 2>&1

if errorlevel 1 (

  echo ERROR: PowerShell not found in PATH.

  exit /b 1

)



echo Restarting %SERVICE_NAME% on %INSTANCE% (%ZONE%)...

echo Using project: %PROJECT_ID%

if not "%PS_EXTRA%"=="" echo Remote: %PS_EXTRA% ^(see restart_server_remote.ps1^).

echo.



powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0restart_server_remote.ps1" -ProjectId "%PROJECT_ID%" -Zone "%ZONE%" -InstanceTarget "%INSTANCE_TARGET%" -ServiceName "%SERVICE_NAME%" %PS_EXTRA%

exit /b %errorlevel%

