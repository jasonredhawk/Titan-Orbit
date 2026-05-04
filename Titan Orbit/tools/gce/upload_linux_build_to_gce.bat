@echo off
setlocal

REM Upload Unity Linux headless build folder to GCE using Windows OpenSSH only (ssh.exe/scp.exe).
REM Does NOT use gcloud compute ssh/scp (PuTTY plink) — that path is unreliable on Windows.
REM
REM Usage:
REM   upload_linux_build_to_gce.bat
REM   upload_linux_build_to_gce.bat useIap
REM   upload_linux_build_to_gce.bat "C:\path\to\build-folder"
REM   upload_linux_build_to_gce.bat "C:\path\to\build-folder" your-gcp-project-id
REM   upload_linux_build_to_gce.bat "C:\path\to\build-folder" your-gcp-project-id useIap
REM Prereq: OpenSSH Client (ssh.exe, scp.exe), gcloud (for IAP tunnel + metadata), tar (used by the PowerShell uploader).

set "INSTANCE=titanorbitcp"
for %%I in ("%~dp0..\..") do set "REPO_ROOT=%%~fI"
set "SOURCE_DIR=%REPO_ROOT%\BuildOutput\Server\TitanOrbitLinux1"
set "PROJECT_ID=titan-orbit"
set "USE_IAP="

if /i "%~1"=="useIap" (
  set "USE_IAP=1"
) else (
  if not "%~1"=="" set "SOURCE_DIR=%~1"
)
if /i "%~2"=="useIap" (
  set "USE_IAP=1"
) else (
  if not "%~2"=="" set "PROJECT_ID=%~2"
)
if /i "%~3"=="useIap" (
  set "USE_IAP=1"
)

echo.
echo [1/2] Checking prerequisites...
where gcloud >nul 2>&1
if errorlevel 1 (
  echo ERROR: gcloud was not found in PATH.
  echo Install Google Cloud CLI and run: gcloud init
  exit /b 1
)
where ssh >nul 2>&1
if errorlevel 1 (
  echo ERROR: ssh.exe not found. Install OpenSSH Client:
  echo   Windows Settings -^> Apps -^> Optional features -^> Add OpenSSH Client
  exit /b 1
)
where scp >nul 2>&1
if errorlevel 1 (
  echo ERROR: scp.exe not found. Install OpenSSH Client ^(same as ssh.exe^).
  exit /b 1
)
where tar >nul 2>&1
if errorlevel 1 (
  echo ERROR: tar was not found in PATH.
  exit /b 1
)

echo.
echo [2/2] Uploading via upload_linux_build_to_gce_openssh.ps1 ^(OpenSSH + gcloud IAP tunnel when needed^)...
echo Instance: %INSTANCE%  Project: %PROJECT_ID%
if not exist "%SOURCE_DIR%" (
  echo ERROR: Source folder not found:
  echo   %SOURCE_DIR%
  exit /b 1
)

echo Pipeline: ssh.exe/scp.exe + %USERPROFILE%\.ssh\google_compute_engine ^(no PuTTY plink^).
if defined USE_IAP (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0upload_linux_build_to_gce_openssh.ps1" -ProjectId "%PROJECT_ID%" -SourceDir "%SOURCE_DIR%" -UseIap
) else (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0upload_linux_build_to_gce_openssh.ps1" -ProjectId "%PROJECT_ID%" -SourceDir "%SOURCE_DIR%"
)
if errorlevel 1 (
  echo.
  echo ERROR: Upload failed. See messages above ^(IAP 4003, keys, or network^).
  echo Docs: tools\gce\README.md
  exit /b 1
)
echo.
echo Upload complete.
echo Next: deploy_server_gce.bat ^(upload + restart^) or restart_titanorbit_server_on_gce.bat useIap
exit /b 0
