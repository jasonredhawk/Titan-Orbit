@echo off
REM Same as deploy_server_gce.bat but forces IAP for upload + restart (recommended on Windows if plink times out).
REM Uses default build folder and project titan-orbit. For a custom folder + IAP use:
REM   deploy_server_gce.bat "C:\path\to\TitanOrbitLinux1" titan-orbit useIap

call "%~dp0deploy_server_gce.bat" useIap
exit /b %errorlevel%
