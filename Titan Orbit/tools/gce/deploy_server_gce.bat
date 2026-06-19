@echo off
setlocal

REM Full deploy pipeline: optional VM disk cleanup, upload, install on VM, restart systemd or hard VM reset.
REM Implemented in deploy_server_gce_pipeline.ps1 (OpenSSH upload path and/or GCS + VM pull).
REM
REM Flags (any order, case-insensitive):  freeDisk   useGcs   aggressive   useIap
REM   freeDisk    Run vm_free_disk_for_server_upload_gce.bat first (clears /tmp etc. on the VM).
REM   useGcs      Run upload_linux_build_to_gcs.bat, then pull tarball from GCS on the VM and extract
REM               (VM default service account needs storage.objectViewer on the bucket — see README).
REM   aggressive  With freeDisk: stronger cleanup (removes whole game install, ~/.cache, etc.). Implied when freeDisk+useGcs.
REM   useIap      IAP for free-disk + GCS-install SSH; legacy match: after deploy runs reset_gce_vm.bat
REM               instead of systemctl restart (same as this .bat before the pipeline split).
REM
REM Then pass through the same arguments as upload_linux_build_to_gce.bat / upload_linux_build_to_gcs.bat:
REM   [build folder] [project id] [bucket when useGcs]   and optional useIap
REM
REM Examples (from tools\gce):
REM   deploy_server_gce.bat
REM   deploy_server_gce.bat freeDisk useGcs useIap
REM   deploy_server_gce.bat useGcs "C:\path\TitanOrbitLinux1" titan-orbit my-bucket
REM   deploy_server_gce.bat "C:\path\TitanOrbitLinux1" titan-orbit useIap

cd /d "%~dp0"

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0deploy_server_gce_pipeline.ps1" %*
exit /b %ERRORLEVEL%
