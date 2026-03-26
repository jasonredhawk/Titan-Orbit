@echo off
setlocal

REM Opens an interactive SSH shell to your GCE VM.
REM Usage:
REM   open_gce_shell.bat
REM   open_gce_shell.bat your-gcp-project-id

set "INSTANCE=titan-orbit-compute-engine"
set "ZONE=us-central1-a"
set "PROJECT_ID="
set "REMOTE_USER=jason"
set "INSTANCE_TARGET="
set "USE_IAP="

if not "%~1"=="" (
  set "PROJECT_ID=%~1"
)
if /i "%~2"=="useIap" (
  set "USE_IAP=--tunnel-through-iap"
)
set "INSTANCE_TARGET=%REMOTE_USER%@%INSTANCE%"

where gcloud >nul 2>&1
if errorlevel 1 (
  echo ERROR: gcloud was not found in PATH.
  echo Install Google Cloud CLI and run: gcloud init
  exit /b 1
)

if "%PROJECT_ID%"=="" (
  for /f "usebackq delims=" %%P in (`call gcloud config get-value project 2^>nul`) do set "PROJECT_ID=%%P"
)
if "%PROJECT_ID%"=="" (
  echo ERROR: Could not determine GCP project id.
  echo Pass project id as first arg, e.g.:
  echo   open_gce_shell.bat your-gcp-project-id
  exit /b 1
)

echo Using project: %PROJECT_ID%
call gcloud --project %PROJECT_ID% compute ssh %INSTANCE_TARGET% %USE_IAP% --zone %ZONE% --strict-host-key-checking=no
exit /b %errorlevel%

