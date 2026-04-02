@echo off
setlocal

REM One-step deploy: sync WebGL folder to GCS, then fix Content-Type / Content-Encoding for Brotli.
REM Same arguments as upload_webgl_to_gcs.bat / set_webgl_gcs_metadata.bat.
REM
REM Usage:
REM   deploy_webgl_gcs.bat
REM   deploy_webgl_gcs.bat "C:\path\to\TitanOrbitWebGL"
REM   deploy_webgl_gcs.bat "C:\path\to\TitanOrbitWebGL" your-gcp-project-id

call "%~dp0upload_webgl_to_gcs.bat" %*
if errorlevel 1 exit /b 1
call "%~dp0set_webgl_gcs_metadata.bat" %*
exit /b %errorlevel%
