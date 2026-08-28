@echo off
title j0kers Media Server - Install
rem Double-click entry point. Runs Install.ps1 beside this file, bypassing the
rem execution policy for this one process only (nothing machine-wide changes).
cd /d "%~dp0"

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1" %*
set "rc=%errorlevel%"

echo.
if not "%rc%"=="0" (
  echo Install did not finish - the message above says why.
) 
rem The window is closed by the person reading it, not by the script: a
rem double-clicked installer that vanishes takes its own error message with it.
pause
exit /b %rc%
