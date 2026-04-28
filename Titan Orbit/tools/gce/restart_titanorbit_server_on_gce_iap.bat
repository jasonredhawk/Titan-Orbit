@echo off
REM IAP-only wrapper: always skip plain/direct SSH probe.
REM This avoids hanging on Windows plink/direct SSH in networks where only IAP is reliable.

call "%~dp0restart_titanorbit_server_on_gce.bat" useIap %*
exit /b %errorlevel%
