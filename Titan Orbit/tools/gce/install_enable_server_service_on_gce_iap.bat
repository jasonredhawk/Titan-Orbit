@echo off
REM Same as install_enable_server_service_on_gce.bat but forces --tunnel-through-iap for gcloud compute ssh.

call "%~dp0install_enable_server_service_on_gce.bat" useIap %*
exit /b %errorlevel%
