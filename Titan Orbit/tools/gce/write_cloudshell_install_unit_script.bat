@echo off
setlocal
REM Writes cloudshell_install_titanorbit_unit.sh — run THAT FILE in Google Cloud Shell (not on Windows).
REM Usage: write_cloudshell_install_unit_script.bat
REM        write_cloudshell_install_unit_script.bat YOUR_PROJECT YOUR_ZONE user@INSTANCE

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0write_cloudshell_install_unit_script.ps1" %*
if errorlevel 1 exit /b 1
echo.
echo Upload "%~dp0cloudshell_install_titanorbit_unit.sh" to Cloud Shell, then:  bash cloudshell_install_titanorbit_unit.sh
exit /b 0
