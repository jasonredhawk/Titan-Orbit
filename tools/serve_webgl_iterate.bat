@echo off
title Titan Orbit — Fast Iterate server
echo Starting local WebGL server...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0serve_webgl_iterate.ps1"
if errorlevel 1 (
  echo.
  echo Server failed to start. Read the message above.
  pause
)
