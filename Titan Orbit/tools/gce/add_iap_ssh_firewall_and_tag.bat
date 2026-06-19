@echo off
setlocal EnableExtensions
REM Thin wrapper: logic is in add_iap_ssh_firewall_and_tag.ps1 (auto-detects VM VPC; per-network rule name).
REM Usage:
REM   add_iap_ssh_firewall_and_tag.bat
REM   add_iap_ssh_firewall_and_tag.bat YOUR_GCP_PROJECT_ID
REM   add_iap_ssh_firewall_and_tag.bat YOUR_GCP_PROJECT_ID YOUR_VPC_NETWORK_NAME

where powershell >nul 2>&1
if errorlevel 1 (
  echo ERROR: PowerShell not found.
  exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0add_iap_ssh_firewall_and_tag.ps1" %*
exit /b %ERRORLEVEL%
