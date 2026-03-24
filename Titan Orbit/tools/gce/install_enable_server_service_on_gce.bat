@echo off
setlocal

REM Installs and enables a systemd service for Titan Orbit headless server on GCE.
REM Usage:
REM   install_enable_server_service_on_gce.bat
REM   install_enable_server_service_on_gce.bat your-gcp-project-id

set "INSTANCE=titan-orbit-compute-engine"
set "ZONE=us-central1-a"
set "PROJECT_ID="
set "REMOTE_USER=jason"
set "INSTANCE_TARGET=%REMOTE_USER%@%INSTANCE%"

set "SERVICE_NAME=titanorbit-server.service"
set "REMOTE_DIR=/home/jason/titanorbit-server/TitanOrbitLinux1"
set "EXE_NAME=TitanOrbitServer.x86_64"
set "RUN_ARGS=-batchmode -nographics --maxPlayers=60 --serverPort=7777 --relayProtocol=wss --isLatest=1 -logFile /home/jason/titanorbit-server/TitanOrbitLinux1/Player.log"

if not "%~1"=="" (
  set "PROJECT_ID=%~1"
)

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
  echo   install_enable_server_service_on_gce.bat your-gcp-project-id
  exit /b 1
)

echo Installing and enabling %SERVICE_NAME%...
echo Using project: %PROJECT_ID%

call gcloud --project %PROJECT_ID% compute ssh %INSTANCE_TARGET% --zone %ZONE% --strict-host-key-checking=no --command "bash -lc 'set -e; test -f %REMOTE_DIR%/%EXE_NAME%; chmod +x %REMOTE_DIR%/%EXE_NAME%; sudo tee /etc/systemd/system/%SERVICE_NAME% >/dev/null <<\"EOF\"
[Unit]
Description=Titan Orbit Dedicated Headless Server
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=%REMOTE_USER%
WorkingDirectory=%REMOTE_DIR%
ExecStart=%REMOTE_DIR%/%EXE_NAME% %RUN_ARGS%
Restart=always
RestartSec=5
KillSignal=SIGINT
TimeoutStopSec=30

[Install]
WantedBy=multi-user.target
EOF
sudo systemctl daemon-reload; sudo systemctl enable --now %SERVICE_NAME%; sudo systemctl status %SERVICE_NAME% --no-pager -l | sed -n \"1,80p\"; echo; echo \"Recent log tail:\"; tail -n 80 /home/jason/titanorbit-server/TitanOrbitLinux1/Player.log || true'"

if errorlevel 1 (
  echo.
  echo ERROR: Failed to install or start systemd service.
  exit /b 1
)

echo.
echo Service installed and enabled.
echo Useful commands:
echo   gcloud --project %PROJECT_ID% compute ssh %INSTANCE_TARGET% --zone %ZONE% --strict-host-key-checking=no --command "bash -lc 'sudo systemctl status %SERVICE_NAME% --no-pager -l'"
echo   gcloud --project %PROJECT_ID% compute ssh %INSTANCE_TARGET% --zone %ZONE% --strict-host-key-checking=no --command "bash -lc 'tail -n 120 /home/jason/titanorbit-server/TitanOrbitLinux1/Player.log'"
echo   gcloud --project %PROJECT_ID% compute ssh %INSTANCE_TARGET% --zone %ZONE% --strict-host-key-checking=no --command "bash -lc 'sudo journalctl -u %SERVICE_NAME% -n 120 --no-pager'"

exit /b 0

