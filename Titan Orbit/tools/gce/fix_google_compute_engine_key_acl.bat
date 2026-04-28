@echo off
setlocal
REM Fixes Windows ACLs on google_compute_engine so ssh.exe accepts the key (see README: private key permissions).

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0fix_google_compute_engine_key_acl.ps1" %*
exit /b %errorlevel%
