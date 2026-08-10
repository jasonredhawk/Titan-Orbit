@echo off
setlocal

REM One-shot: Unity Linux headless server build + GCE deploy.
REM Close the Unity Editor first (same project cannot be open in GUI + batchmode).
REM
REM Defaults match day-to-day GCE publish: freeDisk useGcs
REM
REM Examples (from tools\gce):
REM   build_and_deploy_server_gce.bat
REM   build_and_deploy_server_gce.bat freeDisk useGcs useIap
REM   build_and_deploy_server_gce.bat buildOnly
REM   build_and_deploy_server_gce.bat deployOnly freeDisk useGcs
REM
REM Optional env:
REM   TITANORBIT_UNITY_EDITOR  Full path to Unity.exe (overrides Hub auto-detect)
REM   UNITY_EDITOR_PATH        Same as TITANORBIT_UNITY_EDITOR

cd /d "%~dp0"

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build_and_deploy_server_gce.ps1" %*
exit /b %ERRORLEVEL%
