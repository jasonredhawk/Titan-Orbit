@echo off
setlocal

REM Sync local WebGL build folder to a GCS bucket (mirror: removes remote objects not present locally).
REM This only uploads bytes. Serving (HTTPS, website config, DNS, public read) is separate.
REM After upload, run set_webgl_gcs_metadata.bat (or deploy_webgl_gcs.bat) for Brotli Content-Encoding.
REM
REM Usage:
REM   upload_webgl_to_gcs.bat
REM   upload_webgl_to_gcs.bat "C:\path\to\TitanOrbitWebGL"
REM   upload_webgl_to_gcs.bat "C:\path\to\TitanOrbitWebGL" your-gcp-project-id

REM Defaults (edit if your bucket name is taken or you use another build folder)
set "BUCKET=titan-orbit-webgl"
set "PROJECT_ID=titan-orbit"
set "SOURCE_DIR=C:\Users\jason\Documents\Titan Orbit\Downloads\TitanOrbitWeb1"

if not "%~1"=="" for %%I in ("%~1") do set "SOURCE_DIR=%%~fI"
if not "%~2"=="" set "PROJECT_ID=%~2"

echo.
echo [1/3] Checking gcloud CLI...
where gcloud >nul 2>&1
if errorlevel 1 (
  echo ERROR: gcloud was not found in PATH.
  echo Install Google Cloud CLI: https://cloud.google.com/sdk/docs/install
  exit /b 1
)

if "%PROJECT_ID%"=="" (
  for /f "usebackq delims=" %%P in (`call gcloud config get-value project 2^>nul`) do set "PROJECT_ID=%%P"
)
if "%PROJECT_ID%"=="" (
  echo ERROR: Could not determine GCP project id.
  echo Edit PROJECT_ID= in this script, or: gcloud config set project titan-orbit
  echo Or pass project as 2nd arg:
  echo   %~nx0 "%SOURCE_DIR%" your-gcp-project-id
  exit /b 1
)

echo.
echo [2/3] Verifying source folder...
if not exist "%SOURCE_DIR%" (
  echo ERROR: Source folder not found:
  echo   %SOURCE_DIR%
  exit /b 1
)
if not exist "%SOURCE_DIR%\index.html" (
  echo ERROR: index.html missing - build WebGL first ^(TitanOrbit -^> Build -^> WebGL Production^).
  exit /b 1
)

echo.
echo [3/3] Syncing to gs://%BUCKET%/
echo Project: %PROJECT_ID%
echo Source:  %SOURCE_DIR%
echo.
call gcloud --project "%PROJECT_ID%" storage rsync "%SOURCE_DIR%" "gs://%BUCKET%/" --recursive --delete-unmatched-destination-objects
if errorlevel 1 (
  echo.
  echo ERROR: gcloud storage rsync failed.
  echo If your SDK is old, try: gsutil -m rsync -r -d "%SOURCE_DIR%" "gs://%BUCKET%/"
  exit /b 1
)

echo.
echo Upload sync complete.
echo Next: run set_webgl_gcs_metadata.bat ^(same args^) or deploy_webgl_gcs.bat
exit /b 0
