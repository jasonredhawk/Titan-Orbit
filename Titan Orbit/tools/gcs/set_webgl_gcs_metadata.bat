@echo off
setlocal EnableDelayedExpansion

REM Set Content-Type and Content-Encoding for Unity WebGL objects already in GCS.
REM Run after upload_webgl_to_gcs.bat. Walks the local build tree and updates matching gs:// paths.
REM Encoding is detected per file (Brotli vs plain) — do NOT blindly set br on every .unityweb.
REM
REM Usage:
REM   set_webgl_gcs_metadata.bat
REM   set_webgl_gcs_metadata.bat "C:\path\to\TitanOrbitWebGL"
REM   set_webgl_gcs_metadata.bat "C:\path\to\TitanOrbitWebGL" your-gcp-project-id

REM Defaults - keep in sync with upload_webgl_to_gcs.bat
set "BUCKET=titan-orbit-webgl"
set "PROJECT_ID=titan-orbit"
set "SOURCE_DIR=C:\Users\jason\Documents\repo\Titan-Orbit\Titan Orbit\BuildOutput\WebGL\production\TitanOrbitWebGL"
set "ENCODING_PS1=%~dp0Get-WebGLEncoding.ps1"

if not "%~1"=="" for %%I in ("%~1") do set "SOURCE_DIR=%%~fI"
if not "%~2"=="" set "PROJECT_ID=%~2"

where gcloud >nul 2>&1
if errorlevel 1 (
  echo ERROR: gcloud was not found in PATH.
  exit /b 1
)

if not exist "%ENCODING_PS1%" (
  echo ERROR: Missing helper script:
  echo   %ENCODING_PS1%
  exit /b 1
)

if "%PROJECT_ID%"=="" (
  for /f "usebackq delims=" %%P in (`call gcloud config get-value project 2^>nul`) do set "PROJECT_ID=%%P"
)
if "%PROJECT_ID%"=="" (
  echo ERROR: Could not determine GCP project id.
  exit /b 1
)

if not exist "%SOURCE_DIR%\index.html" (
  echo ERROR: Source folder invalid or missing index.html:
  echo   %SOURCE_DIR%
  exit /b 1
)

REM Strip trailing backslash from SOURCE_DIR for safe relative-path substitution
set "SRCBASE=%SOURCE_DIR%"
if "%SRCBASE:~-1%"=="\" set "SRCBASE=%SRCBASE:~0,-1%"

echo.
echo Setting metadata for gs://%BUCKET%/ ^(project: %PROJECT_ID%^)
echo Local tree: %SRCBASE%
echo Preflight: powershell -File "%~dp0verify_webgl_build.ps1" "%SRCBASE%"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0verify_webgl_build.ps1" "%SRCBASE%"
echo.

set "ERR=0"

REM All build artifacts under Build\ and root (loader, index, etc.)
for /r "%SRCBASE%" %%F in (*) do (
  if exist "%%F\" (
    rem Skip directories.
  ) else (
    set "FULL=%%~fF"
    set "REL=!FULL:%SRCBASE%\=!"
    set "GS=gs://%BUCKET%/!REL:\=/!"
    set "NAME=%%~nxF"
    set "EXT=%%~xF"

    set "CT="
    set "ENC="

    if /i "!EXT!"==".html" set "CT=text/html"
    if /i "!EXT!"==".css" set "CT=text/css"
    if /i "!EXT!"==".ico" set "CT=image/x-icon"
    if /i "!EXT!"==".png" set "CT=image/png"
    if /i "!EXT!"==".json" set "CT=application/json"
    if /i "!EXT!"==".js" set "CT=application/javascript"
    if /i "!EXT!"==".wasm" set "CT=application/wasm"
    if /i "!EXT!"==".data" set "CT=application/octet-stream"
    if /i "!EXT!"==".unityweb" set "CT=application/octet-stream"
    if /i "!EXT!"==".br" set "CT=application/octet-stream"

    if /i "!NAME:~-14!"==".wasm.unityweb" set "CT=application/wasm"
    if /i "!NAME:~-14!"==".json.unityweb" set "CT=application/json"
    if /i "!NAME:~-12!"==".js.unityweb" set "CT=application/javascript"
    if /i "!NAME:~-8!"==".wasm.br" set "CT=application/wasm"
    if /i "!NAME:~-6!"==".js.br" set "CT=application/javascript"
    if /i "!NAME:~-8!"==".json.br" set "CT=application/json"

    if not "!CT!"=="" (
      for /f "usebackq delims=" %%E in (`powershell -NoProfile -ExecutionPolicy Bypass -File "%ENCODING_PS1%" "!FULL!" 2^>nul`) do set "ENC=%%E"

      if /i "!ENC!"=="br" (
        call :RunUpdate "!GS!" br "!CT!"
      ) else if /i "!ENC!"=="gzip" (
        call :RunUpdate "!GS!" gzip "!CT!"
      ) else (
        call :RunUpdate "!GS!" "" "!CT!"
      )
    )
  )
)

if not "!ERR!"=="0" (
  echo.
  echo Completed with one or more errors.
  exit /b 1
)
echo Metadata pass complete.
exit /b 0

:RunUpdate
REM Args: gs URL, content-encoding or empty, content-type
if "%~2"=="" (
  gcloud --project "%PROJECT_ID%" storage objects update "%~1" --clear-content-encoding --content-type="%~3" --cache-control=no-cache
) else (
  gcloud --project "%PROJECT_ID%" storage objects update "%~1" --content-encoding="%~2" --content-type="%~3" --cache-control=no-cache
)
if errorlevel 1 set "ERR=1"
exit /b 0
