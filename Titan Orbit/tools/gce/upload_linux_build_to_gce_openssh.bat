@echo off
setlocal
REM Upload Linux headless build using Windows OpenSSH (ssh/scp), not gcloud's PuTTY plink.
REM Defaults match upload_linux_build_to_gce.bat.
REM   upload_linux_build_to_gce_openssh.bat
REM       (tries direct SSH first; if it times out, automatically retries via IAP)
REM   upload_linux_build_to_gce_openssh.bat useIap
REM       (skip direct; IAP only)
REM For more options run PowerShell:
REM   powershell -NoProfile -File "%~dp0upload_linux_build_to_gce_openssh.ps1" -UseIap -SourceDir "D:\path\TitanOrbitLinux1"

if /i "%~1"=="useIap" (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0upload_linux_build_to_gce_openssh.ps1" -UseIap
  exit /b %ERRORLEVEL%
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0upload_linux_build_to_gce_openssh.ps1" %*
exit /b %ERRORLEVEL%
