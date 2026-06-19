@echo off
REM Same as upload_linux_build_to_gce.bat but forces --tunnel-through-iap (fixes many Windows plink timeouts).
REM Usage: same optional args after useIap — e.g. custom folder then project:
REM   upload_linux_build_to_gce_iap.bat "D:\Build\TitanOrbitLinux1" other-gcp-project-id

call "%~dp0upload_linux_build_to_gce.bat" useIap %*
exit /b %errorlevel%
