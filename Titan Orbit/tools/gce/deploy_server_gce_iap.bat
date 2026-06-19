@echo off
REM Same as deploy_server_gce.bat but forces IAP for upload, then VM hard reset (reset_gce_vm.bat — not SSH service restart).
REM Recommended on Windows when post-upload SSH restart is unreliable. Uses default build folder and project titan-orbit.
REM For a custom folder + IAP use:
REM   deploy_server_gce.bat "C:\path\to\TitanOrbitLinux1" titan-orbit useIap

call "%~dp0deploy_server_gce.bat" useIap
exit /b %errorlevel%
