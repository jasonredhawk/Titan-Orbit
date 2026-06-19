@echo off
setlocal
REM Runs vm_free_disk_for_server_upload.sh on your GCE VM via gcloud compute ssh only (no scp/pscp).
REM Windows gcloud uses PuTTY pscp for "compute scp", which often fails with "error while writing"
REM when /tmp is full or the tunnel is flaky — same class of failure as tarball uploads.
REM This .bat delegates to vm_free_disk_for_server_upload_gce.ps1 (base64-in-ssh-command; see README / install_unit_remote.ps1).
REM
REM From PowerShell or cmd (from repo):
REM   cd "...\Titan Orbit\tools\gce"
REM   vm_free_disk_for_server_upload_gce.bat
REM   vm_free_disk_for_server_upload_gce.bat useIap
REM   vm_free_disk_for_server_upload_gce.bat useIap aggressive
REM
REM Optional env:
REM   TITANORBIT_GCE_PROJECT  TITANORBIT_GCE_ZONE  TITANORBIT_GCE_INSTANCE  TITANORBIT_GCE_SSH_USER

cd /d "%~dp0"

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0vm_free_disk_for_server_upload_gce.ps1" %*
exit /b %ERRORLEVEL%
