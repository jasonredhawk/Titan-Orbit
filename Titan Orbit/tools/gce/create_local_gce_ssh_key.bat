@echo off
setlocal
echo Creates a GCE SSH key under your Windows user folder and copies the public line to the clipboard.
echo See README: Windows first-time setup.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0create_local_gce_ssh_key.ps1" %*
exit /b %ERRORLEVEL%
