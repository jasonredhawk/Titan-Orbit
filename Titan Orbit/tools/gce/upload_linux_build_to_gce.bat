@echo off
setlocal

REM Upload Unity Linux headless build folder to Google Compute Engine VM.
REM Usage:
REM   upload_linux_build_to_gce.bat
REM   upload_linux_build_to_gce.bat useIap
REM   upload_linux_build_to_gce.bat "C:\path\to\build-folder"
REM   upload_linux_build_to_gce.bat "C:\path\to\build-folder" your-gcp-project-id
REM   upload_linux_build_to_gce.bat "C:\path\to\build-folder" your-gcp-project-id useIap
REM If SSH times out (home ISP, office firewall, VM without public IP), add useIap as shown or as the only argument.

set "INSTANCE=titanorbitcp"
set "ZONE=us-central1-f"
REM Default: same folder as TitanOrbit → Build → Headless Server (Linux — Google Cloud) in TitanOrbitBuildAutomation.cs
for %%I in ("%~dp0..\..") do set "REPO_ROOT=%%~fI"
set "SOURCE_DIR=%REPO_ROOT%\BuildOutput\Server\TitanOrbitLinux1"
set "REMOTE_USER=jason"
set "TARGET_DIR=/home/jason/titanorbit-server"
REM Titan Orbit GCP project (do not rely on gcloud config — may point at another repo).
set "PROJECT_ID=titan-orbit"
set "SOURCE_BASENAME="
set "SOURCE_PARENT="
set "INSTANCE_TARGET="
set "ARCHIVE_PATH="
set "USE_IAP="

if /i "%~1"=="useIap" (
  set "USE_IAP=--tunnel-through-iap"
) else (
  if not "%~1"=="" set "SOURCE_DIR=%~1"
)
if /i "%~2"=="useIap" (
  set "USE_IAP=--tunnel-through-iap"
) else (
  if not "%~2"=="" set "PROJECT_ID=%~2"
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
  call :SshUploadFailedHints
  exit /b 1
)

echo Command:
echo   gcloud --project "%PROJECT_ID%" compute scp %USE_IAP% "%ARCHIVE_PATH%" "%INSTANCE_TARGET%:/tmp/%SOURCE_BASENAME%.tar.gz" --zone "%ZONE%" --strict-host-key-checking=no
call gcloud --project "%PROJECT_ID%" compute scp %USE_IAP% "%ARCHIVE_PATH%" "%INSTANCE_TARGET%:/tmp/%SOURCE_BASENAME%.tar.gz" --zone "%ZONE%" --strict-host-key-checking=no
if errorlevel 1 (
  echo.
  echo Upload failed while copying archive.
  call :SshUploadFailedHints
  exit /b 1
)
echo Extracting archive on VM...
call gcloud --project "%PROJECT_ID%" compute ssh %INSTANCE_TARGET% %USE_IAP% --zone %ZONE% --strict-host-key-checking=no --command "bash -lc 'mkdir -p %TARGET_DIR%; rm -rf %TARGET_DIR%/%SOURCE_BASENAME%; tar -xzf /tmp/%SOURCE_BASENAME%.tar.gz -C %TARGET_DIR%; rm -f /tmp/%SOURCE_BASENAME%.tar.gz'"
if errorlevel 1 (
  echo.
  echo Upload failed while extracting archive on VM.
  call :SshUploadFailedHints
  exit /b 1
)
REM Windows tar often strips execute bits; systemd reports 203/EXEC without chmod +x on the player.
echo chmod +x Linux player binaries on VM...
REM Avoid find / 2>/dev/null here: cmd/plink mangles ^> so bash sees broken tokens (e.g. 2^^).
call gcloud --project "%PROJECT_ID%" compute ssh %INSTANCE_TARGET% %USE_IAP% --zone %ZONE% --strict-host-key-checking=no --command "bash -lc 'if [ -f %TARGET_DIR%/%SOURCE_BASENAME%/TitanOrbitServer.x86_64 ]; then chmod +x %TARGET_DIR%/%SOURCE_BASENAME%/TitanOrbitServer.x86_64; fi; if [ -f %TARGET_DIR%/%SOURCE_BASENAME%/TitanOrbitServer ]; then chmod +x %TARGET_DIR%/%SOURCE_BASENAME%/TitanOrbitServer; fi; exit 0'"
if errorlevel 1 (
  echo.
  echo WARNING: chmod step failed; service may still hit 203/EXEC until binaries are executable.
)
echo Verifying remote upload...
call gcloud --project "%PROJECT_ID%" compute ssh %INSTANCE_TARGET% %USE_IAP% --zone %ZONE% --strict-host-key-checking=no --command "bash -lc 'ls -la %TARGET_DIR%; ls -la %TARGET_DIR%/%SOURCE_BASENAME% || true; if [ -f %TARGET_DIR%/%SOURCE_BASENAME%/TitanOrbitServer.x86_64 ]; then ls -l %TARGET_DIR%/%SOURCE_BASENAME%/TitanOrbitServer.x86_64; test -x %TARGET_DIR%/%SOURCE_BASENAME%/TitanOrbitServer.x86_64 && echo OK_executable || echo WARN_not_executable; fi; if [ -f %TARGET_DIR%/%SOURCE_BASENAME%/TitanOrbitServer ]; then ls -l %TARGET_DIR%/%SOURCE_BASENAME%/TitanOrbitServer; test -x %TARGET_DIR%/%SOURCE_BASENAME%/TitanOrbitServer && echo OK_executable_extensionless || echo WARN_not_executable_extensionless; fi'"
if exist "%ARCHIVE_PATH%" del /f /q "%ARCHIVE_PATH%" >nul 2>&1

echo.
echo Upload complete.
echo Next: run prepare_and_start_server_on_gce.bat to chmod and start.
exit /b 0

:SshUploadFailedHints
echo.
echo --- SSH failed ^(often Windows plink timeout^) ---
echo 0. Bypass PuTTY: upload_linux_build_to_gce_openssh.bat ^(direct then auto-IAP^) or useIap for IAP-only
echo    ^(Windows ssh.exe/scp.exe — same keys under %USERPROFILE%\.ssh\google_compute_engine^)
echo 1. In Cloud Console -^> Compute Engine -^> VM -^> SSH button ^(browser^). If that works, use WSL gcloud
echo    or WinSCP + %USERPROFILE%\.ssh\google_compute_engine until local plink works.
echo 2. Confirm Linux user exists: scripts use %INSTANCE_TARGET% ^(user must exist on VM^).
echo 3. Optional IAP:  %~nx0 useIap   or   upload_linux_build_to_gce_iap.bat
echo    ^(If IAP shows ^"remote closed^", fix VM user / sshd / IAP IAM — see tools/gce/README.md^)
echo 4. Exclude plink.exe from antivirus; try another network.
goto :eof

