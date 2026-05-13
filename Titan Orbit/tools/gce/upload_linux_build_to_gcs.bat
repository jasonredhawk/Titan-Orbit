@echo off
setlocal

REM Upload Linux headless server build to Google Cloud Storage (no SSH / no plink / no scp).
REM Use this when Windows "gcloud compute ssh" or "gcloud compute scp" fails.
REM
REM One-time in Google Cloud Console:
REM   1) Create a bucket (example name below) in the same region you use for the VM.
REM   2) Grant the VM's service account "Storage Object Viewer" on that bucket
REM      (often: PROJECT_NUMBER-compute@developer.gserviceaccount.com), or use Console "Permissions".
REM
REM Then from Windows run this script, and from Cloud Shell run the commands in tools\gce\README.md
REM section "Simple deploy: GCS + Cloud Shell".
REM
REM Usage:
REM   upload_linux_build_to_gcs.bat
REM   upload_linux_build_to_gcs.bat "C:\path\to\TitanOrbitLinux1"
REM   upload_linux_build_to_gcs.bat "C:\path\to\TitanOrbitLinux1" your-gcp-project-id
REM   upload_linux_build_to_gcs.bat "C:\path\to\TitanOrbitLinux1" your-gcp-project-id your-bucket-name

for %%I in ("%~dp0..\..") do set "REPO_ROOT=%%~fI"
set "SOURCE_DIR=%REPO_ROOT%\BuildOutput\Server\TitanOrbitLinux1"
set "PROJECT_ID=titan-orbit"
set "BUCKET=titan-orbit-dedicated-server"
set "GCS_PREFIX=titanorbit-linux-build"

if not "%~1"=="" for %%I in ("%~1") do set "SOURCE_DIR=%%~fI"
if not "%~2"=="" set "PROJECT_ID=%~2"
if not "%~3"=="" set "BUCKET=%~3"

for %%I in ("%SOURCE_DIR%") do set "SOURCE_BASENAME=%%~nxI"
for %%I in ("%SOURCE_DIR%") do set "SOURCE_PARENT=%%~dpI"
set "ARCHIVE_PATH=%TEMP%\%SOURCE_BASENAME%.tar.gz"
set "GCS_URI=gs://%BUCKET%/%GCS_PREFIX%/%SOURCE_BASENAME%-latest.tar.gz"

echo.
echo [1/4] Checking gcloud CLI...
where gcloud >nul 2>&1
if errorlevel 1 (
  echo ERROR: gcloud was not found in PATH.
  exit /b 1
)
where tar >nul 2>&1
if errorlevel 1 (
  echo ERROR: tar was not found in PATH.
  exit /b 1
)

echo.
echo [2/4] Verifying source folder...
if not exist "%SOURCE_DIR%" (
  echo ERROR: Source folder not found:
  echo   %SOURCE_DIR%
  echo Build in Unity: TitanOrbit -^> Build -^> Headless Server ^(Linux - Google Cloud^)
  exit /b 1
)

echo.
echo Verifying IL2CPP files before tar - avoids uploading a tree where global-metadata.dat is empty on the VM.
set "META=%SOURCE_DIR%\TitanOrbitServer_Data\il2cpp_data\Metadata\global-metadata.dat"
if not exist "%META%" (
  echo ERROR: Missing metadata file:
  echo   %META%
  echo Quit Unity Editor, rebuild Headless Server ^(Linux^), then retry.
  exit /b 1
)
for %%A in ("%META%") do set "META_SIZE=%%~zA"
if %META_SIZE% LSS 1000000 (
  echo ERROR: global-metadata.dat is only %META_SIZE% bytes ^(need ^>= 1000000^).
  echo Quit Unity / antivirus may have locked the file during a previous tar. Rebuild or retry.
  exit /b 1
)
set "GASM=%SOURCE_DIR%\GameAssembly.so"
if not exist "%GASM%" (
  echo ERROR: Missing GameAssembly.so under %SOURCE_DIR%
  exit /b 1
)
for %%A in ("%GASM%") do set "GASM_SIZE=%%~zA"
if %GASM_SIZE% LSS 10000000 (
  echo ERROR: GameAssembly.so is only %GASM_SIZE% bytes ^(need ^>= 10000000 for IL2CPP^).
  exit /b 1
)

echo.
echo [3/4] Creating archive...
if exist "%ARCHIVE_PATH%" del /f /q "%ARCHIVE_PATH%" >nul 2>&1
REM Exclude Unity IL2CPP backup + Burst debug trees (huge; not needed on server; fills VM disk if packed).
pushd "%SOURCE_PARENT%"
tar -czf "%ARCHIVE_PATH%" ^
  --exclude="%SOURCE_BASENAME%/TitanOrbitServer_BackUpThisFolder_ButDontShipItWithYourGame" ^
  --exclude="%SOURCE_BASENAME%/Titan Orbit_BurstDebugInformation_DoNotShip" ^
  "%SOURCE_BASENAME%"
set "TAR_EXIT=%errorlevel%"
popd
if not "%TAR_EXIT%"=="0" (
  echo ERROR: tar failed.
  exit /b 1
)

echo.
echo [4/4] Uploading to %GCS_URI%
echo Project: %PROJECT_ID%
call gcloud --project "%PROJECT_ID%" storage cp "%ARCHIVE_PATH%" "%GCS_URI%"
if errorlevel 1 (
  echo.
  echo ERROR: Upload failed. Create the bucket if needed:
  echo   gcloud storage buckets create gs://%BUCKET% --project=%PROJECT_ID% --location=us-central1 --uniform-bucket-level-access
  echo Then grant your VM service account roles/storage.objectViewer on gs://%BUCKET%
  exit /b 1
)

if exist "%ARCHIVE_PATH%" del /f /q "%ARCHIVE_PATH%" >nul 2>&1

echo.
echo Upload complete.
echo Next: open Google Cloud Console -^> Cloud Shell ^(terminal icon^) and run the block under "Simple deploy: GCS + Cloud Shell" in tools\gce\README.md
exit /b 0
