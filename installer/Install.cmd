@echo off
rem Double-click entry point. Runs Install.ps1 beside this file, bypassing the
rem execution policy for this one process only (nothing machine-wide changes).
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1" %*
if errorlevel 1 (
  echo.
  echo Install failed - see the message above.
  pause
)
