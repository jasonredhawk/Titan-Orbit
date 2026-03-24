@echo off
setlocal

REM Prepares uploaded Linux build folder on GCE VM and starts server once (foreground).
REM Usage:
REM   prepare_and_start_server_on_gce.bat
REM   prepare_and_start_server_on_gce.bat TitanOrbitServer.x86_64
REM   prepare_and_start_server_on_gce.bat TitanOrbitServer.x86_64 your-gcp-project-id

set "INSTANCE=titan-orbit-compute-engine"
set "ZONE=us-central1-a"
set "REMOTE_BASE=/home/jason/titanorbit-server"
set "REMOTE_DIR=/home/jason/titanorbit-server/TitanOrbitLinux1"
set "EXE_NAME=TitanOrbitServer.x86_64"
set "PROJECT_ID="
set "REMOTE_USER=jason"
set "INSTANCE_TARGET="

if not "%~1"=="" (
  set "EXE_NAME=%~1"
)
if not "%~2"=="" (
  set "PROJECT_ID=%~2"
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
  echo Pass project id as 2nd arg, e.g.:
  echo   prepare_and_start_server_on_gce.bat %EXE_NAME% your-gcp-project-id
  exit /b 1
)

echo Preparing remote files...
echo Using project: %PROJECT_ID%
call gcloud --project %PROJECT_ID% compute ssh %INSTANCE_TARGET% --zone %ZONE% --strict-host-key-checking=no --command "bash -lc 'set -e; mkdir -p %REMOTE_BASE%; if [ ! -d %REMOTE_DIR% ]; then CANDIDATE=$(ls -d %REMOTE_BASE%/* 2>/dev/null | head -n1); if [ -n \"$CANDIDATE\" ]; then echo Auto-detected upload folder: $CANDIDATE; ln -sfn \"$CANDIDATE\" %REMOTE_DIR%; fi; fi; cd %REMOTE_DIR%; chmod +x ./*.x86_64 || true; ls -la'"
if errorlevel 1 (
  echo ERROR: Failed while preparing files on VM.
  exit /b 1
)

echo.
echo Starting server in foreground (press Ctrl+C to stop)...
echo Logs also written to %REMOTE_DIR%/Player.log (tail in another SSH session if this looks quiet).
echo Executable: %EXE_NAME%
call gcloud --project %PROJECT_ID% compute ssh %INSTANCE_TARGET% --zone %ZONE% --strict-host-key-checking=no --command "bash -lc 'cd %REMOTE_DIR% && if [ ! -f ./%EXE_NAME% ]; then EXE=$(ls *.x86_64 2>/dev/null | head -n1); if [ -z \"$EXE\" ]; then echo No .x86_64 executable found in %REMOTE_DIR%; exit 1; fi; echo Using detected executable: $EXE; else EXE=%EXE_NAME%; fi; ./$EXE -batchmode -nographics -logFile ./Player.log --maxPlayers=60 --serverPort=7777 --relayProtocol=wss --isLatest=1'"

exit /b %errorlevel%

