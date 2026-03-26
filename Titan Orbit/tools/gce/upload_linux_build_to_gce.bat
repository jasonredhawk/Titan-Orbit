@echo off
setlocal

REM Upload Unity Linux headless build folder to Google Compute Engine VM.
REM Usage:
REM   upload_linux_build_to_gce.bat
REM   upload_linux_build_to_gce.bat "C:\path\to\build-folder"
REM   upload_linux_build_to_gce.bat "C:\path\to\build-folder" your-gcp-project-id

set "INSTANCE=titan-orbit-compute-engine"
set "ZONE=us-central1-a"
set "SOURCE_DIR=C:\Users\jason\Documents\Titan Orbit\Downloads\TitanOrbitLinux1"
set "REMOTE_USER=jason"
set "TARGET_DIR=/home/jason/titanorbit-server"
set "PROJECT_ID="
set "SOURCE_BASENAME="
set "SOURCE_PARENT="
set "INSTANCE_TARGET="
set "ARCHIVE_PATH="
set "USE_IAP="

if not "%~1"=="" (
  set "SOURCE_DIR=%~1"
)
if not "%~2"=="" (
  set "PROJECT_ID=%~2"
)
if /i "%~3"=="useIap" (
  set "USE_IAP=--tunnel-through-iap"
)
for %%I in ("%SOURCE_DIR%") do set "SOURCE_BASENAME=%%~nxI"
for %%I in ("%SOURCE_DIR%") do set "SOURCE_PARENT=%%~dpI"
set "INSTANCE_TARGET=%REMOTE_USER%@%INSTANCE%"
set "ARCHIVE_PATH=%TEMP%\%SOURCE_BASENAME%.tar.gz"

echo.
echo [1/3] Checking gcloud CLI...
where gcloud >nul 2>&1
if errorlevel 1 (
  echo ERROR: gcloud was not found in PATH.
  echo Install Google Cloud CLI and run: gcloud init
  exit /b 1
)
where tar >nul 2>&1
if errorlevel 1 (
  echo ERROR: tar was not found in PATH.
  echo Install tar or run from Windows 10/11 shell with built-in tar support.
  exit /b 1
)

if "%PROJECT_ID%"=="" (
  for /f "usebackq delims=" %%P in (`call gcloud config get-value project 2^>nul`) do set "PROJECT_ID=%%P"
)
if "%PROJECT_ID%"=="" (
  echo ERROR: Could not determine GCP project id.
  echo Pass project id as 2nd arg, e.g.:
  echo   upload_linux_build_to_gce.bat "%SOURCE_DIR%" your-gcp-project-id
  exit /b 1
)

echo.
echo [2/3] Verifying source folder...
if not exist "%SOURCE_DIR%" (
  echo ERROR: Source folder not found:
  echo   %SOURCE_DIR%
  exit /b 1
)

echo.
echo [3/3] Uploading build to %INSTANCE% (%ZONE%)...
echo Using project: %PROJECT_ID%
echo Creating local archive...
if exist "%ARCHIVE_PATH%" del /f /q "%ARCHIVE_PATH%" >nul 2>&1
pushd "%SOURCE_PARENT%"
if errorlevel 1 (
  echo ERROR: Failed to enter source parent folder:
  echo   %SOURCE_PARENT%
  exit /b 1
)
tar -czf "%ARCHIVE_PATH%" "%SOURCE_BASENAME%"
set "TAR_EXIT=%errorlevel%"
popd
if not "%TAR_EXIT%"=="0" (
  echo ERROR: tar failed while creating archive from:
  echo   %SOURCE_DIR%
  exit /b 1
)
if not exist "%ARCHIVE_PATH%" (
  echo ERROR: Failed to create local archive:
  echo   %ARCHIVE_PATH%
  exit /b 1
)
echo Preparing remote target directory...
call gcloud --project "%PROJECT_ID%" compute ssh %INSTANCE_TARGET% %USE_IAP% --zone %ZONE% --strict-host-key-checking=no --command "bash -lc 'mkdir -p %TARGET_DIR%'"
if errorlevel 1 (
  echo ERROR: Failed to create target directory on VM.
  exit /b 1
)

echo Command:
echo   gcloud --project "%PROJECT_ID%" compute scp %USE_IAP% "%ARCHIVE_PATH%" "%INSTANCE_TARGET%:/tmp/%SOURCE_BASENAME%.tar.gz" --zone "%ZONE%" --strict-host-key-checking=no
call gcloud --project "%PROJECT_ID%" compute scp %USE_IAP% "%ARCHIVE_PATH%" "%INSTANCE_TARGET%:/tmp/%SOURCE_BASENAME%.tar.gz" --zone "%ZONE%" --strict-host-key-checking=no
if errorlevel 1 (
  echo.
  echo Upload failed while copying archive.
  exit /b 1
)
echo Extracting archive on VM...
call gcloud --project "%PROJECT_ID%" compute ssh %INSTANCE_TARGET% %USE_IAP% --zone %ZONE% --strict-host-key-checking=no --command "bash -lc 'mkdir -p %TARGET_DIR%; rm -rf %TARGET_DIR%/%SOURCE_BASENAME%; tar -xzf /tmp/%SOURCE_BASENAME%.tar.gz -C %TARGET_DIR%; rm -f /tmp/%SOURCE_BASENAME%.tar.gz'"
if errorlevel 1 (
  echo.
  echo Upload failed while extracting archive on VM.
  exit /b 1
)
echo Verifying remote upload...
call gcloud --project "%PROJECT_ID%" compute ssh %INSTANCE_TARGET% %USE_IAP% --zone %ZONE% --strict-host-key-checking=no --command "bash -lc 'ls -la %TARGET_DIR%; ls -la %TARGET_DIR%/%SOURCE_BASENAME% || true'"
if exist "%ARCHIVE_PATH%" del /f /q "%ARCHIVE_PATH%" >nul 2>&1

echo.
echo Upload complete.
echo Next: run prepare_and_start_server_on_gce.bat to chmod and start.
exit /b 0

