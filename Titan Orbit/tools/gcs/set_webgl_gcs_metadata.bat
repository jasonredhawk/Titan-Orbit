@echo off
setlocal

REM Set Content-Type and Content-Encoding for Unity WebGL objects already in GCS.
REM Thin wrapper around Set-WebGlGcsMetadata.ps1 (PowerShell) — the old pure-batch
REM for /f encoding detect left a trailing CR so "br" never matched and
REM --clear-content-encoding stripped Brotli headers → WASM memory access OOB at _main.
REM
REM Usage:
REM   set_webgl_gcs_metadata.bat
REM   set_webgl_gcs_metadata.bat "C:\path\to\TitanOrbitWebGL"
REM   set_webgl_gcs_metadata.bat "C:\path\to\TitanOrbitWebGL" your-gcp-project-id

set "SOURCE_DIR=C:\Users\jason\Documents\repo\Titan-Orbit\Titan Orbit\BuildOutput\WebGL\production\TitanOrbitWebGL"
set "PROJECT_ID=titan-orbit"

if not "%~1"=="" for %%I in ("%~1") do set "SOURCE_DIR=%%~fI"
if not "%~2"=="" set "PROJECT_ID=%~2"

where gcloud >nul 2>&1
if errorlevel 1 (
  echo ERROR: gcloud was not found in PATH.
  exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Set-WebGlGcsMetadata.ps1" -SourceDir "%SOURCE_DIR%" -ProjectId "%PROJECT_ID%"
exit /b %ERRORLEVEL%
