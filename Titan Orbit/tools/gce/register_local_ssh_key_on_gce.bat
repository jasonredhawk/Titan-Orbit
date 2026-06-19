@echo off
setlocal
REM Pushes %USERPROFILE%\.ssh\google_compute_engine.pub to GCP via gcloud (merges ssh-keys metadata).
REM No need to paste into Cloud Console if this succeeds.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0register_local_ssh_key_on_gce.ps1" %*
exit /b %ERRORLEVEL%
