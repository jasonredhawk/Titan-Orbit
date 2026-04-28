@echo off
REM Tries plain "gcloud compute ssh" first (same as restart_titanorbit_server_on_gce.bat), then IAP
REM (start-iap-tunnel + ssh.exe) if direct SSH fails. Avoids hanging on IAP when the VM is reachable
REM without a tunnel (typical after a successful non-IAP install).
REM For IAP-first behavior: restart_titanorbit_server_on_gce.bat useIap  (omit plainFirst)

call "%~dp0restart_titanorbit_server_on_gce.bat" useIap plainFirst %*
exit /b %errorlevel%
