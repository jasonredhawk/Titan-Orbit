@echo off
setlocal

REM One-step deploy: sync WebGL folder to GCS, then fix Content-Type / Content-Encoding for Brotli.
REM Same arguments as upload_webgl_to_gcs.bat / set_webgl_gcs_metadata.bat.
REM
REM Cache busting: if the site fails with "function signature mismatch" or odd WebGL crashes after
REM a new Unity build, the browser may still be serving an OLD .data.unityweb from IndexedDB while
REM loading NEW .js/.wasm. Fix: rename the build output folder (e.g. TitanOrbitWeb2), update the
REM site to load that folder, or clear site data for the origin. UnityCache "revalidated" in the
REM console often means stale data was reused.
REM WebAssembly 2023 / BigInt (Player Settings) needs current Chrome/Edge/Firefox/Safari; very old
REM browsers may fail to instantiate the module.
REM
REM Usage:
REM   deploy_webgl_gcs.bat
REM   deploy_webgl_gcs.bat "C:\path\to\TitanOrbitWebGL"
REM   deploy_webgl_gcs.bat "C:\path\to\TitanOrbitWebGL" your-gcp-project-id

call "%~dp0upload_webgl_to_gcs.bat" %*
if errorlevel 1 exit /b 1
call "%~dp0set_webgl_gcs_metadata.bat" %*
exit /b %errorlevel%
