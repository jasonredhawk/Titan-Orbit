@echo off
setlocal EnableDelayedExpansion

REM Set Content-Type and Content-Encoding for Unity WebGL objects already in GCS.
REM Run after upload_webgl_to_gcs.bat. Walks the local build tree and updates matching gs:// paths.
REM
REM Usage:
REM   set_webgl_gcs_metadata.bat
REM   set_webgl_gcs_metadata.bat "C:\path\to\TitanOrbitWebGL"
REM   set_webgl_gcs_metadata.bat "C:\path\to\TitanOrbitWebGL" your-gcp-project-id

REM Defaults - keep in sync with upload_webgl_to_gcs.bat
set "BUCKET=titan-orbit-webgl"
set "PROJECT_ID=titan-orbit"
set "SOURCE_DIR=C:\Users\jason\Documents\Titan Orbit\Downloads\TitanOrbitWeb1"

if not "%~1"=="" for %%I in ("%~1") do set "SOURCE_DIR=%%~fI"
if not "%~2"=="" set "PROJECT_ID=%~2"

where gcloud >nul 2>&1
if errorlevel 1 (
  echo ERROR: gcloud was not found in PATH.
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
echo.

set "ERR=0"

REM Brotli-compressed artifacts
for /r "%SRCBASE%" %%F in (*.br) do (
  set "FULL=%%~fF"
  set "REL=!FULL:%SRCBASE%\=!"
  set "GS=gs://%BUCKET%/!REL:\=/!"
  set "NAME=%%~nxF"
  set "CT=application/octet-stream"

  REM Brotli suffix lengths: wasm data json 8 chars, js 6 chars
  if /i "!NAME:~-8!"==".wasm.br" set "CT=application/wasm"
  if /i "!NAME:~-8!"==".data.br" set "CT=application/octet-stream"
  if /i "!NAME:~-6!"==".js.br" set "CT=application/javascript"
  if /i "!NAME:~-8!"==".json.br" set "CT=application/json"

  call :RunUpdate "!GS!" br "!CT!"
)

REM Uncompressed WebGL files (if present)
for /r "%SRCBASE%" %%F in (*.wasm) do (
  set "FULL=%%~fF"
  set "REL=!FULL:%SRCBASE%\=!"
  set "GS=gs://%BUCKET%/!REL:\=/!"
  call :RunUpdate "!GS!" "" "application/wasm"
)
for /r "%SRCBASE%" %%F in (*.js) do (
  set "NAME=%%~nxF"
  if /i not "!NAME:~-6!"==".js.br" (
    set "FULL=%%~fF"
    set "REL=!FULL:%SRCBASE%\=!"
    set "GS=gs://%BUCKET%/!REL:\=/!"
    call :RunUpdate "!GS!" "" "application/javascript"
  )
)
for /r "%SRCBASE%" %%F in (*.data) do (
  set "NAME=%%~nxF"
  if /i not "!NAME:~-8!"==".data.br" (
    set "FULL=%%~fF"
    set "REL=!FULL:%SRCBASE%\=!"
    set "GS=gs://%BUCKET%/!REL:\=/!"
    call :RunUpdate "!GS!" "" "application/octet-stream"
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
REM Use for %%I in ("%~1") so paths expand at call time (reliable when deploy_webgl_gcs.bat call-chains this script).
if "%~2"=="" (
  for %%I in ("%~1") do call gcloud --project "%PROJECT_ID%" storage objects update "%%~I" --clear-content-encoding --content-type="%~3" --continue-on-error
) else (
  for %%I in ("%~1") do call gcloud --project "%PROJECT_ID%" storage objects update "%%~I" --content-encoding="%~2" --content-type="%~3" --continue-on-error
)
if errorlevel 1 set "ERR=1"
exit /b 0
