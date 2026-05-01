@echo off
setlocal

REM Prepares uploaded Linux build folder on GCE VM and starts server once (foreground).
REM Usage:
REM   prepare_and_start_server_on_gce.bat
REM   prepare_and_start_server_on_gce.bat TitanOrbitServer.x86_64
REM   prepare_and_start_server_on_gce.bat TitanOrbitServer.x86_64 your-gcp-project-id

set "INSTANCE=titanorbitcp"
set "ZONE=us-central1-f"
set "REMOTE_BASE=/home/jason/titanorbit-server"
set "REMOTE_DIR=/home/jason/titanorbit-server/TitanOrbitLinux1"
set "EXE_NAME="
set "PROJECT_ID=titan-orbit"
set "REMOTE_USER=jason"
set "INSTANCE_TARGET="
set "USE_IAP="

if not "%~1"=="" (
  set "EXE_NAME=%~1"
)
if not "%~2"=="" (
  set "PROJECT_ID=%~2"
)
if /i "%~3"=="useIap" (
  set "USE_IAP=--tunnel-through-iap"
)
set "INSTANCE_TARGET=%REMOTE_USER%@%INSTANCE%"

where gcloud >nul 2>&1
if errorlevel 1 (
  echo ERROR: gcloud was not found in PATH.
  echo Install Google Cloud CLI and run: gcloud init
  exit /b 1
)

echo Preparing remote files...
echo Using project: %PROJECT_ID%
call gcloud --project %PROJECT_ID% compute ssh %INSTANCE_TARGET% %USE_IAP% --zone %ZONE% --strict-host-key-checking=no --quiet --command "bash -lc 'set -e; mkdir -p %REMOTE_BASE%; if [ ! -d %REMOTE_DIR% ]; then CANDIDATE=$(ls -d %REMOTE_BASE%/* 2>/dev/null | head -n1); if [ -n \"$CANDIDATE\" ]; then echo Auto-detected upload folder: $CANDIDATE; ln -sfn \"$CANDIDATE\" %REMOTE_DIR%; fi; fi; cd %REMOTE_DIR%; chmod +x ./*.x86_64 || true; ls -la'"
if errorlevel 1 (
  echo ERROR: Failed while preparing files on VM.
  exit /b 1
)

echo.
echo Starting server in foreground (press Ctrl+C to stop)...
echo Logs also written to %REMOTE_DIR%/Player.log (tail in another SSH session if this looks quiet).
echo Executable: %EXE_NAME%
call gcloud --project %PROJECT_ID% compute ssh %INSTANCE_TARGET% %USE_IAP% --zone %ZONE% --strict-host-key-checking=no --quiet --command "bash -lc 'cd %REMOTE_DIR% && if [ -f ./TitanOrbitServer.x86_64 ]; then EXE=TitanOrbitServer.x86_64; elif [ -f ./TitanOrbitServer ]; then EXE=TitanOrbitServer; elif [ %EXE_NAME%x != x ] && [ -f ./%EXE_NAME% ]; then EXE=%EXE_NAME%; else EXE=$(ls *.x86_64 2>/dev/null | head -n1); fi; if [ -z \"$EXE\" ] || [ ! -f ./$EXE ]; then echo No server binary found in %REMOTE_DIR%; ls -la; exit 1; fi; echo Using executable: $EXE; ./$EXE -batchmode -nographics -logFile ./Player.log --maxPlayers=60 --serverPort=7777 --relayProtocol=udp --serverListenAddress=0.0.0.0 --isLatest=1'"

exit /b %errorlevel%

